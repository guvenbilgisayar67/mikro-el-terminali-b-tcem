using System.Security.Claims;
using Butcem.Api.Infrastructure;
using Dapper;

namespace Butcem.Api.Features;

/// <summary>Uygulamanın kendi kullanıcıları (ButcemApp.Kullanicilar). Mikro kullanıcıları kullanılmaz.</summary>
public static class Kullanicilar
{
    public sealed record GirisIstegi(string KullaniciAdi, string Sifre);
    public sealed record GirisCevabi(string Token, string AdSoyad, string Rol, int? DepoNo);
    public sealed record Kullanici(int Id, string KullaniciAdi, string AdSoyad, string Rol, int? DepoNo, bool Aktif, DateTime? SonGiris);
    public sealed record KullaniciKaydi(string KullaniciAdi, string AdSoyad, string Rol, int? DepoNo, bool Aktif, string? Sifre);
    public sealed record SifreDegistir(string EskiSifre, string YeniSifre);

    private sealed record Kayit(int Id, string KullaniciAdi, string SifreHash, string AdSoyad, string Rol, int? DepoNo, bool Aktif);

    public static void Map(WebApplication app)
    {
        app.MapPost("/api/giris", (GirisIstegi g, Db db, TokenServisi tokens, IslemLogu log) =>
        {
            using var conn = db.App();
            var k = conn.QuerySingleOrDefault<Kayit>(
                "SELECT Id, KullaniciAdi, SifreHash, AdSoyad, Rol, DepoNo, Aktif FROM Kullanicilar WHERE KullaniciAdi = @KullaniciAdi",
                new { g.KullaniciAdi });
            if (k is null || !k.Aktif || !BCrypt.Net.BCrypt.Verify(g.Sifre, k.SifreHash))
            {
                log.Yaz(null, "giris_hatali", new { g.KullaniciAdi });
                return Results.Unauthorized();
            }
            conn.Execute("UPDATE Kullanicilar SET SonGiris = SYSDATETIME() WHERE Id = @Id", new { k.Id });
            log.Yaz(k.Id, "giris", null);
            return Results.Ok(new GirisCevabi(tokens.Uret(k.Id, k.KullaniciAdi, k.Rol, k.DepoNo), k.AdSoyad, k.Rol, k.DepoNo));
        })
        .AllowAnonymous()
        .WithTags("Giriş");

        app.MapPost("/api/sifre", (SifreDegistir s, ClaimsPrincipal user, Db db) =>
        {
            if (s.YeniSifre.Length < 8) return Results.BadRequest("Yeni şifre en az 8 karakter olmalı.");
            using var conn = db.App();
            var hash = conn.ExecuteScalar<string>("SELECT SifreHash FROM Kullanicilar WHERE Id = @id", new { id = user.Id() });
            if (hash is null || !BCrypt.Net.BCrypt.Verify(s.EskiSifre, hash)) return Results.BadRequest("Eski şifre yanlış.");
            conn.Execute("UPDATE Kullanicilar SET SifreHash = @h WHERE Id = @id",
                new { h = BCrypt.Net.BCrypt.HashPassword(s.YeniSifre), id = user.Id() });
            return Results.NoContent();
        })
        .RequireAuthorization(Roller.Herkes)
        .WithTags("Giriş");

        var g = app.MapGroup("/api/kullanicilar").RequireAuthorization(Roller.Admin).WithTags("Yönetim");

        g.MapGet("/", (Db db) =>
        {
            using var conn = db.App();
            return Results.Ok(conn.Query<Kullanici>(
                "SELECT Id, KullaniciAdi, AdSoyad, Rol, DepoNo, Aktif, SonGiris FROM Kullanicilar ORDER BY KullaniciAdi"));
        });

        g.MapPost("/", (KullaniciKaydi k, Db db) =>
        {
            if (Dogrula(k, yeni: true) is { } hata) return Results.BadRequest(hata);
            using var conn = db.App();
            var id = conn.ExecuteScalar<int>("""
                INSERT INTO Kullanicilar (KullaniciAdi, SifreHash, AdSoyad, Rol, DepoNo, Aktif)
                OUTPUT INSERTED.Id VALUES (@KullaniciAdi, @hash, @AdSoyad, @Rol, @DepoNo, @Aktif);
                """, new { k.KullaniciAdi, hash = BCrypt.Net.BCrypt.HashPassword(k.Sifre), k.AdSoyad, k.Rol, k.DepoNo, k.Aktif });
            return Results.Created($"/api/kullanicilar/{id}", new { id });
        });

        g.MapPut("/{id:int}", (int id, KullaniciKaydi k, Db db) =>
        {
            if (Dogrula(k, yeni: false) is { } hata) return Results.BadRequest(hata);
            using var conn = db.App();
            var n = conn.Execute("""
                UPDATE Kullanicilar SET KullaniciAdi = @KullaniciAdi, AdSoyad = @AdSoyad, Rol = @Rol, DepoNo = @DepoNo,
                       Aktif = @Aktif, SifreHash = COALESCE(@hash, SifreHash)
                WHERE Id = @id;
                """, new { id, k.KullaniciAdi, k.AdSoyad, k.Rol, k.DepoNo, k.Aktif,
                           hash = string.IsNullOrEmpty(k.Sifre) ? null : BCrypt.Net.BCrypt.HashPassword(k.Sifre) });
            return n == 0 ? Results.NotFound() : Results.NoContent();
        });
    }

    private static string? Dogrula(KullaniciKaydi k, bool yeni)
    {
        if (string.IsNullOrWhiteSpace(k.KullaniciAdi)) return "Kullanıcı adı boş olamaz.";
        if (k.Rol is not (Roller.Admin or Roller.Merkez or Roller.Magaza)) return "Rol: admin, merkez ya da magaza.";
        if (k.Rol == Roller.Magaza && k.DepoNo is null) return "Mağaza kullanıcısına depo seçilmeli.";
        if (yeni && (k.Sifre is null || k.Sifre.Length < 8)) return "Şifre en az 8 karakter olmalı.";
        if (!yeni && k.Sifre is { Length: > 0 and < 8 }) return "Şifre en az 8 karakter olmalı.";
        return null;
    }
}
