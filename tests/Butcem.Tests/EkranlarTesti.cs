using System.Net;
using System.Net.Http.Json;

namespace Butcem.Tests;

/// <summary>
/// Ana yapı kontrolü: her ekranın okuma uçları cevap veriyor, yetkiler ve doğrulamalar eskisi gibi çalışıyor.
/// Bu testler Mikro'ya yazmaz (kayıt isteklerinin hepsi doğrulamada reddedilir).
/// </summary>
[Collection(nameof(ApiKoleksiyonu))]
public sealed class EkranlarTesti(ApiFabrikasi api)
{
    private static string Gun(int geri = 0) => DateTime.Today.AddDays(-geri).ToString("yyyy-MM-dd");

    public static TheoryData<string> OkumaUclari => new()
    {
        "/api/ben",
        "/api/depolar",
        "/api/yazma/durum",
        "/api/kullanicilar",
        "/api/kullanicilar/yetkiler",
        $"/api/satis?bas={Gun(7)}&bit={Gun()}",
        $"/api/satis?bas={Gun(7)}&bit={Gun()}&kirilim=Urun&ilk=20",
        "/api/cari?ara=a&ilk=5",
        "/api/cek",
        "/api/cek/portfoy",
        "/api/cek/bankalar",
        "/api/sevk",
        "/api/fatura/bekleyen-irsaliyeler",
        "/api/fiyat",
        "/api/etiket/degisenler",
        "/api/etiket/yazicilar",
        "/api/etiket/isler",
        "/api/yonetim/ajanlar",
        "/api/siparis",
        "/api/siparis/oneri?depo=1",
        "/api/sayim",
        "/api/sayim/acik?depo=1",
        "/api/sayim/urunler",
        $"/api/karlilik?bas={Gun(7)}&bit={Gun()}",
        $"/api/karlilik?bas={Gun(7)}&bit={Gun()}&kirilim=Urun&ilk=50",
        $"/api/karlilik?bas={Gun(7)}&bit={Gun()}&kirilim=Gun&tur=Perakende&depo=1",
    };

    [Theory]
    [MemberData(nameof(OkumaUclari))]
    public async Task Okuma_ucu_cevap_verir(string yol)
    {
        var r = await (await api.Admin()).GetAsync(yol);
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"{yol} → {(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task Saglik_kontrolu_mikro_ve_app_ok()
    {
        var d = await api.Anonim().GetFromJsonAsync<Dictionary<string, string>>("/api/saglik");
        Assert.Equal("ok", d!["mikro"]);
        Assert.Equal("ok", d["app"]);
    }

    [Theory]
    [InlineData("/api/siparis")]
    [InlineData("/api/sevk")]
    [InlineData("/api/fiyat")]
    [InlineData("/api/kullanicilar")]
    public async Task Girissiz_istek_reddedilir(string yol) =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Anonim().GetAsync(yol)).StatusCode);

    [Theory]
    [InlineData("/api/fiyat")]
    [InlineData("/api/kullanicilar")]
    [InlineData("/api/cek")]
    [InlineData("/api/fatura/bekleyen-irsaliyeler")]
    public async Task Yetkisiz_ekran_403(string yol) =>
        Assert.Equal(HttpStatusCode.Forbidden, (await (await api.Magaza()).GetAsync(yol)).StatusCode);

    [Fact]
    public async Task Magaza_yalniz_kendi_deposunun_siparisini_gorur()
    {
        var m = await api.Magaza();
        Assert.Equal(HttpStatusCode.OK, (await m.GetAsync($"/api/siparis/oneri?depo={ApiFabrikasi.TestMagazaDepo}")).StatusCode);
        var baska = await m.GetAsync("/api/siparis/oneri?depo=4");
        Assert.Equal(HttpStatusCode.BadRequest, baska.StatusCode);
        Assert.Contains("kendi deponuz", await baska.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Magaza_baska_deponun_sayimini_goremez_ve_onaylayamaz()
    {
        var m = await api.Magaza();
        Assert.Equal(HttpStatusCode.OK, (await m.GetAsync($"/api/sayim/acik?depo={ApiFabrikasi.TestMagazaDepo}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await m.GetAsync("/api/sayim/acik?depo=4")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await m.PostAsJsonAsync("/api/sayim", new { depo = 4 })).StatusCode);
    }

    [Fact]
    public async Task Magazaya_sayim_onayi_verilemez()
    {
        var r = await (await api.Admin()).PostAsJsonAsync("/api/kullanicilar", new
        {
            kullaniciAdi = "test_gecersiz", adSoyad = "x", rol = "magaza", depoNo = 1, aktif = true, sifre = "Gecersiz_2026!", yetkiler = new[] { "sayim_onay" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("Sayım onayı", await r.Content.ReadAsStringAsync());
    }

    public static TheoryData<string, object, string> HataliKayitlar => new()
    {
        { "/api/siparis", new { depo = 1, kaynakTur = "tedarikci", kaynakKod = "320.001", satirlar = Array.Empty<object>() }, "En az bir ürün" },
        { "/api/siparis", new { depo = 1, kaynakTur = "depo", kaynakKod = "1", satirlar = new[] { new { urun = "x", miktar = 1 } } }, "kendi deposu olamaz" },
        { "/api/siparis", new { depo = 1, kaynakTur = "tedarikci", kaynakKod = "320.001", satirlar = new[] { new { urun = "x", miktar = 0 } } }, "sıfırdan büyük" },
        { "/api/sevk", new { cikisDepo = 1, girisDepo = 1, satirlar = new[] { new { urun = "x", miktar = 1 } } }, "aynı olamaz" },
        { "/api/fiyat", new { satirlar = new[] { new { urun = "x", yeniFiyat = -1 } } }, "sıfırdan büyük" },
        { "/api/fiyat", new { tarih = DateTime.Today.AddDays(3), satirlar = new[] { new { urun = "x", yeniFiyat = 10 } } }, "İleri tarihli" },
    };

    [Theory]
    [MemberData(nameof(HataliKayitlar))]
    public async Task Hatali_kayit_aciklamayla_reddedilir(string yol, object govde, string mesaj)
    {
        var r = await (await api.Admin()).PostAsJsonAsync(yol, govde);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains(mesaj, await r.Content.ReadAsStringAsync());
    }
}
