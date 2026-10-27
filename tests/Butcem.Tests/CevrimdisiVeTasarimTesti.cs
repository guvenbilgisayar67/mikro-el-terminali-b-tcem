using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Butcem.Api.Etiket;
using Butcem.Api.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Butcem.Tests;

/// <summary>
/// v0.7: terminalin çevrimdışı çalışması (ürün listesi, tekrar gönderimde tek işlem) ve panelden düzenlenen etiket tasarımları.
/// Mikro'ya yazmaz: tekrar koruması etiket kuyruğunda (uygulama veritabanı) ve reddedilen sevkte denenir.
/// </summary>
[Collection(nameof(ApiKoleksiyonu))]
public sealed class CevrimdisiVeTasarimTesti(ApiFabrikasi api)
{
    private static HttpRequestMessage Islem(string yol, object govde, Guid id)
    {
        var m = new HttpRequestMessage(HttpMethod.Post, yol) { Content = JsonContent.Create(govde) };
        m.Headers.Add("X-Istemci-Id", id.ToString());
        return m;
    }

    [Fact]
    public async Task Katalog_magazaya_kendi_deposunun_stogu_ile_gelir()
    {
        var j = await (await api.Magaza()).GetFromJsonAsync<JsonElement>("/api/katalog?depo=3");
        Assert.Equal(ApiFabrikasi.TestMagazaDepo, j.GetProperty("depo").GetInt32());
        var urunler = j.GetProperty("urunler");
        Assert.True(urunler.GetArrayLength() > 100);
        var ilk = urunler[0];
        Assert.Equal(7, ilk.GetArrayLength());
        Assert.Equal(JsonValueKind.Number, ilk[4].ValueKind);   // fiyat
        Assert.Equal(JsonValueKind.Number, ilk[6].ValueKind);   // depo stoğu
    }

    [Fact]
    public async Task Etiket_basimi_ayni_kimlikle_iki_kez_gelirse_bir_kez_kuyruga_girer()
    {
        var admin = await api.Admin();
        var yaziciId = await TestYazicisi(admin);
        var barkod = (await admin.GetFromJsonAsync<JsonElement>("/api/katalog")).GetProperty("urunler")[0][0].GetString()!;
        var govde = new { yaziciId, sablon = "raf", kalemler = new[] { new { barkod, adet = 1 } } };
        var id = Guid.NewGuid();

        var r1 = await admin.SendAsync(Islem("/api/etiket/bas", govde, id));
        var r2 = await admin.SendAsync(Islem("/api/etiket/bas", govde, id));
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.True(r2.Headers.Contains("X-Tekrar"));
        var s1 = await r1.Content.ReadFromJsonAsync<EtiketBaskisi.BasmaSonucu>();
        var s2 = await r2.Content.ReadFromJsonAsync<EtiketBaskisi.BasmaSonucu>();
        Assert.Equal(s1!.Isler, s2!.Isler);

        // Başlıksız (panel) istek her seferinde yeni iş açar; eski davranış değişmedi.
        var r3 = await admin.PostAsJsonAsync("/api/etiket/bas", govde);
        var s3 = await r3.Content.ReadFromJsonAsync<EtiketBaskisi.BasmaSonucu>();
        Assert.NotEqual(s1.Isler, s3!.Isler);

        // Aynı kimlik başka bir işlemde kullanılamaz.
        var r4 = await admin.SendAsync(Islem("/api/sevk", new { cikisDepo = 1, girisDepo = 3, satirlar = new[] { new { urun = barkod, miktar = 1 } } }, id));
        Assert.Equal(HttpStatusCode.BadRequest, r4.StatusCode);
    }

    [Fact]
    public async Task Reddedilen_sevk_kimligi_kilitlemez_duzeltip_yeniden_gonderilebilir()
    {
        var magaza = await api.Magaza();
        var id = Guid.NewGuid();
        var govde = new { cikisDepo = ApiFabrikasi.TestMagazaDepo, girisDepo = 3, satirlar = new[] { new { urun = "BOYLE-BIR-URUN-YOK-123", miktar = 1 } } };
        for (var i = 0; i < 2; i++)
        {
            var r = await magaza.SendAsync(Islem("/api/sevk", govde, id));
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);   // "bulunamadı"; 409 (işleniyor) değil
            Assert.DoesNotContain("henüz sonuçlanmadı", await r.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Yerlesik_tasarimlar_listelenir_ve_onizlenir()
    {
        var admin = await api.Admin();
        var l = await admin.GetFromJsonAsync<List<JsonElement>>("/api/etiket/sablonlar");
        Assert.Contains(l!, s => s.GetProperty("kod").GetString() == "raf");
        Assert.Contains(l!, s => s.GetProperty("kod").GetString() == "manav");
    }

    [Fact]
    public async Task Tasarim_kaydedilir_basim_ekraninda_gorunur_ve_silinir()
    {
        var admin = await api.Admin();
        const string kod = "test-tasarim";
        var tasarim = new
        {
            genislikMm = 58, yukseklikMm = 40,
            ogeler = new object[]
            {
                new { tur = "yazi", metin = "{urunAdi}", x = 2, y = 2, genislik = 54, yukseklik = 10, boyut = 4, satirSayisi = 2, kalin = true },
                new { tur = "yazi", metin = "{fiyat} ₺", x = 2, y = 14, genislik = 54, yukseklik = 12, boyut = 10, hiza = "orta", ters = true },
                new { tur = "kutu", x = 1, y = 1, genislik = 56, yukseklik = 38, kalinlik = 0.3 },
                new { tur = "barkod", x = 10, y = 28, genislik = 38, yukseklik = 11, hiza = "orta" },
            },
        };
        var on = await admin.PostAsJsonAsync("/api/etiket-tasarim/onizleme", new { tasarim });
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.Equal("image/png", on.Content.Headers.ContentType!.MediaType);

        (await admin.PutAsJsonAsync($"/api/etiket-tasarim/{kod}", new { ad = "Test tasarım", aktif = true, sira = 99, tasarim })).EnsureSuccessStatusCode();
        try
        {
            var l = await admin.GetFromJsonAsync<List<JsonElement>>("/api/etiket/sablonlar");
            var t = l!.Single(s => s.GetProperty("kod").GetString() == kod);
            Assert.Equal(58, t.GetProperty("genislikMm").GetDouble());
        }
        finally
        {
            Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/etiket-tasarim/{kod}")).StatusCode);
        }
        var sonra = await admin.GetFromJsonAsync<List<JsonElement>>("/api/etiket/sablonlar");
        Assert.DoesNotContain(sonra!, s => s.GetProperty("kod").GetString() == kod);
    }

    [Theory]
    [InlineData("{\"genislikMm\":5,\"yukseklikMm\":39,\"ogeler\":[]}")]
    [InlineData("{\"genislikMm\":100,\"yukseklikMm\":39,\"ogeler\":[{\"tur\":\"resim\"}]}")]
    [InlineData("{\"genislikMm\":100,\"yukseklikMm\":39,\"ogeler\":[{\"tur\":\"yazi\",\"metin\":\"{yok}\"}]}")]
    [InlineData("{\"genislikMm\":100,\"yukseklikMm\":39,\"ogeler\":[{\"tur\":\"yazi\",\"metin\":\"a\",\"boyut\":0}]}")]
    [InlineData("{\"genislikMm\":100,\"yukseklikMm\":39,\"ogeler\":[{\"tur\":\"barkod\",\"x\":500}]}")]
    public async Task Gecersiz_tasarim_kaydedilmez(string tasarimJson)
    {
        var admin = await api.Admin();
        var govde = $"{{\"ad\":\"x\",\"aktif\":true,\"sira\":1,\"tasarim\":{tasarimJson}}}";
        var r = await admin.PutAsync("/api/etiket-tasarim/gecersiz", new StringContent(govde, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Tasarim_ekrani_yetkisiz_kullaniciya_kapali()
    {
        var magaza = await api.Magaza();
        Assert.Equal(HttpStatusCode.Forbidden, (await magaza.GetAsync("/api/etiket-tasarim")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await magaza.PutAsJsonAsync("/api/etiket-tasarim/raf", new { })).StatusCode);
    }

    [Fact]
    public void Yerlesik_raf_tasarimi_eski_olcude_cizer_ve_doner()
    {
        var cizici = api.Services.GetRequiredService<EtiketCizici>();
        var raf = EtiketSablonlari.Yerlesikler(new EtiketAyarlari()).Single(s => s.Kod == "raf").Tasarim;
        using var duz = cizici.Ciz(EtiketTasarimlari.OrnekUrun, raf, 8, false);
        using var donuk = cizici.Ciz(EtiketTasarimlari.OrnekUrun, raf, 8, true);
        Assert.Equal((800, 312), (duz.Width, duz.Height));
        Assert.Equal((312, 800), (donuk.Width, donuk.Height));
        var siyah = 0;
        for (var y = 0; y < duz.Height; y += 2)
            for (var x = 0; x < duz.Width; x += 2)
                if (duz.GetPixel(x, y).Red < 128) siyah++;
        Assert.InRange(siyah, 2000, 40000);   // yazı ve barkod var, etiket kapkara değil
    }

    [Fact]
    public void Bos_alanli_oge_basilmaz_sabit_yazi_basilir()
    {
        var d = EtiketCizici.Degerler(EtiketTasarimlari.OrnekUrun with { Mensei = null, EskiFiyat = null });
        Assert.Null(EtiketCizici.MetniDoldur("Menşei: {mensei}", d));
        Assert.Null(EtiketCizici.MetniDoldur("Eski fiyat: {eskiFiyat} ₺", d));
        Assert.Equal("SATIŞ FİYATI", EtiketCizici.MetniDoldur("SATIŞ FİYATI", d));
        Assert.Equal("1.234,50 ₺ / ADET", EtiketCizici.MetniDoldur("{fiyat} ₺ / {birim}", d));
        Assert.Equal("1.234 50", EtiketCizici.MetniDoldur("{fiyatLira} {fiyatKurus}", d));
    }

    private static async Task<int> TestYazicisi(HttpClient admin)
    {
        var mevcut = await admin.GetFromJsonAsync<List<JsonElement>>("/api/etiket/yazicilar");
        var y = mevcut!.FirstOrDefault(x => x.GetProperty("ad").GetString() == "test_yazici");
        if (y.ValueKind != JsonValueKind.Undefined) return y.GetProperty("id").GetInt32();
        var ajan = await (await admin.PostAsJsonAsync("/api/yonetim/ajanlar", new { ad = "test_ajan", depoNo = 1 })).Content.ReadFromJsonAsync<JsonElement>();
        var r = await admin.PostAsJsonAsync("/api/yonetim/yazicilar",
            new { ajanId = ajan.GetProperty("id").GetInt32(), ad = "test_yazici", windowsYaziciAdi = "TEST", tur = "etiket", dil = "TSPL" });
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }
}
