using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;

namespace Butcem.Api.Etiket;

/// <summary>
/// Etiket tasarımı: ölçü ve üzerindeki öğeler. Tüm ölçüler milimetre, sol üst köşe (0, 0).
/// Panelin etiket tasarımı ekranında düzenlenir, ButcemApp.EtiketSablonlari tablosunda JSON olarak durur.
/// </summary>
public sealed record EtiketTasarimi
{
    public double GenislikMm { get; init; } = 100;
    public double YukseklikMm { get; init; } = 39;
    public List<EtiketOgesi> Ogeler { get; init; } = [];
}

/// <summary>
/// Tür: yazi (sabit metin ve {alan} yer tutucuları), barkod, kutu (çerçeve ya da dolu alan / çizgi), logo (yerli üretim).
/// Yazı kutusuna sığmazsa küçülür; içindeki alanların hepsi boşsa (örn. menşei girilmemiş) hiç basılmaz.
/// </summary>
public sealed record EtiketOgesi
{
    public string Tur { get; init; } = "yazi";
    public double X { get; init; }
    public double Y { get; init; }
    public double Genislik { get; init; } = 30;
    public double Yukseklik { get; init; } = 6;
    public string Metin { get; init; } = "";
    public double Boyut { get; init; } = 4;            // yazı boyu (mm)
    public string YaziTipi { get; init; } = "sans";    // sans, serif
    public bool Kalin { get; init; }
    public string Hiza { get; init; } = "sol";          // sol, orta, sag (barkodda da)
    public string DikeyHiza { get; init; } = "ust";     // ust, orta, alt
    public int SatirSayisi { get; init; } = 1;
    public bool BuyukHarf { get; init; }
    public bool Ters { get; init; }                     // siyah zemine beyaz yazı
    public bool Dolu { get; init; }                     // kutu: içi dolu (çizgi için ince dolu kutu)
    public double Kalinlik { get; init; } = 0.4;        // kutu çerçeve kalınlığı (mm)
    public bool BarkodYazisi { get; init; } = true;     // barkodun altında numarası
}

/// <summary>Kayıtlı tasarım (ya da programın içindeki varsayılan).</summary>
public sealed record EtiketSablonu(string Kod, string Ad, bool Aktif, int Sira, bool Kayitli, bool Yerlesik, EtiketTasarimi Tasarim);

public static class EtiketSablonlari
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Yazı öğelerinde kullanılabilecek alanlar (panel bu listeyi gösterir).</summary>
    public static readonly IReadOnlyList<(string Kod, string Ad)> Alanlar =
    [
        ("urunAdi", "Ürün adı"),
        ("fiyat", "Satış fiyatı (12,50)"),
        ("fiyatLira", "Fiyatın lira kısmı (12)"),
        ("fiyatKurus", "Fiyatın kuruş kısmı (50)"),
        ("birim", "Birim (ADET, KG)"),
        ("fdTarih", "Fiyat değişim tarihi"),
        ("eskiFiyat", "Eski fiyat (değiştiyse)"),
        ("mensei", "Menşei"),
        ("barkod", "Barkod numarası"),
        ("stokKodu", "Stok kodu"),
        ("reyon", "Reyon kodu"),
        ("bugun", "Basım tarihi"),
    ];

    public static readonly string[] Turler = ["yazi", "barkod", "kutu", "logo"];

    /// <summary>Programın içindeki tasarımlar: Orion raf etiketi (RAFEAN13DIKEY) ve manav etiketi.
    /// Panelden değiştirilip kaydedilince kayıtlı hali, silinince yine bunlar kullanılır.</summary>
    public static IReadOnlyList<EtiketSablonu> Yerlesikler(EtiketAyarlari a)
    {
        var raf = new List<EtiketOgesi>
        {
            new() { Metin = "{urunAdi}", X = 2.5, Y = 2.5, Genislik = 95, Yukseklik = 10, Boyut = 4.2, YaziTipi = "serif", Kalin = true, SatirSayisi = 2 },
            new() { Metin = "F.D TARİHİ", X = 2.5, Y = 12.8, Genislik = 20, Yukseklik = 3, Boyut = 2.4, YaziTipi = "serif", Kalin = true },
            new() { Metin = "{fdTarih}", X = 2.5, Y = 16.4, Genislik = 20, Yukseklik = 3, Boyut = 2.4, YaziTipi = "serif", Kalin = true },
            new() { Metin = "SATIŞ FİYATI", X = 24, Y = 12.4, Genislik = 36, Yukseklik = 4, Boyut = 3.2, YaziTipi = "serif", Kalin = true },
            new() { Metin = "{fiyat}", X = 18, Y = 21, Genislik = 38, Yukseklik = 10.5, Boyut = 9.5, Kalin = true, Hiza = "sag" },
            new() { Metin = "₺", X = 57.5, Y = 24, Genislik = 6, Yukseklik = 7, Boyut = 6.5, Kalin = true },
            new() { Tur = "barkod", X = 66, Y = 22, Genislik = a.GenislikMm - 68.5, Yukseklik = a.YukseklikMm - 24, Hiza = "sag" },
        };
        if (a.EskiFiyatGoster)
            raf.Add(new() { Metin = "Eski fiyat: {eskiFiyat} ₺", X = 2.5, Y = 33.1, Genislik = 60, Yukseklik = 3, Boyut = 2.4 });
        if (a.YerliUretimLogosu)
            raf.Add(new() { Tur = "logo", X = a.GenislikMm - 12, Y = 9.5, Genislik = 7, Yukseklik = 11 });

        var manav = new List<EtiketOgesi>
        {
            new() { Metin = "{urunAdi}", X = 2.5, Y = 2.5, Genislik = 95, Yukseklik = 6, Boyut = 5, Kalin = true },
            new() { Metin = "{fiyat} ₺ / {birim}", X = 2.5, Y = 10, Genislik = 95, Yukseklik = 13, Boyut = 12, Kalin = true, BuyukHarf = true },
            new() { Metin = "Menşei: {mensei}", X = 2.5, Y = 26, Genislik = 58, Yukseklik = 3.5, Boyut = 3 },
            new() { Metin = "F.D: {fdTarih}", X = 2.5, Y = 32.1, Genislik = 58, Yukseklik = 3, Boyut = 2.4 },
            new() { Tur = "barkod", X = 62, Y = 25, Genislik = a.GenislikMm - 64.5, Yukseklik = a.YukseklikMm - 27, Hiza = "sag" },
        };

        EtiketTasarimi T(List<EtiketOgesi> o) => new() { GenislikMm = a.GenislikMm, YukseklikMm = a.YukseklikMm, Ogeler = o };
        return
        [
            new("raf", "Raf etiketi", true, 1, false, true, T(raf)),
            new("manav", "Manav etiketi", true, 2, false, true, T(manav)),
        ];
    }

    private sealed record Kayit(string Kod, string Ad, string Tasarim, bool Aktif, int Sira);

    /// <summary>Kayıtlı tasarımlar ve kaydı olmayan yerleşik tasarımlar, sıraya göre.</summary>
    public static List<EtiketSablonu> Hepsi(System.Data.IDbConnection app, EtiketAyarlari a)
    {
        var kayitlar = app.Query<Kayit>("SELECT Kod, Ad, Tasarim, Aktif, Sira FROM EtiketSablonlari").ToList();
        var yerlesik = Yerlesikler(a);
        var liste = kayitlar.Select(k => new EtiketSablonu(k.Kod, k.Ad, k.Aktif, k.Sira, true,
                                                           yerlesik.Any(y => y.Kod == k.Kod), Coz(k.Tasarim))).ToList();
        liste.AddRange(yerlesik.Where(y => kayitlar.All(k => k.Kod != y.Kod)));
        return liste.OrderBy(s => s.Sira).ThenBy(s => s.Ad).ToList();
    }

    public static EtiketSablonu? Bul(System.Data.IDbConnection app, EtiketAyarlari a, string? kod)
    {
        kod = string.IsNullOrWhiteSpace(kod) ? "raf" : kod.Trim().ToLowerInvariant();
        return Hepsi(app, a).FirstOrDefault(s => s.Kod == kod);
    }

    public static EtiketTasarimi Coz(string json) =>
        JsonSerializer.Deserialize<EtiketTasarimi>(json, Json) ?? new EtiketTasarimi();

    public static readonly Regex KodKalibi = new("^[a-z0-9_-]{1,20}$");

    /// <summary>Kaydetmeden ve önizlemeden önce: yazıcının basamayacağı ya da çizimi bozacak değerleri reddeder.</summary>
    public static void Dogrula(EtiketTasarimi t)
    {
        if (t.GenislikMm is < 10 or > 300 || t.YukseklikMm is < 10 or > 300)
            throw new Infrastructure.IslemHatasi("Etiket ölçüsü 10 ile 300 mm arasında olmalı.");
        if (t.Ogeler is not { Count: <= 80 }) throw new Infrastructure.IslemHatasi("Bir etikette en fazla 80 öğe olabilir.");
        for (var i = 0; i < t.Ogeler.Count; i++)
        {
            var o = t.Ogeler[i];
            var ad = $"{i + 1}. öğe";
            if (!Turler.Contains(o.Tur)) throw new Infrastructure.IslemHatasi($"{ad}: bilinmeyen tür ({o.Tur}).");
            if (o.Genislik is <= 0 or > 300 || o.Yukseklik is <= 0 or > 300 || double.IsNaN(o.X) || double.IsNaN(o.Y)
                || o.X < -5 || o.Y < -5 || o.X > t.GenislikMm || o.Y > t.YukseklikMm)
                throw new Infrastructure.IslemHatasi($"{ad}: konum ya da ölçü etiketin dışında.");
            if (o.Tur == "yazi")
            {
                if (o.Boyut is < 0.5 or > 80) throw new Infrastructure.IslemHatasi($"{ad}: yazı boyu 0,5 ile 80 mm arasında olmalı.");
                if (o.Metin.Length is 0 or > 300) throw new Infrastructure.IslemHatasi($"{ad}: yazı boş ya da çok uzun.");
                if (o.SatirSayisi is < 1 or > 10) throw new Infrastructure.IslemHatasi($"{ad}: satır sayısı 1 ile 10 arasında olmalı.");
                foreach (Match m in AlanKalibi.Matches(o.Metin))
                    if (Alanlar.All(x => x.Kod != m.Groups[1].Value))
                        throw new Infrastructure.IslemHatasi($"{ad}: bilinmeyen alan {{{m.Groups[1].Value}}}.");
            }
            if (o.Kalinlik is < 0 or > 20) throw new Infrastructure.IslemHatasi($"{ad}: çizgi kalınlığı geçersiz.");
        }
    }

    public static readonly Regex AlanKalibi = new(@"\{(\w+)\}");
}
