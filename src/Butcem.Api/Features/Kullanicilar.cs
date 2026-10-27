using System.Security.Claims;
using Butcem.Api.Infrastructure;
using Dapper;
using Microsoft.Extensions.Caching.Memory;

namespace Butcem.Api.Features;

/// <summary>Uygulamanın kendi kullanıcıları (ButcemApp.Kullanicilar). Mikro kullanıcıları kullanılmaz.</summary>
public static class Kullanicilar
{
    public sealed record GirisIstegi(string KullaniciAdi, string Sifre);
    public sealed record GirisCevabi(string Token, string AdSoyad, string Rol, int? DepoNo, string[] Yetkiler);
    public sealed record Ben(string KullaniciAdi, string AdSoyad, string Rol, int? DepoNo, string[] Yetkiler);
    public sealed record Kullanici(int Id, string KullaniciAdi, string AdSoyad, string Rol, int? DepoNo, bool Aktif, DateTime? SonGiris, string[] Yetkiler);
    public sealed record KullaniciKaydi(string KullaniciAdi, string AdSoyad, string Rol, int? DepoNo, bool Aktif, string? Sifre, string[]? Yetkiler);
    public sealed record SifreDegistir(string EskiSifre, string YeniSifre);

    private sealed record Kayit(int Id, string KullaniciAdi, string SifreHash, string AdSoyad, string Rol, int? DepoNo, bool Aktif, string? Yetkiler);
    private sealed record Liste(int Id, string KullaniciAdi, string AdSoyad, string Rol, int? DepoNo, bool Aktif, DateTime? SonGiris, string? Yetkiler);

    public static void Map(WebApplication app)
    {
        app.MapPost("/api/giris", (GirisIstegi g, Db db, TokenServisi tokens, IslemLogu log) =>
        {
            using var conn = db.App();
            var k = conn.QuerySingleOrDefault<Kayit>(
                "SELECT Id, KullaniciAdi, SifreHash, AdSoyad, Rol, DepoNo, Aktif, Yetkiler FROM Kullanicilar WHERE KullaniciAdi = @KullaniciAdi",
                new { g.KullaniciAdi });
            if (k is null || !k.Aktif || !BCrypt.Net.BCrypt.Verify(g.Sifre, k.SifreHash))
            {
                log.Yaz(null, "giris_hatali", new { g.KullaniciAdi });
                return Results.Unauthorized();
            }
            conn.Execute("UPDATE Kullanicilar SET SonGiris = SYSDATETIME() WHERE Id = @Id", new { k.Id });
            log.Yaz(k.Id, "giris", null);
            return Results.Ok(new GirisCevabi(tokens.Uret(k.Id, k.KullaniciAdi), k.AdSoyad, k.Rol, k.DepoNo, Yetkiler.Coz(k.Rol, k.Yetkiler)));
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

        // Panel ve terminal açılışta güncel yetkileri buradan alır (yetki değişikliği yeniden giriş gerektirmez).
        app.MapGet("/api/ben", (ClaimsPrincipal user, Db db) =>
        {
            using var conn = db.App();
            var ad = conn.ExecuteScalar<string>("SELECT AdSoyad FROM Kullanicilar WHERE Id = @id", new { id = user.Id() }) ?? "";
            var rol = user.FindFirstValue(ClaimTypes.Role) ?? "";
            return Results.Ok(new Ben(user.Identity!.Name!, ad, rol, user.Depo(),
                user.FindAll(Yetkiler.YetkiClaim).Select(c => c.Value).ToArray()));
        })
        .RequireAuthorization(Roller.Herkes)
        .WithTags("Giriş");

        var g = app.MapGroup("/api/kullanicilar").RequireAuthorization(Roller.Admin).WithTags("Yönetim");

        g.MapGet("/", (Db db) =>
        {
            using var conn = db.App();
            return Results.Ok(conn.Query<Liste>(
                "SELECT Id, KullaniciAdi, AdSoyad, Rol, DepoNo, Aktif, SonGiris, Yetkiler FROM Kullanicilar ORDER BY KullaniciAdi")
                .Select(k => new Kullanici(k.Id, k.KullaniciAdi, k.AdSoyad, k.Rol, k.DepoNo, k.Aktif, k.SonGiris, Yetkiler.Coz(k.Rol, k.Yetkiler))));
        });

        // Yönetim ekranındaki yetki kutucukları.
        g.MapGet("/yetkiler", () => Results.Ok(Yetkiler.Liste.Select(y => new { kod = y.Kod, ad = y.Ad })));

        g.MapPost("/", (KullaniciKaydi k, Db db) =>
        {
            if (Dogrula(k, yeni: true) is { } hata) return Results.BadRequest(hata);
            using var conn = db.App();
            var id = conn.ExecuteScalar<int>("""
                INSERT INTO Kullanicilar (KullaniciAdi, SifreHash, AdSoyad, Rol, DepoNo, Aktif, Yetkiler)
                OUTPUT INSERTED.Id VALUES (@KullaniciAdi, @hash, @AdSoyad, @Rol, @DepoNo, @Aktif, @yetkiler);
                """, new { k.KullaniciAdi, hash = BCrypt.Net.BCrypt.HashPassword(k.Sifre), k.AdSoyad, k.Rol, DepoNo = Depo(k), k.Aktif, yetkiler = Sutun(k) });
            return Results.Created($"/api/kullanicilar/{id}", new { id });
        });

        g.MapPut("/{id:int}", (int id, KullaniciKaydi k, ClaimsPrincipal user, Db db, IMemoryCache onbellek) =>
        {
            if (Dogrula(k, yeni: false) is { } hata) return Results.BadRequest(hata);
            if (id == user.Id() && (k.Rol != Roller.Admin || !k.Aktif))
                return Results.BadRequest("Kendi yönetici yetkinizi kaldıramaz, kendinizi pasife alamazsınız.");
            using var conn = db.App();
            var n = conn.Execute("""
                UPDATE Kullanicilar SET KullaniciAdi = @KullaniciAdi, AdSoyad = @AdSoyad, Rol = @Rol, DepoNo = @DepoNo,
                       Aktif = @Aktif, Yetkiler = @yetkiler, SifreHash = COALESCE(@hash, SifreHash)
                WHERE Id = @id;
                """, new { id, k.KullaniciAdi, k.AdSoyad, k.Rol, DepoNo = Depo(k), k.Aktif, yetkiler = Sutun(k),
                           hash = string.IsNullOrEmpty(k.Sifre) ? null : BCrypt.Net.BCrypt.HashPassword(k.Sifre) });
            KullaniciYukleyici.Unut(onbellek, id);
            return n == 0 ? Results.NotFound() : Results.NoContent();
        });
    }

    private static string? Dogrula(KullaniciKaydi k, bool yeni)
    {
        if (string.IsNullOrWhiteSpace(k.KullaniciAdi)) return "Kullanıcı adı boş olamaz.";
        if (k.Rol is not (Roller.Admin or Roller.Merkez or Roller.Magaza)) return "Rol: admin, merkez ya da magaza.";
        if (k.Rol == Roller.Magaza && k.DepoNo is null) return "Mağaza kullanıcısına depo seçilmeli.";
        if (k.Rol == Roller.Magaza && k.Yetkiler?.Contains(Yetkiler.Ciro) == true)
            return "Günün cirosu tüm mağazaları gösterir; mağaza kullanıcısına verilemez.";
        if (k.Rol == Roller.Magaza && k.Yetkiler?.Contains(Yetkiler.SayimOnay) == true)
            return "Sayım onayı merkezde verilir; mağaza kullanıcısına verilemez.";
        if (k.Rol != Roller.Admin && k.Yetkiler is not { Length: > 0 }) return "En az bir yetki seçin.";
        if (yeni && (k.Sifre is null || k.Sifre.Length < 8)) return "Şifre en az 8 karakter olmalı.";
        if (!yeni && k.Sifre is { Length: > 0 and < 8 }) return "Şifre en az 8 karakter olmalı.";
        return null;
    }

    private static int? Depo(KullaniciKaydi k) => k.Rol == Roller.Magaza ? k.DepoNo : null;

    // Admin için sütun boş kalır (her şeye yetkili); diğerlerinde seçilen yetkiler.
    private static string? Sutun(KullaniciKaydi k) =>
        k.Rol == Roller.Admin ? null : string.Join(',', Yetkiler.Coz(k.Rol, string.Join(',', k.Yetkiler ?? [])));
}
