using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Butcem.Tests;

/// <summary>
/// API'yi test ortamının ayarlarıyla (varsayılan: appsettings.Local.json, kopya veritabanı) bellekte çalıştırır.
/// Canlı veritabanına bağlı bir ayarla ÇALIŞTIRMAYIN: test, kullanıcı tablosuna "test_magaza" kullanıcısını ekler.
/// </summary>
public sealed class ApiFabrikasi : WebApplicationFactory<Program>
{
    public const string TestMagaza = "test_magaza";
    public const string TestMagazaSifre = "Test_Magaza_2026!";
    public const int TestMagazaDepo = 1;

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseEnvironment(Environment.GetEnvironmentVariable("BUTCEM_TEST_ORTAM") ?? "Local");

    private string? _admin, _magaza;

    public async Task<HttpClient> Admin() => Istemci(_admin ??= await Giris("admin", Environment.GetEnvironmentVariable("BUTCEM_TEST_ADMIN_SIFRE") ?? "Butcem2026!"));

    /// <summary>Depo 1'e bağlı, yalnız sipariş, sevk ve sayım yetkili mağaza kullanıcısı (yoksa oluşturulur, varsa yetkileri sıfırlanır).</summary>
    public async Task<HttpClient> Magaza()
    {
        if (_magaza is null)
        {
            var admin = await Admin();
            var liste = await admin.GetFromJsonAsync<List<KullaniciOzet>>("/api/kullanicilar") ?? [];
            var kayit = new
            {
                kullaniciAdi = TestMagaza, adSoyad = "Test Mağaza", rol = "magaza", depoNo = TestMagazaDepo, aktif = true,
                sifre = TestMagazaSifre, yetkiler = new[] { "siparis", "sevk", "sayim" },
            };
            var mevcut = liste.FirstOrDefault(k => k.KullaniciAdi == TestMagaza);
            var r = mevcut is null ? await admin.PostAsJsonAsync("/api/kullanicilar", kayit) : await admin.PutAsJsonAsync($"/api/kullanicilar/{mevcut.Id}", kayit);
            r.EnsureSuccessStatusCode();
            _magaza = await Giris(TestMagaza, TestMagazaSifre);
        }
        return Istemci(_magaza);
    }

    public HttpClient Anonim() => CreateClient();

    private HttpClient Istemci(string token)
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        c.Timeout = TimeSpan.FromMinutes(3);
        return c;
    }

    private async Task<string> Giris(string kullanici, string sifre)
    {
        var r = await CreateClient().PostAsJsonAsync("/api/giris", new { kullaniciAdi = kullanici, sifre });
        Assert.True(r.IsSuccessStatusCode, $"{kullanici} giriş yapamadı: {(int)r.StatusCode}");
        return (await r.Content.ReadFromJsonAsync<GirisCevabi>())!.Token;
    }

    private sealed record GirisCevabi(string Token);
    private sealed record KullaniciOzet(int Id, string KullaniciAdi);
}

[CollectionDefinition(nameof(ApiKoleksiyonu))]
public sealed class ApiKoleksiyonu : ICollectionFixture<ApiFabrikasi>;
