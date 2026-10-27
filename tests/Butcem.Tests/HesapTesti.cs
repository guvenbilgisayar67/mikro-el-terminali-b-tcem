using Butcem.Api.Features;
using Butcem.Api.Infrastructure;

namespace Butcem.Tests;

/// <summary>Veritabanı gerektirmeyen hesaplar.</summary>
public sealed class HesapTesti
{
    [Theory]
    [InlineData(0.5, 7, 2, 0, 2)]     // 3,5 ihtiyaç − 2 stok = 1,5 → 2
    [InlineData(0.036, 7, 0, 0, 0)]   // ayda bir satan ürün: 0,25 → önerilmez
    [InlineData(1, 7, -50, 0, 7)]     // eksi stok sıfır sayılır
    [InlineData(1, 7, 3, 4, 0)]       // açık sipariş yeterli
    [InlineData(2.4, 10, 0, 0, 24)]
    public void Siparis_onerisi(decimal gunluk, int gun, decimal stok, decimal acik, decimal beklenen) =>
        Assert.Equal(beklenen, Siparisler.OnerilenMiktar(gunluk, gun, stok, acik));

    [Theory]
    [InlineData("0532 123 45 67", "905321234567")]
    [InlineData("5321234567", "905321234567")]
    [InlineData("+90 (532) 123-45-67", "905321234567")]
    [InlineData("0090 532 123 45 67", "905321234567")]
    [InlineData("123", null)]
    [InlineData("", null)]
    public void WhatsApp_telefonu(string girilen, string? beklenen) => Assert.Equal(beklenen, Siparisler.Telefon(girilen));

    [Fact]
    public void Yetkiler_eski_varsayilanlar_degismedi()
    {
        Assert.Equal(Yetkiler.Tumu, Yetkiler.Coz(Roller.Admin, "ciro"));
        Assert.Equal([Yetkiler.Sevk, Yetkiler.Etiket], Yetkiler.Coz(Roller.Magaza, null));
        Assert.Equal([Yetkiler.AlisFatura], Yetkiler.Coz(Roller.Merkez, "alis_fatura, bilinmeyen"));
        Assert.Contains(Yetkiler.Siparis, Yetkiler.CariArayabilir);
    }
}
