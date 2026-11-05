using System.Collections.Concurrent;
using System.Data;
using System.Security.Claims;
using Butcem.Api.Infrastructure;
using Dapper;

namespace Butcem.Api.Features;

/// <summary>
/// Kasa tahsilat fişi (Mikro 051100), kasa ödeme fişi (051200), kasadan bankaya nakit yatırma makbuzu (072520),
/// tediye makbuzu havale ile (042210) ve kasa hareket föyü (051400).
///
/// Hepsi yalnız CARI_HESAP_HAREKETLERI'ne, evrak satırı başına bir satır yazar (tediyede ayrıca ODEME_EMIRLERI).
/// Yapı firmanın kendi evraklarından alındı (05.11.2026 kasa tahsilat 523, 04.11.2026 kasa ödeme 428,
/// 04.11.2026 bankaya yatırma 629, 05.11.2026 tediye 247537):
///  - Kasa tahsilat (evrak tipi 2): cha_tip 1 (karşı hesap alacak), cha_cari_cins/cha_kod = karşı hesap
///    (ör. 4 / 200 MAĞAZALAR KASA), cha_kasa_hizmet 4 / cha_kasa_hizkod = fişin kasası. Kasaya giriş.
///  - Kasa ödeme (evrak tipi 65): aynısı, cha_tip 0 (karşı hesap borç; ör. masraf 5 / 3 KİRA GİDERLERİ). Kasadan çıkış.
///  - Bankaya yatırma (evrak tipi 8): cha_tip 0, cha_cari_cins 2 / banka kodu, cha_grupno 1, kasa_hizmet 4 / kasa.
///  - Tediye havale (evrak tipi 64, cinsi 20): cha_tip 0, cari 0 / cari kodu, kasa_hizmet 2 / banka kodu,
///    cha_karsidgrupno 10, cha_trefno = havale emri (FH-000-000-yyyy-nnnnnnnn), cha_sntck_poz 1; ODEME_EMIRLERI'nde
///    tip 7 kart: sahibi banka (grup 10), nerede = cari, son pozisyon 1.
/// Seri boş, sıra evrak tipine göre son numaradan devam eder. Boş alanlar Mikro'nun boş değerleriyle yazılır.
/// </summary>
public static class KasaIslemleri
{
    public enum FisTuru { Tahsilat, Odeme }

    /// <param name="Cins">Karşı hesap cinsi: 0 cari, 4 kasa, 5 masraf.</param>
    public sealed record FisSatiri(int Cins, string Kod, decimal Tutar, string? Aciklama);

    public sealed record FisIstek(FisTuru Tur, string KasaKod, DateTime? Tarih, string? BelgeNo, List<FisSatiri> Satirlar);

    public sealed record YatirmaSatiri(string KasaKod, decimal Tutar, string? Aciklama);

    public sealed record YatirmaIstek(string BankaKod, DateTime? Tarih, string? BelgeNo, List<YatirmaSatiri> Satirlar);

    public sealed record TediyeSatiri(string BankaKod, DateTime? Vade, decimal Tutar, string? Aciklama);

    public sealed record TediyeIstek(string CariKod, DateTime? Tarih, string? BelgeNo, List<TediyeSatiri> Satirlar);

    public sealed record Sonuc(int EvrakTip, int Sira, int SatirSayisi, decimal Toplam, List<string>? RefNolar = null);

    public sealed record Hesap(string Kod, string Ad);

    public sealed record FoyHareketi(Guid Guid, DateTime Tarih, int EvrakTip, string Evrak, string EvrakAdi, string BelgeNo,
                                     string KarsiHesap, string Aciklama, decimal Giris, decimal Cikis, decimal Bakiye);

    public sealed record Foy(Hesap Kasa, DateOnly Bas, DateOnly Bit, decimal Devir, decimal Giris, decimal Cikis, decimal Bakiye,
                             List<FoyHareketi> Hareketler);

    private sealed record HamFoy(Guid Guid, DateTime Tarih, int EvrakTip, int Cinsi, int NormalIade, string Seri, int Sira,
                                 string BelgeNo, int KarsiCins, string KarsiKod, string? KarsiAd, string Aciklama, decimal Giris, decimal Cikis);

    private sealed record BankaHesabi(string Kod, string Ad, string Sube, string Tcmb, string TcmbSube, string TcmbIl);

    /// <summary>Havale emrinde bankanın grup numarası (firmanın tediye 247537 kaydından).</summary>
    private const byte HavaleBankaGrubu = 10;

    /// <summary>Bankaya yatırmada bankanın grup numarası (firmanın 629 numaralı makbuzundan).</summary>
    private const byte YatirmaBankaGrubu = 1;

    private const int HavaleCinsi = 20;

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/kasa").RequireYetki(Yetkiler.Kasa, Yetkiler.Tediye).WithTags("Kasa");

        g.MapGet("/kasalar", (Db db) =>
        {
            using var conn = db.Mikro();
            return Results.Ok(conn.Query<Hesap>("SELECT kas_kod AS Kod, kas_isim AS Ad FROM KASALAR WITH (NOLOCK) WHERE ISNULL(kas_iptal, 0) = 0 ORDER BY kas_kod"));
        });

        g.MapGet("/bankalar", (Db db) =>
        {
            using var conn = db.Mikro();
            return Results.Ok(conn.Query<Hesap>("SELECT ban_kod AS Kod, ban_ismi AS Ad FROM BANKALAR WITH (NOLOCK) WHERE ISNULL(ban_iptal, 0) = 0 ORDER BY ban_kod"));
        });

        // Fiş satırında karşı hesap seçimi: cins 0 cari, 4 kasa, 5 masraf.
        g.MapGet("/hesap-ara", (int cins, string? q, Db db) =>
        {
            using var conn = db.Mikro();
            return Results.Ok(HesapAra(conn, cins, q?.Trim() ?? ""));
        });

        g.MapPost("/fis", (FisIstek istek, ClaimsPrincipal user, MikroYazici yazici, IslemLogu log) =>
        {
            if (istek.Satirlar is not { Count: > 0 }) throw new IslemHatasi("En az bir satır ekleyin.");
            TutarKontrol(istek.Satirlar.Select(s => s.Tutar), istek.Tarih);
            if (istek.Satirlar.Any(s => s.Cins is not (Mikro.CariCins.Cari or Mikro.CariCins.Kasa or Mikro.CariCins.Masraf)))
                throw new IslemHatasi("Satır cinsi cari, kasa ya da masraf olabilir.");
            if (istek.Satirlar.Any(s => s.Cins == Mikro.CariCins.Kasa && s.Kod.Trim() == istek.KasaKod.Trim()))
                throw new IslemHatasi("Fişin kendi kasası satırda karşı hesap olamaz.");
            var sonuc = Fis(yazici, istek);
            log.Yaz(user.Id(), istek.Tur == FisTuru.Tahsilat ? "kasa_tahsilat" : "kasa_odeme", new { istek, sonuc });
            return Results.Ok(sonuc);
        }).RequireYetki(Yetkiler.Kasa);

        g.MapPost("/bankaya-yatirma", (YatirmaIstek istek, ClaimsPrincipal user, MikroYazici yazici, IslemLogu log) =>
        {
            if (istek.Satirlar is not { Count: > 0 }) throw new IslemHatasi("En az bir kasa satırı ekleyin.");
            TutarKontrol(istek.Satirlar.Select(s => s.Tutar), istek.Tarih);
            var sonuc = BankayaYatirma(yazici, istek);
            log.Yaz(user.Id(), "kasadan_bankaya", new { istek, sonuc });
            return Results.Ok(sonuc);
        }).RequireYetki(Yetkiler.Kasa);

        g.MapPost("/tediye", (TediyeIstek istek, ClaimsPrincipal user, MikroYazici yazici, IslemLogu log) =>
        {
            if (istek.Satirlar is not { Count: > 0 }) throw new IslemHatasi("En az bir ödeme satırı ekleyin.");
            TutarKontrol(istek.Satirlar.Select(s => s.Tutar), istek.Tarih);
            var sonuc = Tediye(yazici, istek);
            log.Yaz(user.Id(), "tediye", new { istek, sonuc });
            return Results.Ok(sonuc);
        }).RequireYetki(Yetkiler.Tediye);

        // Kasa hareket föyü: kasanın fiş kasası (cha_kasa_hizkod) ya da karşı hesap (cha_kod) olduğu bütün hareketler.
        g.MapGet("/foy", (string kasa, DateOnly? bas, DateOnly? bit, Db db) =>
        {
            var b = bas ?? DateOnly.FromDateTime(DateTime.Today.AddDays(-30));
            var e = bit ?? DateOnly.FromDateTime(DateTime.Today);
            if (e < b) throw new IslemHatasi("Bitiş tarihi başlangıçtan önce olamaz.");
            using var conn = db.Mikro();
            var k = conn.QueryFirstOrDefault<Hesap>("SELECT kas_kod AS Kod, kas_isim AS Ad FROM KASALAR WITH (NOLOCK) WHERE kas_kod = @kasa", new { kasa })
                    ?? throw new IslemHatasi($"Kasa bulunamadı: {kasa}");
            var p = new { kasa = k.Kod, b = b.ToDateTime(TimeOnly.MinValue), e = e.ToDateTime(TimeOnly.MinValue) };
            // Fiş kasası tarafında yön karşı hesabın tersidir: karşı hesap alacak (cha_tip 1) ise kasaya giriş.
            const string hareketSql = """
                SELECT h.cha_Guid AS Guid, h.cha_tarihi AS Tarih, CAST(h.cha_evrak_tip AS int) AS EvrakTip, CAST(h.cha_cinsi AS int) AS Cinsi,
                       CAST(h.cha_normal_Iade AS int) AS NormalIade, h.cha_evrakno_seri AS Seri, h.cha_evrakno_sira AS Sira, h.cha_belge_no AS BelgeNo,
                       CAST(CASE WHEN h.cha_kasa_hizmet = 4 AND h.cha_kasa_hizkod = @kasa THEN h.cha_cari_cins ELSE h.cha_kasa_hizmet END AS int) AS KarsiCins,
                       CASE WHEN h.cha_kasa_hizmet = 4 AND h.cha_kasa_hizkod = @kasa THEN h.cha_kod ELSE h.cha_kasa_hizkod END AS KarsiKod,
                       CAST(NULL AS nvarchar(200)) AS KarsiAd, h.cha_aciklama AS Aciklama,
                       CAST(CASE WHEN (h.cha_kasa_hizmet = 4 AND h.cha_kasa_hizkod = @kasa AND h.cha_tip = 1)
                                   OR (h.cha_cari_cins = 4 AND h.cha_kod = @kasa AND h.cha_tip = 0) THEN h.cha_meblag ELSE 0 END AS decimal(18,2)) AS Giris,
                       CAST(CASE WHEN (h.cha_kasa_hizmet = 4 AND h.cha_kasa_hizkod = @kasa AND h.cha_tip = 0)
                                   OR (h.cha_cari_cins = 4 AND h.cha_kod = @kasa AND h.cha_tip = 1) THEN h.cha_meblag ELSE 0 END AS decimal(18,2)) AS Cikis
                FROM CARI_HESAP_HAREKETLERI h WITH (NOLOCK)
                WHERE ISNULL(h.cha_iptal, 0) = 0
                  AND ((h.cha_kasa_hizmet = 4 AND h.cha_kasa_hizkod = @kasa) OR (h.cha_cari_cins = 4 AND h.cha_kod = @kasa))
                """;
            var devir = conn.ExecuteScalar<decimal?>($"SELECT SUM(Giris - Cikis) FROM ({hareketSql} AND h.cha_tarihi < @b) x", p) ?? 0m;
            var ham = conn.Query<HamFoy>($"{hareketSql} AND h.cha_tarihi >= @b AND h.cha_tarihi <= @e ORDER BY h.cha_tarihi, h.cha_create_date, h.cha_satir_no", p).ToList();
            var adlar = KarsiAdlari(conn, ham.Select(x => (x.KarsiCins, x.KarsiKod)).Distinct());
            var bakiye = devir;
            var hareketler = ham.Select(x =>
            {
                bakiye += x.Giris - x.Cikis;
                adlar.TryGetValue((x.KarsiCins, x.KarsiKod), out var ad);
                return new FoyHareketi(x.Guid, x.Tarih, x.EvrakTip, string.IsNullOrWhiteSpace(x.Seri) ? x.Sira.ToString() : $"{x.Seri}-{x.Sira}",
                    Mikro.CariEvrakAdi(x.EvrakTip, x.Cinsi, x.NormalIade), x.BelgeNo, string.IsNullOrWhiteSpace(ad) ? x.KarsiKod : $"{x.KarsiKod} {ad}",
                    x.Aciklama, x.Giris, x.Cikis, bakiye);
            }).ToList();
            return Results.Ok(new Foy(k, b, e, devir, hareketler.Sum(x => x.Giris), hareketler.Sum(x => x.Cikis), bakiye, hareketler));
        }).RequireYetki(Yetkiler.Kasa);
    }

    private static void TutarKontrol(IEnumerable<decimal> tutarlar, DateTime? tarih)
    {
        var t = tutarlar.ToList();
        if (t.Count > 200) throw new IslemHatasi("Bir evrakta en fazla 200 satır olabilir.");
        if (t.Any(x => x <= 0)) throw new IslemHatasi("Tutar sıfırdan büyük olmalı.");
        if (tarih is { } d && d.Date > DateTime.Today) throw new IslemHatasi("İleri tarihli evrak girilemez.");
    }

    public static Sonuc Fis(MikroYazici yazici, FisIstek istek) => yazici.Calistir((conn, tx) =>
    {
        var tarih = (istek.Tarih ?? DateTime.Today).Date;
        var kasa = KasaGetir(conn, tx, istek.KasaKod);
        var evrakTip = istek.Tur == FisTuru.Tahsilat ? Mikro.CariEvrakTip.KasaTahsilatFisi : Mikro.CariEvrakTip.KasaOdemeFisi;
        var sira = SonrakiSira(yazici, conn, tx, evrakTip);
        var altKur = yazici.AlternatifKur(conn, tx);
        var simdi = MikroEvrak.Simdi();
        for (var i = 0; i < istek.Satirlar.Count; i++)
        {
            var s = istek.Satirlar[i];
            var kod = HesapKontrol(conn, tx, s.Cins, s.Kod);
            yazici.Ekle(conn, tx, "CARI_HESAP_HAREKETLERI", Satir(yazici, simdi, evrakTip, sira, i, tarih, istek.BelgeNo, s.Tutar, s.Aciklama, altKur, new()
            {
                ["cha_tip"] = (byte)(istek.Tur == FisTuru.Tahsilat ? Mikro.CariHarTip.Alacak : Mikro.CariHarTip.Borc),
                ["cha_cari_cins"] = (byte)s.Cins,
                ["cha_kod"] = kod,
                ["cha_kasa_hizmet"] = (byte)Mikro.CariCins.Kasa,
                ["cha_kasa_hizkod"] = kasa.Kod,
            }));
        }
        return new Sonuc(evrakTip, sira, istek.Satirlar.Count, istek.Satirlar.Sum(s => s.Tutar));
    });

    public static Sonuc BankayaYatirma(MikroYazici yazici, YatirmaIstek istek) => yazici.Calistir((conn, tx) =>
    {
        var tarih = (istek.Tarih ?? DateTime.Today).Date;
        var banka = BankaGetir(conn, tx, istek.BankaKod);
        var sira = SonrakiSira(yazici, conn, tx, Mikro.CariEvrakTip.KasadanBankayaYatirma);
        var altKur = yazici.AlternatifKur(conn, tx);
        var simdi = MikroEvrak.Simdi();
        for (var i = 0; i < istek.Satirlar.Count; i++)
        {
            var s = istek.Satirlar[i];
            var kasa = KasaGetir(conn, tx, s.KasaKod);
            yazici.Ekle(conn, tx, "CARI_HESAP_HAREKETLERI", Satir(yazici, simdi, Mikro.CariEvrakTip.KasadanBankayaYatirma, sira, i, tarih,
                istek.BelgeNo, s.Tutar, s.Aciklama, altKur, new()
                {
                    ["cha_tip"] = (byte)Mikro.CariHarTip.Borc,
                    ["cha_cari_cins"] = (byte)Mikro.CariCins.Banka,
                    ["cha_kod"] = banka.Kod,
                    ["cha_grupno"] = YatirmaBankaGrubu,
                    ["cha_kasa_hizmet"] = (byte)Mikro.CariCins.Kasa,
                    ["cha_kasa_hizkod"] = kasa.Kod,
                }));
        }
        return new Sonuc(Mikro.CariEvrakTip.KasadanBankayaYatirma, sira, istek.Satirlar.Count, istek.Satirlar.Sum(s => s.Tutar));
    });

    public static Sonuc Tediye(MikroYazici yazici, TediyeIstek istek) => yazici.Calistir((conn, tx) =>
    {
        var tarih = (istek.Tarih ?? DateTime.Today).Date;
        var cari = MikroEvrak.Cari(conn, tx, istek.CariKod);
        var sira = SonrakiSira(yazici, conn, tx, Mikro.CariEvrakTip.TediyeMakbuzu);
        var altKur = yazici.AlternatifKur(conn, tx);
        var firmaUnvani = conn.ExecuteScalar<string>("SELECT TOP 1 fir_unvan FROM FIRMALAR WITH (NOLOCK)", transaction: tx) ?? "";
        var refNolar = new List<string>();
        for (var i = 0; i < istek.Satirlar.Count; i++)
        {
            var s = istek.Satirlar[i];
            var banka = BankaGetir(conn, tx, s.BankaKod);
            var vade = (s.Vade ?? tarih).Date;
            var refNo = SonrakiRefNo(conn, tx, tarih.Year);
            var simdi = MikroEvrak.Simdi();
            yazici.Ekle(conn, tx, "ODEME_EMIRLERI", new Dictionary<string, object?>
            {
                ["sck_Guid"] = MikroYazici.YeniGuid(),
                ["sck_fileid"] = (short)54,
                ["sck_create_user"] = yazici.KullaniciNo,
                ["sck_create_date"] = simdi,
                ["sck_lastup_user"] = yazici.KullaniciNo,
                ["sck_lastup_date"] = simdi,
                ["sck_tip"] = (byte)Mikro.CekTip.FirmaHavaleEmri,
                ["sck_refno"] = refNo,
                ["sck_bankano"] = banka.Kod,
                ["sck_borclu"] = MikroEvrak.Kes(firmaUnvani, 50),
                ["sck_vade"] = vade,
                ["sck_tutar"] = (double)s.Tutar,
                ["sck_banka_adres1"] = banka.Ad,
                ["sck_sube_adres2"] = banka.Sube,
                ["sck_sahip_cari_cins"] = (byte)Mikro.CariCins.Banka,
                ["sck_sahip_cari_kodu"] = banka.Kod,
                ["sck_sahip_cari_grupno"] = HavaleBankaGrubu,
                ["sck_nerede_cari_kodu"] = cari.Kod,
                ["sck_ilk_hareket_tarihi"] = tarih,
                ["sck_ilk_evrak_sira_no"] = sira,
                ["sck_ilk_evrak_satir_no"] = i,
                ["sck_son_hareket_tarihi"] = tarih,
                ["sck_doviz_kur"] = 1d,
                ["sck_sonpoz"] = (byte)1,
                ["Sck_TCMB_Banka_kodu"] = banka.Tcmb,
                ["Sck_TCMB_Sube_kodu"] = banka.TcmbSube,
                ["Sck_TCMB_il_kodu"] = banka.TcmbIl,
            });
            var d = Satir(yazici, simdi, Mikro.CariEvrakTip.TediyeMakbuzu, sira, i, tarih, istek.BelgeNo, s.Tutar, s.Aciklama, altKur, new()
            {
                ["cha_tip"] = (byte)Mikro.CariHarTip.Borc,
                ["cha_cinsi"] = (byte)HavaleCinsi,
                ["cha_cari_cins"] = (byte)Mikro.CariCins.Cari,
                ["cha_kod"] = cari.Kod,
                ["cha_kasa_hizmet"] = (byte)Mikro.CariCins.Banka,
                ["cha_kasa_hizkod"] = banka.Kod,
                ["cha_karsidgrupno"] = HavaleBankaGrubu,
                ["cha_trefno"] = refNo,
                ["cha_sntck_poz"] = (byte)1,
            });
            d["cha_vade"] = int.Parse(vade.ToString("yyyyMMdd"));
            yazici.Ekle(conn, tx, "CARI_HESAP_HAREKETLERI", d);
            refNolar.Add(refNo);
        }
        return new Sonuc(Mikro.CariEvrakTip.TediyeMakbuzu, sira, istek.Satirlar.Count, istek.Satirlar.Sum(s => s.Tutar), refNolar);
    });

    private static int SonrakiSira(MikroYazici yazici, IDbConnection conn, IDbTransaction tx, int evrakTip) =>
        yazici.SonrakiSira(conn, tx, "CARI_HESAP_HAREKETLERI", "cha_evrak_tip", evrakTip, "cha_evrakno_seri", "", "cha_evrakno_sira");

    private static Dictionary<string, object?> Satir(MikroYazici yazici, DateTime simdi, int evrakTip, int sira, int satir, DateTime tarih,
        string? belgeNo, decimal tutar, string? aciklama, double altKur, Dictionary<string, object?> ek)
    {
        var d = new Dictionary<string, object?>
        {
            ["cha_Guid"] = MikroYazici.YeniGuid(),
            ["cha_fileid"] = (short)51,
            ["cha_create_user"] = yazici.KullaniciNo,
            ["cha_create_date"] = simdi,
            ["cha_lastup_user"] = yazici.KullaniciNo,
            ["cha_lastup_date"] = simdi,
            ["cha_evrak_tip"] = (byte)evrakTip,
            ["cha_evrakno_seri"] = "",
            ["cha_evrakno_sira"] = sira,
            ["cha_satir_no"] = satir,
            ["cha_tarihi"] = tarih,
            ["cha_belge_no"] = MikroEvrak.Kes(belgeNo?.Trim(), 50),
            ["cha_belge_tarih"] = tarih,
            ["cha_aciklama"] = MikroEvrak.Kes(aciklama?.Trim(), 40),
            ["cha_d_kur"] = 1d,
            ["cha_altd_kur"] = altKur,
            ["cha_karsid_kur"] = 1d,
            ["cha_meblag"] = (double)tutar,
            ["cha_aratoplam"] = (double)tutar,
            ["cha_vade"] = int.Parse(tarih.ToString("yyyyMMdd")),
        };
        foreach (var (k, v) in ek) d[k] = v;
        return d;
    }

    /// <summary>FH-000-000-2026-00000106 biçiminde; yıl içinde sıra.</summary>
    private static string SonrakiRefNo(IDbConnection conn, IDbTransaction tx, int yil)
    {
        var bas = $"FH-000-000-{yil}-";
        var son = conn.ExecuteScalar<string?>(
            "SELECT MAX(sck_refno) FROM ODEME_EMIRLERI WITH (UPDLOCK, HOLDLOCK) WHERE sck_tip = @tip AND sck_refno LIKE @desen",
            new { tip = Mikro.CekTip.FirmaHavaleEmri, desen = bas + "[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]" }, tx);
        var n = son is null ? 1 : int.Parse(son[^8..]) + 1;
        return $"{bas}{n:D8}";
    }

    private static Hesap KasaGetir(IDbConnection conn, IDbTransaction tx, string kod) =>
        conn.QueryFirstOrDefault<Hesap>("SELECT kas_kod AS Kod, kas_isim AS Ad FROM KASALAR WITH (NOLOCK) WHERE kas_kod = @kod",
            new { kod = kod.Trim() }, tx) ?? throw new IslemHatasi($"Kasa bulunamadı: {kod}");

    private static BankaHesabi BankaGetir(IDbConnection conn, IDbTransaction tx, string kod) =>
        conn.QueryFirstOrDefault<BankaHesabi>("""
            SELECT ban_kod AS Kod, ban_ismi AS Ad, ban_sube AS Sube, ban_TCMB_Kodu AS Tcmb,
                   ban_TCMB_Sube_Kodu AS TcmbSube, ban_TCMB_Il_kodu AS TcmbIl
            FROM BANKALAR WITH (NOLOCK) WHERE ban_kod = @kod
            """, new { kod = kod.Trim() }, tx) ?? throw new IslemHatasi($"Banka hesabı bulunamadı: {kod}");

    /// <summary>Karşı hesabın varlığını doğrular, kodunu döndürür.</summary>
    private static string HesapKontrol(IDbConnection conn, IDbTransaction tx, int cins, string kod)
    {
        kod = kod.Trim();
        var var_ = cins switch
        {
            Mikro.CariCins.Cari => conn.ExecuteScalar<int>("SELECT COUNT(*) FROM CARI_HESAPLAR WITH (NOLOCK) WHERE cari_kod = @kod", new { kod }, tx) > 0,
            Mikro.CariCins.Kasa => conn.ExecuteScalar<int>("SELECT COUNT(*) FROM KASALAR WITH (NOLOCK) WHERE kas_kod = @kod", new { kod }, tx) > 0,
            Mikro.CariCins.Masraf => MasrafVarMi(conn, tx, kod),
            _ => false,
        };
        return var_ ? kod : throw new IslemHatasi($"{CinsAdi(cins)} hesabı bulunamadı: {kod}");
    }

    private static string CinsAdi(int cins) => cins switch { 0 => "Cari", 2 => "Banka", 4 => "Kasa", 5 => "Masraf", _ => $"Cins {cins}" };

    private static List<Hesap> HesapAra(IDbConnection conn, int cins, string q)
    {
        var p = new { bas = q + "%", desen = "%" + q + "%" };
        switch (cins)
        {
            case Mikro.CariCins.Cari:
                if (q.Length < 2) return [];
                return conn.Query<Hesap>("""
                    SELECT TOP 20 cari_kod AS Kod, cari_unvan1 AS Ad FROM CARI_HESAPLAR WITH (NOLOCK)
                    WHERE cari_kod LIKE @bas OR cari_unvan1 LIKE @desen ORDER BY cari_kod
                    """, p).ToList();
            case Mikro.CariCins.Kasa:
                return conn.Query<Hesap>("""
                    SELECT kas_kod AS Kod, kas_isim AS Ad FROM KASALAR WITH (NOLOCK)
                    WHERE kas_kod LIKE @bas OR kas_isim LIKE @desen ORDER BY kas_kod
                    """, p).ToList();
            case Mikro.CariCins.Masraf:
                return Masraflar(conn, null).Where(h => q == "" || h.Kod.StartsWith(q, StringComparison.OrdinalIgnoreCase)
                                                        || h.Ad.Contains(q, StringComparison.CurrentCultureIgnoreCase)).Take(50).ToList();
            default:
                throw new IslemHatasi("Satır cinsi cari, kasa ya da masraf olabilir.");
        }
    }

    // Masraf kartlarının tablosu (Mikro'da masraf hesapları) kurulumda adıyla bilinmiyor; *MASRAF* adlı, _kod ve _isim
    // kolonlu tablo aranır. Bulunamazsa daha önce kasa fişlerinde kullanılmış masraf kodları listelenir.
    private static readonly ConcurrentDictionary<string, (string Tablo, string Kod, string Isim)?> MasrafTablosu = new();

    private static (string Tablo, string Kod, string Isim)? MasrafTablosuBul(IDbConnection conn, IDbTransaction? tx) =>
        MasrafTablosu.GetOrAdd("x", _ =>
        {
            var r = conn.QueryFirstOrDefault<(string, string, string)?>("""
                SELECT TOP 1 t.name, k.name, i.name
                FROM sys.tables t
                JOIN sys.columns k ON k.object_id = t.object_id AND k.name LIKE '%[_]kod' AND k.name NOT LIKE '%muh%'
                JOIN sys.columns i ON i.object_id = t.object_id AND (i.name LIKE '%[_]isim' OR i.name LIKE '%[_]ismi' OR i.name LIKE '%[_]adi')
                WHERE t.name LIKE '%MASRAF%' AND t.name NOT LIKE '%HAREKET%'
                ORDER BY CASE WHEN t.name LIKE 'MASRAF[_]HESAP%' THEN 0 WHEN t.name LIKE 'MASRAF%' THEN 1 ELSE 2 END, LEN(t.name)
                """, transaction: tx);
            return r;
        });

    private static List<Hesap> Masraflar(IDbConnection conn, IDbTransaction? tx)
    {
        var t = MasrafTablosuBul(conn, tx);
        var kullanilan = conn.Query<string>(
            "SELECT DISTINCT cha_kod FROM CARI_HESAP_HAREKETLERI WITH (NOLOCK) WHERE cha_cari_cins = 5 AND cha_kod <> ''", transaction: tx).ToList();
        var kartlar = t is { } x
            ? conn.Query<Hesap>($"SELECT CAST([{x.Kod}] AS nvarchar(50)) AS Kod, CAST([{x.Isim}] AS nvarchar(200)) AS Ad FROM [{x.Tablo}] WITH (NOLOCK)", transaction: tx).ToList()
            : [];
        return kartlar.Concat(kullanilan.Where(k => !kartlar.Any(c => c.Kod == k)).Select(k => new Hesap(k, "")))
                      .OrderBy(h => h.Kod).ToList();
    }

    private static bool MasrafVarMi(IDbConnection conn, IDbTransaction tx, string kod) => Masraflar(conn, tx).Any(h => h.Kod == kod);

    private static Dictionary<(int, string), string> KarsiAdlari(IDbConnection conn, IEnumerable<(int Cins, string Kod)> hesaplar)
    {
        var d = new Dictionary<(int, string), string>();
        var liste = hesaplar.ToList();
        void Ekle(int cins, string sql)
        {
            var kodlar = liste.Where(h => h.Cins == cins).Select(h => h.Kod).ToArray();
            if (kodlar.Length == 0) return;
            foreach (var h in conn.Query<Hesap>(sql, new { kodlar })) d[(cins, h.Kod)] = h.Ad;
        }
        Ekle(0, "SELECT cari_kod AS Kod, cari_unvan1 AS Ad FROM CARI_HESAPLAR WITH (NOLOCK) WHERE cari_kod IN @kodlar");
        Ekle(2, "SELECT ban_kod AS Kod, ban_ismi AS Ad FROM BANKALAR WITH (NOLOCK) WHERE ban_kod IN @kodlar");
        Ekle(4, "SELECT kas_kod AS Kod, kas_isim AS Ad FROM KASALAR WITH (NOLOCK) WHERE kas_kod IN @kodlar");
        if (liste.Any(h => h.Cins == 5))
            foreach (var h in Masraflar(conn, null).Where(h => h.Ad != "")) d[(5, h.Kod)] = h.Ad;
        return d;
    }
}
