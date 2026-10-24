using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Butcem.Api.Infrastructure;

public static class Roller
{
    public const string Admin = "admin";     // her şey + kullanıcı yönetimi
    public const string Merkez = "merkez";   // raporlar, cari, çek, fatura
    public const string Magaza = "magaza";   // el terminali: barkod, etiket, sevk (kendi deposu)

    public const string RaporOkuyabilir = "rapor";
    public const string Herkes = "herkes";
}

public sealed record JwtAyarlari(string Anahtar, string Yayinci, int SureSaat);

public sealed class TokenServisi(JwtAyarlari ayar)
{
    public string Uret(int kullaniciId, string kullaniciAdi, string rol, int? depoNo)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, kullaniciId.ToString()),
            new(ClaimTypes.Name, kullaniciAdi),
            new(ClaimTypes.Role, rol),
        };
        if (depoNo is not null) claims.Add(new Claim("depo", depoNo.Value.ToString()));

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
}
