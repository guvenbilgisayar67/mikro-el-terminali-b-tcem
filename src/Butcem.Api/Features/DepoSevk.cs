using System.Security.Claims;
using Butcem.Api.Infrastructure;
using Dapper;

namespace Butcem.Api.Features;

/// <summary>
/// Depolar arası sevk fişi. Mikro'da: Stok > Depolar arası sevk fişi.
/// Kayıt: STOK_HAREKETLERI, evrak tipi 2, hareket tipi 2, cins 6; her ürün bir satır.
/// Mikro bu evrakta başka tablo yazmıyor (örnek evraklar 26, 27, 30, 31 ile karşılaştırıldı).
/// </summary>
public static class DepoSevk
{
    public sealed record SatirIstek(string Urun, decimal Miktar, decimal? BirimFiyat);

    public sealed record Istek(int CikisDepo, int GirisDepo, DateTime? Tarih, List<SatirIstek> Satirlar);

    public sealed record Sonuc(string Seri, int Sira, int SatirSayisi, decimal Tutar);

    public sealed record Ozet(string Seri, int Sira, DateTime Tarih, int CikisDepo, int GirisDepo,
                              int SatirSayisi, decimal Miktar, decimal Tutar, DateTime Olusturma);

    public sealed record Satir(int SatirNo, string StokKodu, string UrunAdi, decimal Miktar, decimal Tutar);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/sevk").RequireYetki(Yetkiler.Sevk).WithTags("Depolar arası sevk");

        // Okutulan ürün ve önerilen maliyet fiyatı (son alış).
        g.MapGet("/urun", (string k, DateTime? tarih, Db db) =>
        {
            using var conn = db.Mikro();
            var stok = MikroEvrak.Stok(conn, null, k);
            var fiyat = MikroEvrak.SonAlisFiyati(conn, null, stok.Kod, (tarih ?? DateTime.Today).Date);
            return Results.Ok(new { stok.Kod, stok.Ad, stok.Birim, Miktar = 1m, BirimFiyat = Math.Round(fiyat, 4), Tutar = Math.Round(fiyat, 2) });
        });

        // Kaydetmeden önce ürünleri ve önerilen fiyatı gösterir.
        g.MapPost("/onizleme", (Istek istek, ClaimsPrincipal user, Db db) =>
        {
            Dogrula(istek, user);
            using var conn = db.Mikro();
            var tarih = (istek.Tarih ?? DateTime.Today).Date;
            var satirlar = istek.Satirlar.Select(s =>
            {
                var stok = MikroEvrak.Stok(conn, null, s.Urun);
                var fiyat = s.BirimFiyat ?? MikroEvrak.SonAlisFiyati(conn, null, stok.Kod, tarih);
                return new { stok.Kod, stok.Ad, stok.Birim, s.Miktar, BirimFiyat = Math.Round(fiyat, 4), Tutar = Math.Round(fiyat * s.Miktar, 2) };
            }).ToList();
            return Results.Ok(satirlar);
        });

        // Terminal çevrimdışıyken kaydettiği sevki bağlantı gelince X-Istemci-Id ile gönderir; tekrar gelirse ikinci fiş açılmaz.
        g.MapPost("/", (Istek istek, HttpRequest req, ClaimsPrincipal user, MikroYazici yazici, Db db, IslemLogu log) =>
        {
            Dogrula(istek, user);
            return TekrarKoruma.Calistir(req, db, "sevk", user.Id(), () =>
            {
                var sonuc = Kaydet(yazici, istek);
                log.Yaz(user.Id(), "sevk", new { istek, sonuc });
                return sonuc;
            });
        });

        g.MapGet("/", (DateTime? bas, DateTime? bit, int? depo, ClaimsPrincipal user, Db db) =>
        {
            var b = (bas ?? DateTime.Today.AddDays(-30)).Date;
            var e = (bit ?? DateTime.Today).Date;
            if (user.MagazaKullanicisi()) depo = user.Depo();
            using var conn = db.Mikro();
            return Results.Ok(conn.Query<Ozet>("""
                SELECT sth_evrakno_seri AS Seri, sth_evrakno_sira AS Sira, MIN(sth_tarih) AS Tarih,
                       MIN(sth_cikis_depo_no) AS CikisDepo, MIN(sth_giris_depo_no) AS GirisDepo,
                       COUNT(*) AS SatirSayisi, CAST(SUM(sth_miktar) AS decimal(18,3)) AS Miktar,
                       CAST(SUM(sth_tutar) AS decimal(18,2)) AS Tutar, MIN(sth_create_date) AS Olusturma
                FROM STOK_HAREKETLERI WITH (NOLOCK)
                WHERE sth_evraktip = 2 AND sth_tip = 2 AND sth_cins = 6
                  AND sth_tarih BETWEEN @b AND @e
                  AND (@depo IS NULL OR sth_cikis_depo_no = @depo OR sth_giris_depo_no = @depo)
                GROUP BY sth_evrakno_seri, sth_evrakno_sira
                ORDER BY MIN(sth_tarih) DESC, MIN(sth_create_date) DESC
                """, new { b, e, depo }));
        });

        g.MapGet("/detay", (string? seri, int sira, Db db) =>
        {
            using var conn = db.Mikro();
            return Results.Ok(conn.Query<Satir>("""
                SELECT h.sth_satirno AS SatirNo, h.sth_stok_kod AS StokKodu, ISNULL(s.sto_isim, '') AS UrunAdi,
                       CAST(h.sth_miktar AS decimal(18,3)) AS Miktar, CAST(h.sth_tutar AS decimal(18,2)) AS Tutar
                FROM STOK_HAREKETLERI h WITH (NOLOCK)
                LEFT JOIN STOKLAR s WITH (NOLOCK) ON s.sto_kod = h.sth_stok_kod
                WHERE h.sth_evraktip = 2 AND h.sth_evrakno_seri = @seri AND h.sth_evrakno_sira = @sira
                ORDER BY h.sth_satirno
                """, new { seri = seri ?? "", sira }));
        });
    }

    private static void Dogrula(Istek istek, ClaimsPrincipal user)
    {
        if (istek.CikisDepo == istek.GirisDepo) throw new IslemHatasi("Çıkış ve giriş deposu aynı olamaz.");
        if (istek.Satirlar is not { Count: > 0 }) throw new IslemHatasi("En az bir ürün ekleyin.");
        if (istek.Satirlar.Count > 500) throw new IslemHatasi("Bir fişte en fazla 500 satır olabilir.");
        if (istek.Satirlar.Any(s => s.Miktar <= 0)) throw new IslemHatasi("Miktar sıfırdan büyük olmalı.");
        if (istek.Satirlar.Any(s => s.BirimFiyat < 0)) throw new IslemHatasi("Fiyat negatif olamaz.");
        if (istek.Tarih is { } t && t.Date > DateTime.Today) throw new IslemHatasi("İleri tarihli sevk girilemez.");
        // Mağaza kullanıcısı yalnız kendi deposundan sevk çıkarabilir.
        if (user.MagazaKullanicisi() && user.Depo() != istek.CikisDepo)
            throw new IslemHatasi("Yalnızca kendi deponuzdan sevk yapabilirsiniz.");
    }

    public static Sonuc Kaydet(MikroYazici yazici, Istek istek) => yazici.Calistir((conn, tx) =>
    {
        var tarih = (istek.Tarih ?? DateTime.Today).Date;
        MikroEvrak.DepoKontrol(conn, tx, istek.CikisDepo, tarih);
        MikroEvrak.DepoKontrol(conn, tx, istek.GirisDepo, tarih);

        var seri = yazici.DepoSerisi(istek.CikisDepo);
        var sira = yazici.SonrakiSira(conn, tx, "STOK_HAREKETLERI", "sth_evraktip", Mikro.StokEvrakTip.DepoSevkFisi,
                                      "sth_evrakno_seri", seri, "sth_evrakno_sira");
        var altKur = yazici.AlternatifKur(conn, tx);
        var srm = MikroEvrak.DepoSorumlulukMerkezi(conn, tx, istek.CikisDepo);
        var toplam = 0m;

        for (var i = 0; i < istek.Satirlar.Count; i++)
        {
            var s = istek.Satirlar[i];
            var stok = MikroEvrak.Stok(conn, tx, s.Urun);
            var fiyat = s.BirimFiyat ?? MikroEvrak.SonAlisFiyati(conn, tx, stok.Kod, tarih);
            var tutar = Math.Round(fiyat * s.Miktar, 2, MidpointRounding.AwayFromZero);
            toplam += tutar;
            var simdi = MikroEvrak.Simdi();

            var d = new Dictionary<string, object?>
            {
                ["sth_Guid"] = MikroYazici.YeniGuid(),
                ["sth_fileid"] = (short)16,
                ["sth_create_user"] = yazici.KullaniciNo,
                ["sth_create_date"] = simdi,
                ["sth_lastup_user"] = yazici.KullaniciNo,
                ["sth_lastup_date"] = simdi,
                ["sth_tarih"] = tarih,
                ["sth_tip"] = (byte)Mikro.StokHarTip.DepoTransfer,
                ["sth_cins"] = (byte)Mikro.StokHarCins.DepolarArasi,
                ["sth_normal_iade"] = (byte)0,
                ["sth_evraktip"] = (byte)Mikro.StokEvrakTip.DepoSevkFisi,
                ["sth_evrakno_seri"] = seri,
                ["sth_evrakno_sira"] = sira,
                ["sth_satirno"] = i,
                ["sth_belge_tarih"] = tarih,
                ["sth_stok_kod"] = stok.Kod,
                ["sth_har_doviz_kuru"] = 1d,
                ["sth_alt_doviz_kuru"] = altKur,
                ["sth_stok_doviz_kuru"] = 1d,
                ["sth_miktar"] = (double)s.Miktar,
                ["sth_miktar2"] = (double)s.Miktar,
                ["sth_birim_pntr"] = (byte)1,
                ["sth_tutar"] = (double)tutar,
                ["sth_vergi_pntr"] = (byte)stok.VergiPntr,
                ["sth_giris_depo_no"] = istek.GirisDepo,
                ["sth_cikis_depo_no"] = istek.CikisDepo,
                ["sth_malkbl_sevk_tarihi"] = DateTime.Today,
                ["sth_stok_srm_merkezi"] = srm,
                ["sth_maliyet_ana"] = (double)tutar,
                ["sth_maliyet_alternatif"] = Math.Round((double)tutar / altKur, 12),
                ["sth_maliyet_orjinal"] = (double)tutar,
                ["sth_adres_no"] = 1,
                ["sth_teslim_tarihi"] = DateTime.Today,
            };
            // Mikro'da iskonto 2-10 "masraf/iskonto" bayrakları sevk fişinde 1 yazılıyor.
            for (var k = 2; k <= 10; k++) d[$"sth_isk_mas{k}"] = (byte)1;
            yazici.Ekle(conn, tx, "STOK_HAREKETLERI", d);
        }
        return new Sonuc(seri, sira, istek.Satirlar.Count, toplam);
    });
}
