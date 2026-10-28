using System.Globalization;
using System.Security.Claims;
using Butcem.Api.Infrastructure;
using Dapper;

namespace Butcem.Api.Features;

/// <summary>
/// Ana sayfadaki "Bekleyen işler" kutusu: kullanıcının yetkisi olan ekranlardaki bekleyen işlerin özeti.
/// Her madde ilgili ekranın kendi sorgusuyla aynı kuralı kullanır; yalnızca sayar, bir şey değiştirmez.
/// </summary>
public static class AnaSayfa
{
    /// <summary>Tur: sayim, irsaliye, cek, etiket. Sayfa: panelde açılacak ekran.</summary>
    public sealed record BekleyenIs(string Tur, string Baslik, string Metin, string Ek, string Sayfa);

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/ana-sayfa/bekleyenler", (ClaimsPrincipal user, Db db, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("AnaSayfa");
            bool Yetkili(string y) => user.HasClaim(Yetkiler.YetkiClaim, y);
            int? depo = user.MagazaKullanicisi() ? user.Depo() : null;
            var liste = new List<BekleyenIs>();
            var bugun = DateTime.Today;

            // Bir maddenin sorgusu hata verirse diğerleri yine gösterilir.
            void Ekle(string ad, Func<BekleyenIs?> f)
            {
                try { if (f() is { } b) liste.Add(b); }
                catch (Exception ex) { log.LogWarning(ex, "Bekleyen iş okunamadı: {Ad}", ad); }
            }

            if (Yetkili(Yetkiler.SayimOnay))
                Ekle("sayim", () =>
                {
                    using var conn = db.App();
                    var s = conn.QuerySingle<(int Adet, int Satir, DateTime? Ilk)>("""
                        SELECT COUNT(DISTINCT s.Id), COUNT(t.StokKodu), MIN(s.GonderimZamani)
                        FROM Sayimlar s LEFT JOIN SayimSatirlari t ON t.SayimId = s.Id
                        WHERE s.Durum = 1 AND (@depo IS NULL OR s.DepoNo = @depo)
                        """, new { depo });
                    return s.Adet == 0 ? null
                        : new("sayim", "Sayım", $"{s.Adet} sayım onay bekliyor · {s.Satir.ToString("N0", Tr)} ürün", Gecen(s.Ilk), "sayim");
                });

            if (Yetkili(Yetkiler.AlisFatura))
                Ekle("irsaliye", () =>
                {
                    using var conn = db.Mikro();
                    var s = conn.QuerySingle<(int Adet, DateTime? Ilk)>("""
                        SELECT COUNT(*), MIN(Tarih) FROM (
                            SELECT MIN(h.sth_tarih) AS Tarih
                            FROM STOK_HAREKETLERI h WITH (NOLOCK)
                            WHERE h.sth_evraktip = 13 AND h.sth_tip = 0 AND h.sth_normal_iade = 0
                              AND h.sth_fat_uid = '00000000-0000-0000-0000-000000000000'
                              AND h.sth_tarih >= DATEADD(month, -6, GETDATE())
                              AND (@depo IS NULL OR h.sth_giris_depo_no = @depo)
                            GROUP BY h.sth_evrakno_seri, h.sth_evrakno_sira, h.sth_cari_kodu) x
                        """, new { depo });
                    return s.Adet == 0 ? null
                        : new("irsaliye", "İrsaliye", $"{s.Adet} alış irsaliyesi faturalanmadı", s.Ilk is { } t ? $"en eskisi {t:dd.MM}" : "", "fatura");
                });

            if (Yetkili(Yetkiler.CekRaporu))
                Ekle("cek", () =>
                {
                    using var conn = db.Mikro();
                    var s = conn.QuerySingle<(int Adet, decimal Tutar, DateTime? Ilk)>("""
                        SELECT COUNT(*), ISNULL(CAST(SUM(sck_tutar) AS decimal(18,2)), 0), MIN(sck_vade)
                        FROM ODEME_EMIRLERI WITH (NOLOCK)
                        WHERE ISNULL(sck_iptal, 0) = 0 AND sck_tip IN (0, 2) AND sck_sonpoz = 0
                          AND sck_vade >= @bugun AND sck_vade < @sonra
                        """, new { bugun, sonra = bugun.AddDays(8) });
                    return s.Adet == 0 ? null
                        : new("cek", "Çek", $"7 gün içinde vadesi gelen {s.Adet} çek / senet · {s.Tutar.ToString("N0", Tr)} ₺", s.Ilk is { } t ? $"ilki {t:dd.MM}" : "", "cek");
                });

            if (Yetkili(Yetkiler.Etiket))
                Ekle("etiket", () =>
                {
                    using var conn = db.Mikro();
                    var adet = conn.ExecuteScalar<int>("""
                        SELECT COUNT(DISTINCT fid_stok_kod) FROM STOK_FIYAT_DEGISIKLIKLERI WITH (NOLOCK)
                        WHERE fid_fiyat_no = 1 AND fid_tarih = @bugun AND ISNULL(fid_iptal, 0) = 0
                        """, new { bugun });
                    return adet == 0 ? null : new("etiket", "Etiket", $"Bugün fiyatı değişen {adet} ürün", "bugün", "etiket");
                });

            return Results.Ok(liste);
        })
        .RequireAuthorization()
        .WithTags("Raporlar");
    }

    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    private static string Gecen(DateTime? t)
    {
        if (t is null) return "";
        var f = DateTime.Now - t.Value;
        return f.TotalMinutes < 60 ? $"{Math.Max(1, (int)f.TotalMinutes)} dk" : f.TotalHours < 24 ? $"{(int)f.TotalHours} saat" : $"{(int)f.TotalDays} gün";
    }
}
