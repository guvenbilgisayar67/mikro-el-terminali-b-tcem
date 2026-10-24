using System.Text.Json.Serialization;
using Butcem.Api.Etiket;
using Butcem.Api.Features;
using Butcem.Api.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;

// Servis olarak çalışırken çalışma klasörü System32 olur; ayarlar ve wwwroot exe'nin yanından okunur.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService()
        ? AppContext.BaseDirectory : null,
});

// Windows'ta servis olarak çalışabilir (sc create ...); konsoldan da çalışır.
builder.Host.UseWindowsService(o => o.ServiceName = "ButcemApi");

var jwt = builder.Configuration.GetSection("Jwt").Get<JwtAyarlari>()
          ?? throw new InvalidOperationException("Jwt ayarları eksik (appsettings.json).");
if (jwt.Anahtar.Length < 32) throw new InvalidOperationException("Jwt:Anahtar en az 32 karakter olmalı.");

builder.Services.AddSingleton(jwt);
builder.Services.AddSingleton<TokenServisi>();
builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<IslemLogu>();
builder.Services.AddSingleton<EtiketCizici>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o => o.TokenValidationParameters = TokenServisi.Dogrulama(jwt));
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Roller.Admin, p => p.RequireRole(Roller.Admin))
    .AddPolicy(Roller.RaporOkuyabilir, p => p.RequireRole(Roller.Admin, Roller.Merkez))
    .AddPolicy(Roller.Herkes, p => p.RequireAuthenticatedUser());

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseExceptionHandler();
app.UseSwagger();
app.UseSwaggerUI();
app.UseDefaultFiles();
app.UseStaticFiles();   // wwwroot: web dashboard
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/saglik", (Db db) =>
{
    var durum = new Dictionary<string, string>();
    void Dene(string ad, Func<System.Data.IDbConnection> ac)
    {
        try { using var c = ac(); durum[ad] = "ok"; }
        catch (Exception ex) { durum[ad] = ex.Message; }
    }
    Dene("mikro", db.Mikro);
    Dene("app", db.App);
    foreach (var k in db.OrionKaynaklari) Dene($"orion:{k.Ad}", () => db.Orion(k));
    return Results.Ok(durum);
}).AllowAnonymous();

Kullanicilar.Map(app);
SatisRaporu.Map(app);
GunlukCiro.Map(app);
CariEkstre.Map(app);
CekRaporu.Map(app);
Stok.Map(app);
EtiketBaskisi.Map(app);

app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
