using System.Security.Claims;
using Butcem.Api.Infrastructure;
using Dapper;

namespace Butcem.Api.Features;

/// <summary>
/// Gün içi ciro (dashboard "bugün" kartları). Kaynak: Orion TBL_KASA_TAKIP_FIS_MAIN — kasa başına Z raporu,
/// nakit, kredi kartı ve iade toplamları. Mikro'ya ertesi gün aktarılan tutarla kasa bazında birebir tutuyor
/// (20.10.2026 için kontrol edildi). Birden fazla Orion kaynağı varsa aynı kasa fişi bir kez sayılır.
/// </summary>
public static class GunlukCiro
{
    public sealed record KasaSatiri(string Kaynak, string Magaza, string KasaKodu, string KasaAdi, int FisSayisi,
                                    decimal ZToplam, decimal Nakit, decimal KrediKarti, decimal Iade,
                                    DateTime? SonGuncelleme);

    public sealed record MagazaSatiri(string Magaza, decimal ZToplam, decimal Nakit, decimal KrediKarti,
                                      decimal Iade, DateTime? SonGuncelleme, IReadOnlyList<KasaSatiri> Kasalar);

    public sealed record Sonuc(DateOnly Tarih, decimal Toplam, IReadOnlyList<MagazaSatiri> Magazalar,
                               IReadOnlyList<string> UlasilamayanKaynaklar);

    private sealed record Ham(string BelgeKodu, string Magaza, string KasaKodu, string KasaAdi, decimal ZToplam,
                              decimal Nakit, decimal KrediKarti, decimal Iade, DateTime? Olusturma);

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/ciro", (DateOnly? tarih, ClaimsPrincipal user, Db db, ILoggerFactory lf) =>
        {
            var gun = tarih ?? DateOnly.FromDateTime(DateTime.Today);
            var log = lf.CreateLogger("GunlukCiro");
            var hatalar = new List<string>();
            var gorulen = new HashSet<string>();
            var satirlar = new List<(string Kaynak, Ham H)>();

            foreach (var kaynak in db.OrionKaynaklari)
            {
                try
                {
                    using var conn = db.Orion(kaynak);
                    var rows = conn.Query<Ham>("""
                        SELECT m.BELGEKODU AS BelgeKodu, ISNULL(d.DEPOADI, '') AS Magaza,
                               k.KASAKODU AS KasaKodu, k.KASAADI AS KasaAdi,
                               CAST(m.ZRAPORUTOPLAM AS decimal(18,2)) AS ZToplam,
                               CAST(m.NAKITTOPLAM AS decimal(18,2)) AS Nakit,
                               CAST(m.KREDITOPLAM AS decimal(18,2)) AS KrediKarti,
                               CAST(m.IADETOPLAM AS decimal(18,2)) AS Iade,
                               ISNULL(m.CHANGEDATE, m.CREATEDATE) AS Olusturma
                        FROM TBL_KASA_TAKIP_FIS_MAIN m WITH (NOLOCK)
                        JOIN TBL_KASA_MAIN k WITH (NOLOCK) ON k.ID = m.KASAID
                        LEFT JOIN TBL_DEPO_MAIN d WITH (NOLOCK) ON d.ID = k.DEPOID
                        WHERE m.BELGETARIHI = @gun;
                        """, new { gun = gun.ToDateTime(TimeOnly.MinValue) }, commandTimeout: 30);
                    foreach (var r in rows)
                        if (gorulen.Add($"{r.KasaKodu}|{r.BelgeKodu}")) satirlar.Add((kaynak.Ad, r));
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Orion kaynağına ulaşılamadı: {Kaynak}", kaynak.Ad);
                    hatalar.Add(kaynak.Ad);
                }
            }

            var kasalar = satirlar
                .GroupBy(x => (x.H.Magaza, x.H.KasaKodu))
                .Select(g => new KasaSatiri(g.First().Kaynak, g.Key.Magaza, g.Key.KasaKodu, g.First().H.KasaAdi,
                    g.Count(), g.Sum(x => x.H.ZToplam), g.Sum(x => x.H.Nakit), g.Sum(x => x.H.KrediKarti),
                    g.Sum(x => x.H.Iade), g.Max(x => x.H.Olusturma)))
                .OrderBy(k => k.KasaKodu).ToList();

            var magazalar = kasalar.GroupBy(k => k.Magaza)
                .Select(g => new MagazaSatiri(g.Key, g.Sum(k => k.ZToplam), g.Sum(k => k.Nakit),
                    g.Sum(k => k.KrediKarti), g.Sum(k => k.Iade), g.Max(k => k.SonGuncelleme), g.ToList()))
                .OrderByDescending(m => m.ZToplam).ToList();

            return Results.Ok(new Sonuc(gun, magazalar.Sum(m => m.ZToplam), magazalar, hatalar));
        })
        .RequireYetki(Yetkiler.Ciro)
        .WithTags("Raporlar");

        // Ana sayfa grafiği: gün gün toplam ciro (aynı Z raporları, kasa ayrıntısı olmadan).
        app.MapGet("/api/ciro/gunler", (DateOnly bas, DateOnly bit, ILoggerFactory lf, Db db) =>
        {
            if (bit < bas) return Results.BadRequest("Bitiş tarihi başlangıçtan önce olamaz.");
            if (bit.DayNumber - bas.DayNumber > 400) return Results.BadRequest("En fazla 400 günlük aralık sorgulanabilir.");
            var log = lf.CreateLogger("GunlukCiro");
            var hatalar = new List<string>();
            var gorulen = new HashSet<string>();
            var toplamlar = new SortedDictionary<DateOnly, decimal>();
            for (var d = bas; d <= bit; d = d.AddDays(1)) toplamlar[d] = 0;

            foreach (var kaynak in db.OrionKaynaklari)
            {
                try
                {
                    using var conn = db.Orion(kaynak);
                    var rows = conn.Query<(string BelgeKodu, string KasaKodu, DateTime Tarih, decimal ZToplam)>("""
                        SELECT m.BELGEKODU, k.KASAKODU, m.BELGETARIHI, CAST(m.ZRAPORUTOPLAM AS decimal(18,2))
                        FROM TBL_KASA_TAKIP_FIS_MAIN m WITH (NOLOCK)
                        JOIN TBL_KASA_MAIN k WITH (NOLOCK) ON k.ID = m.KASAID
                        WHERE m.BELGETARIHI >= @bas AND m.BELGETARIHI <= @bit;
                        """, new { bas = bas.ToDateTime(TimeOnly.MinValue), bit = bit.ToDateTime(TimeOnly.MinValue) }, commandTimeout: 30);
                    foreach (var r in rows)
                        if (gorulen.Add($"{r.KasaKodu}|{r.BelgeKodu}"))
                            toplamlar[DateOnly.FromDateTime(r.Tarih)] = toplamlar.GetValueOrDefault(DateOnly.FromDateTime(r.Tarih)) + r.ZToplam;
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Orion kaynağına ulaşılamadı: {Kaynak}", kaynak.Ad);
                    hatalar.Add(kaynak.Ad);
                }
            }
            return Results.Ok(new
            {
                gunler = toplamlar.Select(x => new { tarih = x.Key, toplam = x.Value }),
                ulasilamayanKaynaklar = hatalar,
            });
        })
        .RequireYetki(Yetkiler.Ciro)
        .WithTags("Raporlar");
    }
}
