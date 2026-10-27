using System.Security.Claims;
using Butcem.Api.Etiket;
using Butcem.Api.Infrastructure;
using Dapper;

namespace Butcem.Api.Features;

/// <summary>
/// Panelin etiket tasarımı ekranı: yerleşik raf / manav tasarımını değiştirme, yeni tasarım ekleme, canlı önizleme.
/// Kaydedilen tasarım hemen terminalde ve etiket basım ekranında kullanılır; program değişikliği gerekmez.
/// </summary>
public static class EtiketTasarimlari
{
    public sealed record Kayit(string Ad, bool Aktif, int Sira, EtiketTasarimi Tasarim);
    public sealed record OnizlemeIstegi(EtiketTasarimi Tasarim, string? Barkod);

    /// <summary>Barkod verilmeden önizlemede kullanılan örnek ürün.</summary>
    public static readonly Stok.Urun OrnekUrun = new("8690000000017", "ÖRNEK ÜRÜN ADI UZUN BİR İSİM 1 KG", "8690000000017", "ADET",
        1234.50m, DateTime.Today, 1099.90m, true, "TÜRKİYE", "", "R1");

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/etiket-tasarim").RequireYetki(Yetkiler.EtiketTasarim).WithTags("Etiket tasarımı");

        g.MapGet("/", (Db db, IConfiguration cfg) =>
        {
            using var conn = db.App();
            return Results.Ok(new
            {
                sablonlar = EtiketSablonlari.Hepsi(conn, EtiketAyarlari.Oku(cfg)),
                alanlar = EtiketSablonlari.Alanlar.Select(a => new { a.Kod, a.Ad }),
            });
        });

        g.MapPut("/{kod}", (string kod, Kayit k, ClaimsPrincipal user, Db db, IslemLogu log) =>
        {
            kod = kod.Trim().ToLowerInvariant();
            if (!EtiketSablonlari.KodKalibi.IsMatch(kod)) throw new IslemHatasi("Kod en fazla 20 karakter olabilir: küçük harf, rakam, - ve _.");
            if (string.IsNullOrWhiteSpace(k.Ad) || k.Ad.Trim().Length > 60) throw new IslemHatasi("Tasarıma 1-60 karakterlik bir ad verin.");
            EtiketSablonlari.Dogrula(k.Tasarim);
            var json = System.Text.Json.JsonSerializer.Serialize(k.Tasarim, EtiketSablonlari.Json);
            using var conn = db.App();
            conn.Execute("""
                MERGE EtiketSablonlari WITH (HOLDLOCK) AS h
                USING (SELECT @kod AS Kod) AS k ON h.Kod = k.Kod
                WHEN MATCHED THEN UPDATE SET Ad = @ad, Tasarim = @json, Aktif = @Aktif, Sira = @Sira,
                                             GuncelleyenId = @kullanici, GuncellemeZamani = SYSDATETIME()
                WHEN NOT MATCHED THEN INSERT (Kod, Ad, Tasarim, Aktif, Sira, GuncelleyenId)
                                      VALUES (@kod, @ad, @json, @Aktif, @Sira, @kullanici);
                """, new { kod, ad = k.Ad.Trim(), json, k.Aktif, k.Sira, kullanici = user.Id() });
            log.Yaz(user.Id(), "etiket_tasarim", new { kod, k.Ad, ogeler = k.Tasarim.Ogeler.Count });
            return Results.Ok(new { kod });
        });

        // Yerleşik tasarımda (raf, manav) kayıt silinir ve programın içindeki hali geri gelir; diğerleri tamamen silinir.
        g.MapDelete("/{kod}", (string kod, ClaimsPrincipal user, Db db, IslemLogu log) =>
        {
            using var conn = db.App();
            var n = conn.Execute("DELETE FROM EtiketSablonlari WHERE Kod = @kod", new { kod });
            if (n == 0) return Results.NotFound("Kayıtlı tasarım yok.");
            log.Yaz(user.Id(), "etiket_tasarim_sil", new { kod });
            return Results.NoContent();
        });

        g.MapPost("/onizleme", (OnizlemeIstegi istek, Db db, EtiketCizici cizici, IConfiguration cfg) =>
        {
            EtiketSablonlari.Dogrula(istek.Tasarim);
            var u = OrnekUrun;
            if (!string.IsNullOrWhiteSpace(istek.Barkod))
            {
                using var conn = db.Mikro();
                u = Stok.BarkodBul(conn, istek.Barkod.Trim()) ?? throw new IslemHatasi($"Barkod bulunamadı: {istek.Barkod}");
            }
            return EtiketBaskisi.Png(cizici, u, istek.Tasarim, EtiketAyarlari.Oku(cfg));
        });
    }
}
