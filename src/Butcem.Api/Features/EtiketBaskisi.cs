using System.IO.Compression;
using System.Security.Claims;
using System.Security.Cryptography;
using Butcem.Api.Etiket;
using Butcem.Api.Infrastructure;
using Dapper;
using SkiaSharp;

namespace Butcem.Api.Features;

/// <summary>
/// Etiket basımı (madde 8). Akış: terminal barkod okutur → API etiketi çizer ve yazıcının diline çevirir →
/// ButcemApp.EtiketIsleri kuyruğuna yazar → mağaza bilgisayarındaki yazdırma ajanı işi alıp USB yazıcıya basar.
/// </summary>
public static class EtiketBaskisi
{
    public sealed record Kalem(string Barkod, int Adet);
    public sealed record BasmaIstegi(int YaziciId, string Sablon, IReadOnlyList<Kalem> Kalemler);
    public sealed record Yazici(int Id, string Ad, string Tur, string Dil, int DepoNo, string AjanAdi, DateTime? AjanSonGorulme);
    public sealed record Is(long Id, string Yazici, string Sablon, int Adet, int Durum, string? Hata,
                            DateTime OlusturmaZamani, DateTime? BitisZamani, string Olusturan);

    public sealed record BasmaSonucu(List<long> Isler, int Basilacak, List<string> Bulunamayan);
    public sealed record AjanIsi(long Id, string WindowsYaziciAdi, string Dil, string Icerik);
    public sealed record AjanSonucu(bool Basarili, string? Hata);
    public sealed record AjanKaydi(string Ad, int DepoNo);
    public sealed record YaziciKaydi(int AjanId, string Ad, string WindowsYaziciAdi, string Tur, string Dil);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/etiket").RequireYetki(Yetkiler.Etiket).WithTags("Etiket");

        g.MapGet("/onizleme", (string barkod, string? sablon, Db db, EtiketCizici cizici, IConfiguration cfg) =>
        {
            var ayar = EtiketAyarlari.Oku(cfg);
            using var app = db.App();
            var t = EtiketSablonlari.Bul(app, ayar, sablon);
            if (t is null) return Results.NotFound($"Etiket tasarımı bulunamadı: {sablon}");
            using var conn = db.Mikro();
            var u = Stok.BarkodBul(conn, barkod);
            if (u is null) return Results.NotFound($"Barkod bulunamadı: {barkod}");
            return Png(cizici, u, t.Tasarim, ayar);
        });

        // Basım ekranı ve terminal için etkin tasarımlar (öğeleri olmadan).
        g.MapGet("/sablonlar", (Db db, IConfiguration cfg) =>
        {
            using var app = db.App();
            return Results.Ok(EtiketSablonlari.Hepsi(app, EtiketAyarlari.Oku(cfg)).Where(s => s.Aktif)
                .Select(s => new { s.Kod, s.Ad, s.Tasarim.GenislikMm, s.Tasarim.YukseklikMm }));
        });

        // Toplu etiket: belirli gün fiyatı değişen ürünler (Mikro fiyat değişiklik fişleri).
        g.MapGet("/degisenler", (DateOnly? gun, Db db) =>
        {
            var t = gun ?? DateOnly.FromDateTime(DateTime.Today);
            using var conn = db.Mikro();
            var kodlar = conn.Query<string>("""
                SELECT DISTINCT fid_stok_kod FROM STOK_FIYAT_DEGISIKLIKLERI WITH (NOLOCK)
                WHERE fid_fiyat_no = 1 AND fid_tarih = @t AND ISNULL(fid_iptal, 0) = 0
                """, new { t = t.ToDateTime(TimeOnly.MinValue) }).ToList();
            var urunler = kodlar.Count == 0 ? [] : Stok.StokKodlariylaBul(conn, kodlar).OrderBy(u => u.UrunAdi).ToList();
            return Results.Ok(new { gun = t, adet = urunler.Count, urunler });
        });

        g.MapGet("/yazicilar", (ClaimsPrincipal user, Db db) =>
        {
            using var conn = db.App();
            return Results.Ok(conn.Query<Yazici>("""
                SELECT y.Id, y.Ad, y.Tur, y.Dil, a.DepoNo, a.Ad AS AjanAdi, a.SonGorulme AS AjanSonGorulme
                FROM Yazicilar y JOIN YazdirmaAjanlari a ON a.Id = y.AjanId
                WHERE y.Aktif = 1 AND a.Aktif = 1 AND (@depo IS NULL OR a.DepoNo = @depo)
                ORDER BY a.DepoNo, y.Ad
                """, new { depo = user.MagazaKullanicisi() ? user.Depo() : null }));
        });

        g.MapPost("/bas", (BasmaIstegi istek, HttpRequest req, ClaimsPrincipal user, Db db, EtiketCizici cizici, IConfiguration cfg, IslemLogu log) =>
        {
            if (istek.Kalemler.Count == 0) return Results.BadRequest("En az bir ürün seçin.");
            if (istek.Kalemler.Any(k => k.Adet is < 1 or > 500)) return Results.BadRequest("Adet 1 ile 500 arasında olmalı.");
            if (istek.Kalemler.Sum(k => k.Adet) > 2000) return Results.BadRequest("Tek seferde en fazla 2000 etiket.");

            using var app = db.App();
            var yazici = app.QuerySingleOrDefault<(int Id, string Tur, string Dil, int DepoNo)>("""
                SELECT y.Id, y.Tur, y.Dil, a.DepoNo FROM Yazicilar y JOIN YazdirmaAjanlari a ON a.Id = y.AjanId
                WHERE y.Id = @YaziciId AND y.Aktif = 1 AND a.Aktif = 1
                """, new { istek.YaziciId });
            if (yazici.Id == 0) return Results.BadRequest("Yazıcı bulunamadı.");
            if (user.MagazaKullanicisi() && user.Depo() != yazici.DepoNo)
                return Results.Forbid();

            var ayar = EtiketAyarlari.Oku(cfg);
            var sablon = EtiketSablonlari.Bul(app, ayar, istek.Sablon);
            if (sablon is null) return Results.BadRequest($"Etiket tasarımı bulunamadı: {istek.Sablon}");
            var tasarim = sablon.Tasarim;
            using var mikro = db.Mikro();
            var bulunamayan = new List<string>();
            var urunler = new List<(Stok.Urun U, int Adet)>();
            foreach (var k in istek.Kalemler)
            {
                var u = Stok.BarkodBul(mikro, k.Barkod.Trim());
                if (u is null) bulunamayan.Add(k.Barkod); else urunler.Add((u, k.Adet));
            }
            if (urunler.Count == 0) return Results.BadRequest(new { hata = "Hiçbir barkod bulunamadı.", bulunamayan });

            // Terminal çevrimdışıyken biriktirdiği basım isteği iki kez gelirse etiketler bir kez basılır.
            return TekrarKoruma.Calistir(req, db, "etiket", user.Id(), () =>
            {
                var isler = new List<long>();
                if (yazici.Dil == "PDF")
                {
                    // A4: tüm etiketler tek iş, sayfa sayfa PNG (zip içinde).
                    var resimler = urunler.SelectMany(x => Enumerable.Repeat(x.U, x.Adet))
                        .Select(u => cizici.Ciz(u, tasarim, ayar.NoktaPerMm, false)).ToList();
                    var sayfalar = YaziciKomutlari.A4Sayfalari(resimler, tasarim.GenislikMm, tasarim.YukseklikMm);
                    resimler.ForEach(r => r.Dispose());
                    isler.Add(IsEkle(app, yazici.Id, sablon.Kod, resimler.Count, Zip(sayfalar), user.Id()));
                }
                else
                {
                    foreach (var (u, adet) in urunler)
                    {
                        using var bmp = cizici.Ciz(u, tasarim, ayar.NoktaPerMm, ayar.Dondur90);
                        var icerik = yazici.Dil == "EPL"
                            ? YaziciKomutlari.Epl(bmp, boslukNokta: 16, adet)
                            : YaziciKomutlari.Tspl(bmp, ayar.Dondur90 ? tasarim.YukseklikMm : tasarim.GenislikMm,
                                                   ayar.Dondur90 ? tasarim.GenislikMm : tasarim.YukseklikMm, adet);
                        isler.Add(IsEkle(app, yazici.Id, sablon.Kod, adet, icerik, user.Id()));
                    }
                }

                log.Yaz(user.Id(), "etiket", new { istek.YaziciId, istek.Sablon, adet = urunler.Sum(x => x.Adet), bulunamayan });
                return new BasmaSonucu(isler, urunler.Sum(x => x.Adet), bulunamayan);
            });
        });

        g.MapGet("/isler", (int? ilk, ClaimsPrincipal user, Db db) =>
        {
            using var conn = db.App();
            return Results.Ok(conn.Query<Is>("""
                SELECT TOP (@ilk) e.Id, y.Ad AS Yazici, e.Sablon, e.Adet, CAST(e.Durum AS int) AS Durum, e.Hata, e.OlusturmaZamani, e.BitisZamani,
                       k.AdSoyad AS Olusturan
                FROM EtiketIsleri e JOIN Yazicilar y ON y.Id = e.YaziciId JOIN YazdirmaAjanlari a ON a.Id = y.AjanId
                JOIN Kullanicilar k ON k.Id = e.OlusturanId
                WHERE (@depo IS NULL OR a.DepoNo = @depo)
                ORDER BY e.Id DESC
                """, new { ilk = Math.Clamp(ilk ?? 50, 1, 500), depo = user.MagazaKullanicisi() ? user.Depo() : null }));
        });

        // ---- Mağaza yazdırma ajanı (kullanıcı değil, ajan anahtarı ile girer) ----
        var ajan = app.MapGroup("/api/ajan").AllowAnonymous().WithTags("Yazdırma ajanı");

        ajan.MapGet("/isler", (HttpRequest req, Db db) =>
        {
            using var conn = db.App();
            var ajanId = AjanDogrula(conn, req);
            if (ajanId is null) return Results.Unauthorized();
            conn.Execute("UPDATE YazdirmaAjanlari SET SonGorulme = SYSDATETIME() WHERE Id = @ajanId", new { ajanId });

            // Ajana verilip 10 dakikada sonuç dönmeyen işler tekrar kuyruğa girer (ajan yeniden başlatıldıysa).
            var isler = conn.Query<(long Id, string WindowsYaziciAdi, string Dil, byte[] Icerik)>("""
                UPDATE e SET Durum = 1, AlinmaZamani = SYSDATETIME()
                OUTPUT inserted.Id, y.WindowsYaziciAdi, y.Dil, inserted.Icerik
                FROM EtiketIsleri e WITH (ROWLOCK, READPAST)
                JOIN Yazicilar y ON y.Id = e.YaziciId
                WHERE y.AjanId = @ajanId
                  AND (e.Durum = 0 OR (e.Durum = 1 AND e.AlinmaZamani < DATEADD(minute, -10, SYSDATETIME())));
                """, new { ajanId });
            return Results.Ok(isler.Select(i => new AjanIsi(i.Id, i.WindowsYaziciAdi, i.Dil, Convert.ToBase64String(i.Icerik))));
        });

        ajan.MapPost("/isler/{id:long}/sonuc", (long id, AjanSonucu s, HttpRequest req, Db db) =>
        {
            using var conn = db.App();
            var ajanId = AjanDogrula(conn, req);
            if (ajanId is null) return Results.Unauthorized();
            var n = conn.Execute("""
                UPDATE e SET Durum = @durum, Hata = @hata, BitisZamani = SYSDATETIME()
                FROM EtiketIsleri e JOIN Yazicilar y ON y.Id = e.YaziciId
                WHERE e.Id = @id AND y.AjanId = @ajanId
                """, new { id, ajanId, durum = s.Basarili ? 2 : 3, hata = s.Hata?[..Math.Min(s.Hata.Length, 500)] });
            return n == 0 ? Results.NotFound() : Results.NoContent();
        });

        // ---- Yönetim: ajan ve yazıcı tanımları ----
        var y = app.MapGroup("/api/yonetim").RequireAuthorization(Roller.Admin).WithTags("Yönetim");

        y.MapPost("/ajanlar", (AjanKaydi k, Db db) =>
        {
            var anahtar = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
            using var conn = db.App();
            var id = conn.ExecuteScalar<int>("""
                INSERT INTO YazdirmaAjanlari (Ad, DepoNo, AnahtarHash) OUTPUT INSERTED.Id VALUES (@Ad, @DepoNo, @hash)
                """, new { k.Ad, k.DepoNo, hash = BCrypt.Net.BCrypt.HashPassword(anahtar) });
            // Anahtar yalnızca bu cevapta görünür; ajanın ayar dosyasına yazılır.
            return Results.Ok(new { id, k.Ad, anahtar });
        });

        y.MapGet("/ajanlar", (Db db) =>
        {
            using var conn = db.App();
            return Results.Ok(conn.Query("SELECT Id, Ad, DepoNo, Aktif, SonGorulme FROM YazdirmaAjanlari ORDER BY Ad"));
        });

        y.MapPost("/yazicilar", (YaziciKaydi k, Db db) =>
        {
            if (k.Tur is not ("etiket" or "a4")) return Results.BadRequest("Tur: etiket ya da a4.");
            if (k.Dil is not ("TSPL" or "EPL" or "PDF")) return Results.BadRequest("Dil: TSPL (Xprinter), EPL (Argox) ya da PDF (A4).");
            using var conn = db.App();
            var id = conn.ExecuteScalar<int>("""
                INSERT INTO Yazicilar (AjanId, Ad, WindowsYaziciAdi, Tur, Dil) OUTPUT INSERTED.Id
                VALUES (@AjanId, @Ad, @WindowsYaziciAdi, @Tur, @Dil)
                """, k);
            return Results.Ok(new { id });
        });
    }

    private static long IsEkle(System.Data.IDbConnection conn, int yaziciId, string sablon, int adet, byte[] icerik, int kullaniciId) =>
        conn.ExecuteScalar<long>("""
            INSERT INTO EtiketIsleri (YaziciId, Sablon, Adet, Icerik, OlusturanId) OUTPUT INSERTED.Id
            VALUES (@yaziciId, @sablon, @adet, @icerik, @kullaniciId)
            """, new { yaziciId, sablon, adet, icerik, kullaniciId });

    private static int? AjanDogrula(System.Data.IDbConnection conn, HttpRequest req)
    {
        var ad = req.Headers["X-Ajan"].ToString();
        var anahtar = req.Headers["X-Anahtar"].ToString();
        if (ad.Length == 0 || anahtar.Length == 0) return null;
        var kayit = conn.QuerySingleOrDefault<(int Id, string AnahtarHash)>(
            "SELECT Id, AnahtarHash FROM YazdirmaAjanlari WHERE Ad = @ad AND Aktif = 1", new { ad });
        return kayit.Id != 0 && BCrypt.Net.BCrypt.Verify(anahtar, kayit.AnahtarHash) ? kayit.Id : null;
    }

    internal static IResult Png(EtiketCizici cizici, Stok.Urun u, EtiketTasarimi t, EtiketAyarlari ayar)
    {
        using var bmp = cizici.Ciz(u, t, ayar.NoktaPerMm, false);
        using var img = SKImage.FromBitmap(bmp);
        return Results.File(img.Encode(SKEncodedImageFormat.Png, 90).ToArray(), "image/png");
    }

    private static byte[] Zip(IReadOnlyList<byte[]> sayfalar)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            for (var i = 0; i < sayfalar.Count; i++)
            {
                using var s = z.CreateEntry($"sayfa{i + 1:000}.png", CompressionLevel.Fastest).Open();
                s.Write(sayfalar[i]);
            }
        return ms.ToArray();
    }
}
