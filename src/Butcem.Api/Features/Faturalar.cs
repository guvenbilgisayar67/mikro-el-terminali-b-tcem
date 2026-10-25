using System.Data;
using System.Security.Claims;
using Butcem.Api.Infrastructure;
using Dapper;

namespace Butcem.Api.Features;

/// <summary>
/// Alış irsaliyesi, irsaliyeden alış faturası ve satış faturası.
///
/// Firmanın alış akışı (örnek: SOLAK 3973, belge 495623241): mal gelince alış irsaliyesi girilir
/// (STOK_HAREKETLERI, evrak tipi 13); fatura gelince irsaliye faturalaştırılır: tek bir
/// CARI_HESAP_HAREKETLERI satırı (evrak tipi 0, boş seri) açılır ve irsaliye satırlarının sth_fat_uid'i
/// bu satıra bağlanır. Bir irsaliye bir faturaya gider.
///
/// Satış faturası (örnek: 250142, 120.381): stok satırları (evrak tipi 4) ve cari satırı (evrak tipi 63)
/// aynı sıra numarasıyla yazılır; satırların sth_fat_uid'i cari satırını gösterir. İstenirse Mikro'nun
/// açtığı E_FATURA_ISLEMLERI kaydı da açılır, fatura Mikro'nun e-fatura/e-arşiv ekranından gönderilir.
/// </summary>
public static class Faturalar
{
    /// <param name="KdvDahil">true ise BirimFiyat KDV dahildir; satırın KDV oranıyla hariç fiyata çevrilir.</param>
    public sealed record SatirIstek(string Urun, decimal Miktar, decimal BirimFiyat, decimal? IskontoYuzde, bool KdvDahil = false);

    public sealed record IrsaliyeIstek(string CariKod, int DepoNo, DateTime? Tarih, string? BelgeNo, DateTime? BelgeTarih,
                                       List<SatirIstek> Satirlar);

    public sealed record IrsaliyeSonuc(string Seri, int Sira, int SatirSayisi, decimal AraToplam, decimal Kdv, decimal Toplam);

    public sealed record FaturalastirIstek(string? Seri, int Sira, string? BelgeNo, DateTime? Tarih);

    public sealed record FaturaSonuc(int EvrakTip, string Seri, int Sira, decimal AraToplam, decimal Iskonto, decimal Kdv, decimal Toplam);

    public sealed record SatisIstek(string CariKod, int DepoNo, DateTime? Tarih, string? BelgeNo, List<SatirIstek> Satirlar);

    public sealed record BekleyenIrsaliye(string Seri, int Sira, DateTime Tarih, string BelgeNo, string CariKod, string CariAdi,
                                          int DepoNo, int SatirSayisi, decimal AraToplam);

    private sealed record IrsaliyeSatiri(Guid Guid, DateTime Tarih, DateTime BelgeTarih, string BelgeNo, string CariKod,
                                         string Srm, int VergiPntr, double Tutar, double Isk1, double Isk2, double Isk3,
                                         double Isk4, double Isk5, double Isk6, double Vergi, Guid FatUid);

    /// <summary>Hesaplanmış satır: tutar KDV hariç brüt, iskonto ayrı, KDV iskontodan sonra.</summary>
    private sealed record Hesap(MikroEvrak.StokBilgi Stok, decimal Miktar, decimal Tutar, decimal Iskonto, double Kdv);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/fatura").RequireAuthorization(Roller.RaporOkuyabilir).WithTags("Fatura");

        g.MapPost("/alis-irsaliyesi", (IrsaliyeIstek istek, ClaimsPrincipal user, MikroYazici yazici, IConfiguration config, IslemLogu log) =>
        {
            SatirKontrol(istek.Satirlar, istek.Tarih);
            var sonuc = AlisIrsaliyesi(yazici, config, istek);
            log.Yaz(user.Id(), "alis_irsaliyesi", new { istek, sonuc });
            return Results.Ok(sonuc);
        });

        // Faturası henüz girilmemiş alış irsaliyeleri.
        g.MapGet("/bekleyen-irsaliyeler", (string? cari, Db db) =>
        {
            using var conn = db.Mikro();
            return Results.Ok(conn.Query<BekleyenIrsaliye>("""
                SELECT h.sth_evrakno_seri AS Seri, h.sth_evrakno_sira AS Sira, MIN(h.sth_tarih) AS Tarih,
                       MIN(h.sth_belge_no) AS BelgeNo, h.sth_cari_kodu AS CariKod, MIN(ISNULL(c.cari_unvan1, '')) AS CariAdi,
                       MIN(h.sth_giris_depo_no) AS DepoNo, COUNT(*) AS SatirSayisi, CAST(SUM(h.sth_tutar) AS decimal(18,2)) AS AraToplam
                FROM STOK_HAREKETLERI h WITH (NOLOCK)
                LEFT JOIN CARI_HESAPLAR c WITH (NOLOCK) ON c.cari_kod = h.sth_cari_kodu
                WHERE h.sth_evraktip = 13 AND h.sth_tip = 0 AND h.sth_normal_iade = 0
                  AND h.sth_fat_uid = '00000000-0000-0000-0000-000000000000'
                  AND h.sth_tarih >= DATEADD(month, -6, GETDATE())
                  AND (@cari IS NULL OR h.sth_cari_kodu = @cari)
                GROUP BY h.sth_evrakno_seri, h.sth_evrakno_sira, h.sth_cari_kodu
                ORDER BY MIN(h.sth_tarih) DESC
                """, new { cari = string.IsNullOrWhiteSpace(cari) ? null : cari.Trim() }));
        });

        g.MapPost("/alis/irsaliyeden", (FaturalastirIstek istek, ClaimsPrincipal user, MikroYazici yazici, IConfiguration config, IslemLogu log) =>
        {
            var sonuc = Faturalastir(yazici, config, istek);
            log.Yaz(user.Id(), "alis_faturasi", new { istek, sonuc });
            return Results.Ok(sonuc);
        });

        g.MapPost("/satis", (SatisIstek istek, ClaimsPrincipal user, MikroYazici yazici, IConfiguration config, IslemLogu log) =>
        {
            SatirKontrol(istek.Satirlar, istek.Tarih);
            var sonuc = Satis(yazici, config, istek);
            log.Yaz(user.Id(), "satis_faturasi", new { istek, sonuc });
            return Results.Ok(sonuc);
        });
    }

    private static void SatirKontrol(List<SatirIstek>? satirlar, DateTime? tarih)
    {
        if (satirlar is not { Count: > 0 }) throw new IslemHatasi("En az bir ürün ekleyin.");
        if (satirlar.Count > 500) throw new IslemHatasi("Bir evrakta en fazla 500 satır olabilir.");
        if (satirlar.Any(s => s.Miktar <= 0)) throw new IslemHatasi("Miktar sıfırdan büyük olmalı.");
        if (satirlar.Any(s => s.BirimFiyat < 0)) throw new IslemHatasi("Fiyat negatif olamaz.");
        if (satirlar.Any(s => s.IskontoYuzde is < 0 or >= 100)) throw new IslemHatasi("İskonto %0 ile %100 arasında olmalı.");
        if (tarih is { } t && t.Date > DateTime.Today) throw new IslemHatasi("İleri tarihli evrak girilemez.");
    }

    /// <summary>KDV oranı (%) vergi göstergesine göre. Mikro'nun parametre tablosu kopyada yok; değerler hareketlerden doğrulandı.</summary>
    private static decimal KdvOrani(IConfiguration config, int pntr)
    {
        if (config[$"Yazma:KdvOranlari:{pntr}"] is { } s) return decimal.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        return pntr switch
        {
            0 or 1 or 8 => 0m, 2 => 1m, 3 => 8m, 4 => 18m, 6 => 10m, 7 => 20m,
            _ => throw new IslemHatasi($"KDV göstergesi {pntr} için oran tanımlı değil (Yazma:KdvOranlari)."),
        };
    }

    private static List<Hesap> Hesapla(IDbConnection conn, IDbTransaction tx, IConfiguration config, List<SatirIstek> satirlar) =>
        satirlar.Select(s =>
        {
            var stok = MikroEvrak.Stok(conn, tx, s.Urun);
            var oran = KdvOrani(config, stok.VergiPntr);
            var birim = s.KdvDahil ? s.BirimFiyat / (1 + oran / 100) : s.BirimFiyat;
            var tutar = Math.Round(birim * s.Miktar, 2, MidpointRounding.AwayFromZero);
            // Mikro iskontoyu da yuvarlamadan saklar (örn. 158,72'nin %10'u 15,872).
            var isk = Math.Round(tutar * (s.IskontoYuzde ?? 0) / 100, 6);
            // Mikro KDV'yi satırda yuvarlamadan saklar (örn. 17.4257).
            var kdv = Math.Round((double)(tutar - isk) * (double)oran / 100, 6);
            return new Hesap(stok, s.Miktar, tutar, isk, kdv);
        }).ToList();

    public static IrsaliyeSonuc AlisIrsaliyesi(MikroYazici yazici, IConfiguration config, IrsaliyeIstek istek) => yazici.Calistir((conn, tx) =>
    {
        var tarih = (istek.Tarih ?? DateTime.Today).Date;
        MikroEvrak.DepoKontrol(conn, tx, istek.DepoNo, tarih);
        var cari = MikroEvrak.Cari(conn, tx, istek.CariKod);
        var seri = yazici.DepoSerisi(istek.DepoNo);
        var sira = yazici.SonrakiSira(conn, tx, "STOK_HAREKETLERI", "sth_evraktip", Mikro.StokEvrakTip.GirisIrsaliyesi,
                                      "sth_evrakno_seri", seri, "sth_evrakno_sira");
        var altKur = yazici.AlternatifKur(conn, tx);
        var srm = MikroEvrak.DepoSorumlulukMerkezi(conn, tx, istek.DepoNo);
        var satirlar = Hesapla(conn, tx, config, istek.Satirlar);

        for (var i = 0; i < satirlar.Count; i++)
        {
            var s = satirlar[i];
            var net = (double)(s.Tutar - s.Iskonto);
            var d = StokSatiri(yazici, Mikro.StokEvrakTip.GirisIrsaliyesi, seri, sira, i, tarih, istek.BelgeTarih ?? tarih,
                               istek.BelgeNo, cari, s, altKur, istek.DepoNo);
            d["sth_tip"] = (byte)Mikro.StokHarTip.Giris;
            d["sth_cari_srm_merkezi"] = srm;
            d["sth_maliyet_ana"] = net;
            d["sth_maliyet_alternatif"] = Math.Round(net / altKur, 12);
            d["sth_maliyet_orjinal"] = net;
            for (var k = 1; k <= 10; k++) d[$"sth_isk_mas{k}"] = (byte)1;
            yazici.Ekle(conn, tx, "STOK_HAREKETLERI", d);
        }
        var ara = satirlar.Sum(s => s.Tutar - s.Iskonto);
        var kdv = (decimal)satirlar.Sum(s => s.Kdv);
        return new IrsaliyeSonuc(seri, sira, satirlar.Count, ara, Math.Round(kdv, 2), Math.Round(ara + kdv, 2));
    });

    public static FaturaSonuc Faturalastir(MikroYazici yazici, IConfiguration config, FaturalastirIstek istek) => yazici.Calistir((conn, tx) =>
    {
        var seri = istek.Seri ?? "";
        var satirlar = conn.Query<IrsaliyeSatiri>("""
            SELECT sth_Guid AS Guid, sth_tarih AS Tarih, sth_belge_tarih AS BelgeTarih, sth_belge_no AS BelgeNo,
                   sth_cari_kodu AS CariKod, sth_cari_srm_merkezi AS Srm, CAST(sth_vergi_pntr AS int) AS VergiPntr,
                   sth_tutar AS Tutar, sth_iskonto1 AS Isk1, sth_iskonto2 AS Isk2, sth_iskonto3 AS Isk3,
                   sth_iskonto4 AS Isk4, sth_iskonto5 AS Isk5, sth_iskonto6 AS Isk6, sth_vergi AS Vergi, sth_fat_uid AS FatUid
            FROM STOK_HAREKETLERI WITH (UPDLOCK, ROWLOCK)
            WHERE sth_evraktip = 13 AND sth_tip = 0 AND sth_evrakno_seri = @seri AND sth_evrakno_sira = @sira
            ORDER BY sth_satirno
            """, new { seri, sira = istek.Sira }, tx).ToList();
        if (satirlar.Count == 0) throw new IslemHatasi($"Alış irsaliyesi bulunamadı: {seri} {istek.Sira}");
        if (satirlar.Any(s => s.FatUid != Guid.Empty)) throw new IslemHatasi("Bu irsaliyenin faturası zaten girilmiş.");

        var ilk = satirlar[0];
        var tarih = (istek.Tarih ?? ilk.Tarih).Date;
        if (tarih > DateTime.Today) throw new IslemHatasi("İleri tarihli fatura girilemez.");
        var belgeNo = string.IsNullOrWhiteSpace(istek.BelgeNo) ? ilk.BelgeNo : istek.BelgeNo.Trim();
        if (string.IsNullOrWhiteSpace(belgeNo)) throw new IslemHatasi("Fatura numarasını (belge no) yazın.");

        var fSira = yazici.SonrakiSira(conn, tx, "CARI_HESAP_HAREKETLERI", "cha_evrak_tip", Mikro.CariEvrakTip.AlisFaturasi,
                                       "cha_evrakno_seri", "", "cha_evrakno_sira");
        var chaGuid = MikroYazici.YeniGuid();
        var simdi = MikroEvrak.Simdi();
        var d = CariFaturaSatiri(yazici, chaGuid, simdi, Mikro.CariEvrakTip.AlisFaturasi, fSira, tarih, istek.Tarih is null ? ilk.BelgeTarih : tarih,
                                 belgeNo, ilk.CariKod, ilk.Srm, yazici.AlternatifKur(conn, tx), satirlar);
        d["cha_tip"] = (byte)Mikro.CariHarTip.Alacak;
        yazici.Ekle(conn, tx, "CARI_HESAP_HAREKETLERI", d);

        conn.Execute("""
            UPDATE STOK_HAREKETLERI SET sth_fat_uid = @chaGuid, sth_lastup_user = @kullanici, sth_lastup_date = @simdi
            WHERE sth_evraktip = 13 AND sth_tip = 0 AND sth_evrakno_seri = @seri AND sth_evrakno_sira = @sira
            """, new { chaGuid, kullanici = yazici.KullaniciNo, simdi, seri, sira = istek.Sira }, tx);

        return FaturaOzeti(Mikro.CariEvrakTip.AlisFaturasi, "", fSira, d);
    });

    public static FaturaSonuc Satis(MikroYazici yazici, IConfiguration config, SatisIstek istek) => yazici.Calistir((conn, tx) =>
    {
        var tarih = (istek.Tarih ?? DateTime.Today).Date;
        MikroEvrak.DepoKontrol(conn, tx, istek.DepoNo, tarih);
        var cari = MikroEvrak.Cari(conn, tx, istek.CariKod);
        // Stok ve cari satırları aynı numarayı taşır; ikisinin de en büyüğünden devam edilir.
        var sira = Math.Max(
            yazici.SonrakiSira(conn, tx, "CARI_HESAP_HAREKETLERI", "cha_evrak_tip", Mikro.CariEvrakTip.SatisFaturasi, "cha_evrakno_seri", "", "cha_evrakno_sira"),
            yazici.SonrakiSira(conn, tx, "STOK_HAREKETLERI", "sth_evraktip", Mikro.StokEvrakTip.CikisFaturasi, "sth_evrakno_seri", "", "sth_evrakno_sira"));
        var altKur = yazici.AlternatifKur(conn, tx);
        var srm = MikroEvrak.DepoSorumlulukMerkezi(conn, tx, istek.DepoNo);
        var satirlar = Hesapla(conn, tx, config, istek.Satirlar);
        var chaGuid = MikroYazici.YeniGuid();
        var sevkAdresNo = conn.ExecuteScalar<int>("SELECT cari_sevk_adres_no FROM CARI_HESAPLAR WITH (NOLOCK) WHERE cari_kod = @k",
                                                  new { k = cari.Kod }, tx);
        var irsSatirlari = new List<IrsaliyeSatiri>();

        for (var i = 0; i < satirlar.Count; i++)
        {
            var s = satirlar[i];
            var d = StokSatiri(yazici, Mikro.StokEvrakTip.CikisFaturasi, "", sira, i, tarih, tarih, istek.BelgeNo, cari, s, altKur, istek.DepoNo);
            d["sth_tip"] = (byte)Mikro.StokHarTip.Cikis;
            d["sth_fat_uid"] = chaGuid;
            d["sth_cari_srm_merkezi"] = srm;
            d["sth_stok_srm_merkezi"] = srm;
            d["sth_adres_no"] = sevkAdresNo == 0 ? 1 : sevkAdresNo;
            d["sth_fiyat_liste_no"] = Mikro.SatisFiyatListesi;
            d["sth_satis_fiyat_doviz_kuru"] = 1d;
            for (var k = 2; k <= 10; k++) d[$"sth_isk_mas{k}"] = (byte)1;
            yazici.Ekle(conn, tx, "STOK_HAREKETLERI", d);
            irsSatirlari.Add(new IrsaliyeSatiri(Guid.Empty, tarih, tarih, istek.BelgeNo ?? "", cari.Kod, srm, s.Stok.VergiPntr,
                (double)s.Tutar, (double)s.Iskonto, 0, 0, 0, 0, 0, s.Kdv, Guid.Empty));
        }

        var simdi = MikroEvrak.Simdi();
        var c = CariFaturaSatiri(yazici, chaGuid, simdi, Mikro.CariEvrakTip.SatisFaturasi, sira, tarih, tarih, istek.BelgeNo ?? "",
                                 cari.Kod, srm, altKur, irsSatirlari);
        c["cha_tip"] = (byte)Mikro.CariHarTip.Borc;

        if (config.GetValue("Yazma:SatisEBelge", false))
        {
            var efatura = conn.ExecuteScalar<bool>("SELECT ISNULL(cari_efatura_fl, 0) FROM CARI_HESAPLAR WITH (NOLOCK) WHERE cari_kod = @k",
                                                   new { k = cari.Kod }, tx);
            var uuid = Guid.NewGuid().ToString().ToUpperInvariant();
            c["cha_ebelge_turu"] = (byte)1;
            c["cha_ebelge_Islemturu"] = (byte)(efatura ? 1 : 2);   // 1 e-fatura, 2 e-arşiv
            c["cha_uuid"] = uuid;
            yazici.Ekle(conn, tx, "E_FATURA_ISLEMLERI", new Dictionary<string, object?>
            {
                ["efi_Guid"] = MikroYazici.YeniGuid(),
                ["efi_fileid"] = (short)559,
                ["efi_create_user"] = yazici.KullaniciNo,
                ["efi_create_date"] = simdi,
                ["efi_lastup_user"] = yazici.KullaniciNo,
                ["efi_lastup_date"] = simdi,
                ["efi_evrakno_seri"] = "",
                ["efi_evrakno_sira"] = sira,
                ["efi_evrak_tipi"] = (byte)Mikro.CariEvrakTip.SatisFaturasi,
                ["efi_uuid"] = uuid,
                ["efi_efaturaolustu_fl"] = true,
                ["efi_efaturaolusturanKulNo"] = yazici.KullaniciNo,
                ["efi_efaturaolusturmatarihi"] = simdi,
                ["efi_efaturaislem_tipi"] = (byte)1,
                ["efi_carikod"] = cari.Kod,
            });
        }
        yazici.Ekle(conn, tx, "CARI_HESAP_HAREKETLERI", c);
        return FaturaOzeti(Mikro.CariEvrakTip.SatisFaturasi, "", sira, c);
    });

    private static Dictionary<string, object?> StokSatiri(MikroYazici yazici, int evrakTip, string seri, int sira, int satir,
        DateTime tarih, DateTime belgeTarih, string? belgeNo, MikroEvrak.CariBilgi cari, Hesap s, double altKur, int depoNo)
    {
        var simdi = MikroEvrak.Simdi();
        return new Dictionary<string, object?>
        {
            ["sth_Guid"] = MikroYazici.YeniGuid(),
            ["sth_fileid"] = (short)16,
            ["sth_create_user"] = yazici.KullaniciNo,
            ["sth_create_date"] = simdi,
            ["sth_lastup_user"] = yazici.KullaniciNo,
            ["sth_lastup_date"] = simdi,
            ["sth_tarih"] = tarih,
            ["sth_cins"] = (byte)Mikro.StokHarCins.Toptan,
            ["sth_normal_iade"] = (byte)0,
            ["sth_evraktip"] = (byte)evrakTip,
            ["sth_evrakno_seri"] = seri,
            ["sth_evrakno_sira"] = sira,
            ["sth_satirno"] = satir,
            ["sth_belge_no"] = MikroEvrak.Kes(belgeNo?.Trim(), 50),
            ["sth_belge_tarih"] = belgeTarih.Date,
            ["sth_stok_kod"] = s.Stok.Kod,
            ["sth_cari_kodu"] = cari.Kod,
            ["sth_har_doviz_kuru"] = 1d,
            ["sth_alt_doviz_kuru"] = altKur,
            ["sth_stok_doviz_kuru"] = 1d,
            ["sth_miktar"] = (double)s.Miktar,
            ["sth_miktar2"] = (double)s.Miktar,
            ["sth_birim_pntr"] = (byte)1,
            ["sth_tutar"] = (double)s.Tutar,
            ["sth_iskonto1"] = (double)s.Iskonto,
            ["sth_vergi_pntr"] = (byte)s.Stok.VergiPntr,
            ["sth_vergi"] = s.Kdv,
            ["sth_giris_depo_no"] = depoNo,
            ["sth_cikis_depo_no"] = depoNo,
            ["sth_malkbl_sevk_tarihi"] = tarih,
            ["sth_adres_no"] = 1,
        };
    }

    /// <summary>Fatura cari satırı: ara toplam brüt, iskontolar ayrı, KDV göstergeye göre cha_vergiN'de.</summary>
    private static Dictionary<string, object?> CariFaturaSatiri(MikroYazici yazici, Guid guid, DateTime simdi, int evrakTip, int sira,
        DateTime tarih, DateTime belgeTarih, string belgeNo, string cariKod, string srm, double altKur, List<IrsaliyeSatiri> satirlar)
    {
        var ara = satirlar.Sum(s => s.Tutar);
        var isk = new[]
        {
            satirlar.Sum(s => s.Isk1), satirlar.Sum(s => s.Isk2), satirlar.Sum(s => s.Isk3),
            satirlar.Sum(s => s.Isk4), satirlar.Sum(s => s.Isk5), satirlar.Sum(s => s.Isk6),
        };
        var d = new Dictionary<string, object?>
        {
            ["cha_Guid"] = guid,
            ["cha_fileid"] = (short)51,
            ["cha_create_user"] = yazici.KullaniciNo,
            ["cha_create_date"] = simdi,
            ["cha_lastup_user"] = yazici.KullaniciNo,
            ["cha_lastup_date"] = simdi,
            ["cha_evrak_tip"] = (byte)evrakTip,
            ["cha_evrakno_seri"] = "",
            ["cha_evrakno_sira"] = sira,
            ["cha_tarihi"] = tarih,
            ["cha_cinsi"] = (byte)6,            // mal faturası
            ["cha_belge_no"] = MikroEvrak.Kes(belgeNo, 50),
            ["cha_belge_tarih"] = belgeTarih.Date,
            ["cha_kod"] = cariKod,
            ["cha_ciro_cari_kodu"] = cariKod,
            ["cha_d_kur"] = 1d,
            ["cha_altd_kur"] = altKur,
            ["cha_srmrkkodu"] = srm,
            ["cha_karsid_kur"] = 1d,
            ["cha_aratoplam"] = Math.Round(ara, 2),
            ["cha_adres_no"] = 1,
        };
        for (var k = 0; k < 6; k++) if (isk[k] != 0) d[$"cha_ft_iskonto{k + 1}"] = isk[k];
        var kdvToplam = 0d;
        foreach (var grup in satirlar.GroupBy(s => s.VergiPntr).Where(x => x.Key > 0))
        {
            var v = Math.Round(grup.Sum(s => s.Vergi), 6);
            d[$"cha_vergi{grup.Key}"] = v;
            kdvToplam += v;
        }
        d["cha_meblag"] = Math.Round(Math.Round(ara, 2) - isk.Sum() + kdvToplam, 6);
        return d;
    }

    private static FaturaSonuc FaturaOzeti(int evrakTip, string seri, int sira, Dictionary<string, object?> d)
    {
        var ara = (decimal)(double)d["cha_aratoplam"]!;
        var isk = Enumerable.Range(1, 6).Sum(k => d.TryGetValue($"cha_ft_iskonto{k}", out var v) ? (decimal)(double)v! : 0m);
        var toplam = (decimal)(double)d["cha_meblag"]!;
        return new FaturaSonuc(evrakTip, seri, sira, ara, Math.Round(isk, 2), Math.Round(toplam - ara + isk, 2), Math.Round(toplam, 2));
    }
}
