using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;

namespace Butcem.Api.Infrastructure;

/// <summary>
/// Kullanıcı tipi. Admin her şeyi yapar ve kullanıcıları yönetir. Merkez ve mağaza kullanıcıları yalnızca kendilerine
/// verilen ekranları (<see cref="Yetkiler"/>) kullanır; mağaza kullanıcısı ayrıca yalnız kendi deposunu görür.
/// </summary>
public static class Roller
{
    public const string Admin = "admin";
    public const string Merkez = "merkez";   // tüm depolar
    public const string Magaza = "magaza";   // tek depo (DepoNo)

    public const string Herkes = "herkes";
}

/// <summary>Ekran / işlem yetkileri. Kullanicilar.Yetkiler sütununda virgülle ayrılmış tutulur.</summary>
public static class Yetkiler
{
    public const string Ciro = "ciro";
    public const string SatisRaporu = "satis_raporu";
    public const string CariEkstre = "cari_ekstre";
    public const string CekRaporu = "cek_raporu";
    public const string CekGiris = "cek_giris";
    public const string CekCikis = "cek_cikis";
    public const string AlisFatura = "alis_fatura";
    public const string SatisFatura = "satis_fatura";
    public const string Sevk = "sevk";
    public const string FiyatDegisikligi = "fiyat_degisikligi";
    public const string Etiket = "etiket";
    public const string Siparis = "siparis";

    public static readonly IReadOnlyList<(string Kod, string Ad)> Liste =
    [
        (Ciro, "Günün cirosu"),
        (SatisRaporu, "Satış raporu"),
        (CariEkstre, "Cari ekstre"),
        (CekRaporu, "Çek raporu"),
        (CekGiris, "Çek girişi"),
        (CekCikis, "Çek çıkışı"),
        (AlisFatura, "Alış irsaliyesi ve faturası"),
        (SatisFatura, "Satış faturası"),
        (Sevk, "Depolar arası sevk"),
        (FiyatDegisikligi, "Fiyat değişikliği"),
        (Etiket, "Etiket basma"),
        (Siparis, "Sipariş (öneri, kayıt, gönderim)"),
    ];

    public static readonly string[] Tumu = Liste.Select(x => x.Kod).ToArray();

    /// <summary>Evrak ekranlarında cari seçebilmek için cari aramasını açan yetkiler.</summary>
    public static readonly string[] CariArayabilir = [CariEkstre, CekGiris, CekCikis, AlisFatura, SatisFatura, Siparis];

    /// <summary>Yetkiler sütunu boşsa (eski kayıtlar) kullanıcı tipinin eski varsayılanı.</summary>
    public static string[] Varsayilan(string rol) => rol == Roller.Magaza ? [Sevk, Etiket] : Tumu;

    /// <summary>Sütundaki değeri çözer; admin her zaman hepsine sahiptir. Tanınmayan kodlar atılır.</summary>
    public static string[] Coz(string rol, string? sutun) =>
        rol == Roller.Admin ? Tumu
        : sutun is null ? Varsayilan(rol)
        : sutun.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(Tumu.Contains).Distinct().ToArray();

    /// <summary>Uç noktayı, verilen yetkilerden en az birine sahip kullanıcılara açar.</summary>
    public static TBuilder RequireYetki<TBuilder>(this TBuilder b, params string[] yetkiler) where TBuilder : IEndpointConventionBuilder =>
        b.RequireAuthorization(p => p.RequireAuthenticatedUser()
            .RequireAssertion(c => yetkiler.Any(y => c.User.HasClaim(YetkiClaim, y))));

    public const string YetkiClaim = "yetki";
}

public sealed record JwtAyarlari(string Anahtar, string Yayinci, int SureSaat);

public sealed class TokenServisi(JwtAyarlari ayar)
{
    /// <summary>Token yalnızca kullanıcıyı tanıtır; rol, depo ve yetkiler her istekte veritabanından okunur
    /// (<see cref="KullaniciYukleyici"/>), böylece yetki değişikliği ve pasife alma hemen geçerli olur.</summary>
    public string Uret(int kullaniciId, string kullaniciAdi)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, kullaniciId.ToString()),
            new(ClaimTypes.Name, kullaniciAdi),
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ayar.Anahtar));
        var token = new JwtSecurityToken(
            issuer: ayar.Yayinci,
            audience: ayar.Yayinci,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(ayar.SureSaat),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static TokenValidationParameters Dogrulama(JwtAyarlari ayar) => new()
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = ayar.Yayinci,
        ValidAudience = ayar.Yayinci,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ayar.Anahtar)),
        ClockSkew = TimeSpan.FromMinutes(2),
    };
}

public static class KullaniciBilgisi
{
    public static int Id(this ClaimsPrincipal u) =>
        int.Parse(u.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? u.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");

    /// <summary>Mağaza kullanıcısının bağlı olduğu depo; merkez/admin için null.</summary>
    public static int? Depo(this ClaimsPrincipal u) =>
        int.TryParse(u.FindFirstValue("depo"), out var d) ? d : null;

    public static bool MagazaKullanicisi(this ClaimsPrincipal u) => u.IsInRole(Roller.Magaza);

    /// <summary>Mağaza kullanıcısı yalnız kendi deposuna evrak girebilir.</summary>
    public static void DepoYetkisi(this ClaimsPrincipal u, int depo)
    {
        if (u.MagazaKullanicisi() && u.Depo() != depo)
            throw new IslemHatasi($"Yalnızca kendi deponuz ({u.Depo()}) için evrak girebilirsiniz.");
    }
}

/// <summary>
/// Geçerli token'ın kullanıcısını ButcemApp.Kullanicilar'dan okur (30 sn önbellek) ve rol, depo, yetki claim'lerini
/// oradan kurar. Kullanıcı silinmiş ya da pasifse istek reddedilir.
/// </summary>
public sealed class KullaniciYukleyici(Db db, IMemoryCache onbellek)
{
    private sealed record Satir(int Id, string KullaniciAdi, string Rol, int? DepoNo, bool Aktif, string? Yetkiler);

    public static void Unut(IMemoryCache onbellek, int id) => onbellek.Remove(Anahtar(id));
    private static string Anahtar(int id) => $"kullanici:{id}";

    public async Task Dogrula(TokenValidatedContext ctx)
    {
        var id = ctx.Principal?.Id() ?? 0;
        var k = await onbellek.GetOrCreateAsync(Anahtar(id), async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            using var conn = db.App();
            return await conn.QuerySingleOrDefaultAsync<Satir>(
                "SELECT Id, KullaniciAdi, Rol, DepoNo, Aktif, Yetkiler FROM Kullanicilar WHERE Id = @id", new { id });
        });
        if (k is null || !k.Aktif) { ctx.Fail("Kullanıcı pasif ya da silinmiş."); return; }

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, k.Id.ToString()),
            new(ClaimTypes.Name, k.KullaniciAdi),
            new(ClaimTypes.Role, k.Rol),
        };
        if (k.Rol == Roller.Magaza && k.DepoNo is not null) claims.Add(new Claim("depo", k.DepoNo.Value.ToString()));
        claims.AddRange(Yetkiler.Coz(k.Rol, k.Yetkiler).Select(y => new Claim(Yetkiler.YetkiClaim, y)));
        ctx.Principal = new ClaimsPrincipal(new ClaimsIdentity(claims, JwtBearerDefaults.AuthenticationScheme, ClaimTypes.Name, ClaimTypes.Role));
    }
}
