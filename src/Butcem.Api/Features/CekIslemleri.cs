using System.Data;
using System.Security.Claims;
using Butcem.Api.Infrastructure;
using Dapper;

namespace Butcem.Api.Features;

/// <summary>
/// Çek giriş ve çıkış bordroları. Mikro'da: Çek/Senet > Müşteri çeki giriş bordrosu ve Çek çıkış bordrosu (cariye).
///
/// Giriş (müşteri çeki, evrak tipi 4): her çek için bir CARI_HESAP_HAREKETLERI satırı (cari alacak) ve bir
/// ODEME_EMIRLERI kartı (tip 0, ÇEK KASASI'nda, portföyde).
/// Çıkış (evrak tipi 67): her çek için bir cari borç satırı. Portföydeki müşteri çeki ciro edilir (kart
/// güncellenir: nerede = cari, pozisyon 1); kendi çekimiz yazılırsa yeni kart açılır (tip 2, banka hesabından).
/// Mikro bu bordrolarda başka tablo yazmıyor (MC-10, 11, 17, 18, FC-96, 99 bordroları ile karşılaştırıldı).
/// </summary>
public static class CekIslemleri
{
    public sealed record GirisCeki(DateTime Vade, decimal Tutar, string? Borclu, string? CekNo, string? Banka,
                                   string? Sube, string? HesapNo);

    public sealed record GirisIstek(string CariKod, DateTime? Tarih, List<GirisCeki> Cekler);

    public sealed record FirmaCeki(string BankaKod, DateTime Vade, decimal Tutar, string? CekNo);

    public sealed record CikisIstek(string CariKod, DateTime? Tarih, List<string>? MusteriCekleri, List<FirmaCeki>? FirmaCekleri);

    public sealed record Sonuc(int EvrakTip, int Sira, List<string> RefNolar, decimal Tutar);

    public sealed record PortfoyCeki(string RefNo, string Borclu, string SahipCari, DateTime Vade, decimal Tutar, string? CekNo);

    public sealed record Banka(string Kod, string Ad, string? Sube);

    private sealed record CekKarti(DateTime Vade, double Tutar, string Srm, int Poz, int NeredeCins, string Nerede);

    private sealed record BankaHesabi(string Kod, string Ad, string Sube, string Tcmb, string TcmbSube, string TcmbIl);

    private const string CekKasasiVarsayilan = "ÇEK KASASI";

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/cek").RequireYetki(Yetkiler.CekGiris, Yetkiler.CekCikis).WithTags("Çek");

        // Çıkış bordrosunda seçilecek, portföydeki (çek kasasındaki) müşteri çekleri.
        g.MapGet("/portfoy", (Db db, IConfiguration config) =>
        {
            using var conn = db.Mikro();
            return Results.Ok(conn.Query<PortfoyCeki>("""
                SELECT sck_refno AS RefNo, sck_borclu AS Borclu, sck_sahip_cari_kodu AS SahipCari, sck_vade AS Vade,
                       CAST(sck_tutar AS decimal(18,2)) AS Tutar, NULLIF(sck_no, '') AS CekNo
                FROM ODEME_EMIRLERI WITH (NOLOCK)
                WHERE sck_tip = 0 AND sck_sonpoz = 0 AND sck_nerede_cari_cins = 4 AND sck_nerede_cari_kodu = @kasa
                ORDER BY sck_vade
                """, new { kasa = CekKasasi(config) }));
        }).RequireYetki(Yetkiler.CekCikis);

        g.MapGet("/bankalar", (Db db) =>
        {
            using var conn = db.Mikro();
            return Results.Ok(conn.Query<Banka>(
                "SELECT ban_kod AS Kod, ban_ismi AS Ad, NULLIF(ban_sube, '') AS Sube FROM BANKALAR WITH (NOLOCK) ORDER BY ban_kod"));
        }).RequireYetki(Yetkiler.CekCikis);

        g.MapPost("/giris", (GirisIstek istek, ClaimsPrincipal user, MikroYazici yazici, IConfiguration config, IslemLogu log) =>
        {
            if (istek.Cekler is not { Count: > 0 }) throw new IslemHatasi("En az bir çek ekleyin.");
            if (istek.Cekler.Any(c => c.Tutar <= 0)) throw new IslemHatasi("Çek tutarı sıfırdan büyük olmalı.");
            TarihKontrol(istek.Tarih);
            var sonuc = Giris(yazici, CekKasasi(config), istek);
            log.Yaz(user.Id(), "cek_giris", new { istek, sonuc });
            return Results.Ok(sonuc);
        }).RequireYetki(Yetkiler.CekGiris);

        g.MapPost("/cikis", (CikisIstek istek, ClaimsPrincipal user, MikroYazici yazici, IConfiguration config, IslemLogu log) =>
        {
            var mc = istek.MusteriCekleri ?? [];
            var fc = istek.FirmaCekleri ?? [];
            if (mc.Count + fc.Count == 0) throw new IslemHatasi("En az bir çek seçin ya da yazın.");
            if (mc.Distinct().Count() != mc.Count) throw new IslemHatasi("Aynı çek iki kez seçilmiş.");
            if (fc.Any(c => c.Tutar <= 0)) throw new IslemHatasi("Çek tutarı sıfırdan büyük olmalı.");
            TarihKontrol(istek.Tarih);
            var sonuc = Cikis(yazici, CekKasasi(config), istek with { MusteriCekleri = mc, FirmaCekleri = fc });
            log.Yaz(user.Id(), "cek_cikis", new { istek, sonuc });
            return Results.Ok(sonuc);
        }).RequireYetki(Yetkiler.CekCikis);
    }

    private static string CekKasasi(IConfiguration config) => config["Yazma:CekKasasi"] ?? CekKasasiVarsayilan;

    private static void TarihKontrol(DateTime? tarih)
    {
        if (tarih is { } t && t.Date > DateTime.Today) throw new IslemHatasi("İleri tarihli bordro girilemez.");
    }

    public static Sonuc Giris(MikroYazici yazici, string cekKasasi, GirisIstek istek) => yazici.Calistir((conn, tx) =>
    {
        var tarih = (istek.Tarih ?? DateTime.Today).Date;
        var cari = MikroEvrak.Cari(conn, tx, istek.CariKod);
        var sira = yazici.SonrakiSira(conn, tx, "CARI_HESAP_HAREKETLERI", "cha_evrak_tip", Mikro.CariEvrakTip.MusteriCekGirisBordrosu,
                                      "cha_evrakno_seri", "", "cha_evrakno_sira");
        var altKur = yazici.AlternatifKur(conn, tx);
        var refNolar = new List<string>();

        for (var i = 0; i < istek.Cekler.Count; i++)
        {
            var c = istek.Cekler[i];
            var refNo = SonrakiRefNo(conn, tx, "MC", Mikro.CekTip.MusteriCeki, tarih.Year);
            refNolar.Add(refNo);
            var simdi = MikroEvrak.Simdi();

            yazici.Ekle(conn, tx, "ODEME_EMIRLERI", new Dictionary<string, object?>
            {
                ["sck_Guid"] = MikroYazici.YeniGuid(),
                ["sck_fileid"] = (short)54,
                ["sck_create_user"] = yazici.KullaniciNo,
                ["sck_create_date"] = simdi,
                ["sck_lastup_user"] = yazici.KullaniciNo,
                ["sck_lastup_date"] = simdi,
                ["sck_tip"] = (byte)Mikro.CekTip.MusteriCeki,
                ["sck_refno"] = refNo,
                ["sck_borclu"] = MikroEvrak.Kes(string.IsNullOrWhiteSpace(c.Borclu) ? cari.Unvan : c.Borclu.Trim(), 50),
                ["sck_vdaire_no"] = MikroEvrak.Kes($"{cari.VergiDairesi} {cari.VergiNo}", 80),
                ["sck_vade"] = c.Vade.Date,
                ["sck_tutar"] = (double)c.Tutar,
                ["sck_banka_adres1"] = MikroEvrak.Kes(c.Banka?.Trim(), 100),
                ["sck_sube_adres2"] = MikroEvrak.Kes(c.Sube?.Trim(), 100),
                ["sck_hesapno_sehir"] = MikroEvrak.Kes(c.HesapNo?.Trim(), 60),
                ["sck_no"] = MikroEvrak.Kes(c.CekNo?.Trim(), 50),
                ["sck_sahip_cari_kodu"] = cari.Kod,
                ["sck_nerede_cari_cins"] = (byte)Mikro.CariCins.Kasa,
                ["sck_nerede_cari_kodu"] = cekKasasi,
                ["sck_ilk_hareket_tarihi"] = tarih,
                ["sck_ilk_evrak_sira_no"] = sira,
                ["sck_ilk_evrak_satir_no"] = i,
                ["sck_son_hareket_tarihi"] = tarih,
                ["sck_doviz_kur"] = 1d,
                ["sck_sonpoz"] = (byte)0,
            });

            yazici.Ekle(conn, tx, "CARI_HESAP_HAREKETLERI", CariSatiri(yazici, simdi, Mikro.CariEvrakTip.MusteriCekGirisBordrosu,
                sira, i, tarih, cari.Kod, altKur, c.Tutar, c.Vade, refNo, new()
                {
                    ["cha_tip"] = (byte)Mikro.CariHarTip.Alacak,
                    ["cha_cinsi"] = (byte)1,          // müşteri çeki
                    ["cha_kasa_hizmet"] = (byte)Mikro.CariCins.Kasa,
                    ["cha_kasa_hizkod"] = cekKasasi,
                }));
        }
        return new Sonuc(Mikro.CariEvrakTip.MusteriCekGirisBordrosu, sira, refNolar, istek.Cekler.Sum(c => c.Tutar));
    });

    public static Sonuc Cikis(MikroYazici yazici, string cekKasasi, CikisIstek istek) => yazici.Calistir((conn, tx) =>
    {
        var tarih = (istek.Tarih ?? DateTime.Today).Date;
        var cari = MikroEvrak.Cari(conn, tx, istek.CariKod);
        var sira = yazici.SonrakiSira(conn, tx, "CARI_HESAP_HAREKETLERI", "cha_evrak_tip", Mikro.CariEvrakTip.CekCikisBordrosu,
                                      "cha_evrakno_seri", "", "cha_evrakno_sira");
        var altKur = yazici.AlternatifKur(conn, tx);
        var refNolar = new List<string>();
        var toplam = 0m;
        var satir = 0;

        foreach (var refNo in istek.MusteriCekleri!)
        {
            // Kartı kilitleyerek oku: aynı anda başka biri ciro etmesin.
            var cek = conn.QueryFirstOrDefault<CekKarti>("""
                SELECT sck_vade AS Vade, sck_tutar AS Tutar, sck_srmmrk AS Srm, CAST(sck_sonpoz AS int) AS Poz,
                       CAST(sck_nerede_cari_cins AS int) AS NeredeCins, sck_nerede_cari_kodu AS Nerede
                FROM ODEME_EMIRLERI WITH (UPDLOCK, ROWLOCK) WHERE sck_tip = 0 AND sck_refno = @refNo
                """, new { refNo }, tx)
                ?? throw new IslemHatasi($"Çek bulunamadı: {refNo}");
            if (cek.Poz != 0 || cek.NeredeCins != Mikro.CariCins.Kasa || cek.Nerede != cekKasasi)
                throw new IslemHatasi($"{refNo} portföyde değil (başka bir bordroyla çıkmış olabilir).");

            var simdi = MikroEvrak.Simdi();
            var tutar = (decimal)cek.Tutar;
            yazici.Ekle(conn, tx, "CARI_HESAP_HAREKETLERI", CariSatiri(yazici, simdi, Mikro.CariEvrakTip.CekCikisBordrosu,
                sira, satir++, tarih, cari.Kod, altKur, tutar, cek.Vade, refNo, new()
                {
                    ["cha_tip"] = (byte)Mikro.CariHarTip.Borc,
                    ["cha_cinsi"] = (byte)1,
                    ["cha_kasa_hizmet"] = (byte)Mikro.CariCins.Kasa,
                    ["cha_kasa_hizkod"] = cekKasasi,
                    ["cha_karsisrmrkkodu"] = cek.Srm,
                    ["cha_sntck_poz"] = (byte)1,
                }));

            conn.Execute("""
                UPDATE ODEME_EMIRLERI
                SET sck_nerede_cari_cins = 0, sck_nerede_cari_kodu = @cari, sck_nerede_cari_grupno = 0, sck_sonpoz = 1,
                    sck_son_hareket_tarihi = @tarih, sck_lastup_user = @kullanici, sck_lastup_date = @simdi
                WHERE sck_tip = 0 AND sck_refno = @refNo
                """, new { cari = cari.Kod, tarih, kullanici = yazici.KullaniciNo, simdi, refNo }, tx);
            refNolar.Add(refNo);
            toplam += tutar;
        }

        if (istek.FirmaCekleri!.Count > 0)
        {
            var firmaUnvani = conn.ExecuteScalar<string>("SELECT TOP 1 fir_unvan FROM FIRMALAR WITH (NOLOCK)", transaction: tx) ?? "";
            foreach (var fc in istek.FirmaCekleri)
            {
                var banka = conn.QueryFirstOrDefault<BankaHesabi>("""
                    SELECT ban_kod AS Kod, ban_ismi AS Ad, ban_sube AS Sube, ban_TCMB_Kodu AS Tcmb,
                           ban_TCMB_Sube_Kodu AS TcmbSube, ban_TCMB_Il_kodu AS TcmbIl
                    FROM BANKALAR WITH (NOLOCK) WHERE ban_kod = @kod
                    """, new { kod = fc.BankaKod.Trim() }, tx)
                    ?? throw new IslemHatasi($"Banka hesabı bulunamadı: {fc.BankaKod}");
                var refNo = SonrakiRefNo(conn, tx, "FC", Mikro.CekTip.FirmaCeki, tarih.Year);
                var simdi = MikroEvrak.Simdi();
                var cekNo = MikroEvrak.Kes(fc.CekNo?.Trim(), 50);

                yazici.Ekle(conn, tx, "ODEME_EMIRLERI", new Dictionary<string, object?>
                {
                    ["sck_Guid"] = MikroYazici.YeniGuid(),
                    ["sck_fileid"] = (short)54,
                    ["sck_create_user"] = yazici.KullaniciNo,
                    ["sck_create_date"] = simdi,
                    ["sck_lastup_user"] = yazici.KullaniciNo,
                    ["sck_lastup_date"] = simdi,
                    ["sck_tip"] = (byte)Mikro.CekTip.FirmaCeki,
                    ["sck_refno"] = refNo,
                    ["sck_bankano"] = banka.Kod,
                    ["sck_borclu"] = MikroEvrak.Kes(firmaUnvani, 50),
                    ["sck_vade"] = fc.Vade.Date,
                    ["sck_tutar"] = (double)fc.Tutar,
                    ["sck_banka_adres1"] = banka.Ad,
                    ["sck_sube_adres2"] = banka.Sube,
                    ["sck_no"] = cekNo,
                    ["sck_sahip_cari_cins"] = (byte)Mikro.CariCins.Banka,
                    ["sck_sahip_cari_kodu"] = banka.Kod,
                    ["sck_sahip_cari_grupno"] = (byte)2,
                    ["sck_nerede_cari_kodu"] = cari.Kod,
                    ["sck_ilk_hareket_tarihi"] = tarih,
                    ["sck_ilk_evrak_sira_no"] = sira,
                    ["sck_ilk_evrak_satir_no"] = satir,
                    ["sck_son_hareket_tarihi"] = tarih,
                    ["sck_doviz_kur"] = 1d,
                    ["sck_sonpoz"] = (byte)1,
                    ["Sck_TCMB_Banka_kodu"] = banka.Tcmb,
                    ["Sck_TCMB_Sube_kodu"] = banka.TcmbSube,
                    ["Sck_TCMB_il_kodu"] = banka.TcmbIl,
                });

                yazici.Ekle(conn, tx, "CARI_HESAP_HAREKETLERI", CariSatiri(yazici, simdi, Mikro.CariEvrakTip.CekCikisBordrosu,
                    sira, satir++, tarih, cari.Kod, altKur, fc.Tutar, fc.Vade, refNo, new()
                    {
                        ["cha_tip"] = (byte)Mikro.CariHarTip.Borc,
                        ["cha_cinsi"] = (byte)3,      // firma çeki
                        ["cha_aciklama"] = cekNo,
                        ["cha_kasa_hizmet"] = (byte)Mikro.CariCins.Banka,
                        ["cha_kasa_hizkod"] = banka.Kod,
                        ["cha_karsidgrupno"] = (byte)2,
                        ["cha_sntck_poz"] = (byte)1,
                    }));
                refNolar.Add(refNo);
                toplam += fc.Tutar;
            }
        }
        return new Sonuc(Mikro.CariEvrakTip.CekCikisBordrosu, sira, refNolar, toplam);
    });

    /// <summary>MC-000-000-2026-00000019 biçiminde; yıl içinde sıra.</summary>
    private static string SonrakiRefNo(IDbConnection conn, IDbTransaction tx, string onEk, int tip, int yil)
    {
        var bas = $"{onEk}-000-000-{yil}-";
        var son = conn.ExecuteScalar<string?>(
            "SELECT MAX(sck_refno) FROM ODEME_EMIRLERI WITH (UPDLOCK, HOLDLOCK) WHERE sck_tip = @tip AND sck_refno LIKE @desen",
            new { tip, desen = bas + "[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]" }, tx);
        var n = son is null ? 1 : int.Parse(son[^8..]) + 1;
        return $"{bas}{n:D8}";
    }

    private static Dictionary<string, object?> CariSatiri(MikroYazici yazici, DateTime simdi, int evrakTip, int sira, int satir,
        DateTime tarih, string cariKod, double altKur, decimal tutar, DateTime vade, string refNo, Dictionary<string, object?> ek)
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
            ["cha_belge_tarih"] = tarih,
            ["cha_kod"] = cariKod,
            ["cha_d_kur"] = 1d,
            ["cha_altd_kur"] = altKur,
            ["cha_karsid_kur"] = 1d,
            ["cha_meblag"] = (double)tutar,
            ["cha_aratoplam"] = (double)tutar,
            ["cha_vade"] = int.Parse(vade.ToString("yyyyMMdd")),
            ["cha_trefno"] = refNo,
        };
        foreach (var (k, v) in ek) d[k] = v;
        return d;
    }
}
