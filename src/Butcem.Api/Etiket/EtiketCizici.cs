using Butcem.Api.Features;
using SkiaSharp;
using ZXing;
using ZXing.Common;

namespace Butcem.Api.Etiket;

public enum EtiketSablonu { Raf, Manav }

/// <summary>Etiket ölçü ve seçenekleri. Varsayılan: Orion RAFEAN13DIKEY / RAFEAN8DIKEY dizaynı (100 × 39 mm).</summary>
public sealed record EtiketAyarlari
{
    public double GenislikMm { get; init; } = 100;
    public double YukseklikMm { get; init; } = 39;
    public int NoktaPerMm { get; init; } = 8;          // 203 dpi (XP-470B, OS-214 plus)
    public bool Dondur90 { get; init; }               // etiket yazıcıya dikey besleniyorsa
    public bool EskiFiyatGoster { get; init; }        // Orion dizaynında gizli
    public bool YerliUretimLogosu { get; init; }      // Orion dizaynında gizli
}

/// <summary>
/// Etiketi tek parça siyah-beyaz resim olarak çizer. Yazıcıya resim olarak gönderildiği için
/// Türkçe karakterler ve büyük fiyat yazısı her iki yazıcı markasında aynı çıkar.
/// </summary>
public sealed class EtiketCizici
{
    private readonly SKTypeface _serifKalin, _sansKalin, _sans;
    private readonly SKBitmap? _yerliLogo;

    public EtiketCizici(IWebHostEnvironment env)
    {
        var fonts = Path.Combine(AppContext.BaseDirectory, "Fonts");
        _serifKalin = SKTypeface.FromFile(Path.Combine(fonts, "DejaVuSerif-Bold.ttf"));
        _sansKalin = SKTypeface.FromFile(Path.Combine(fonts, "DejaVuSans-Bold.ttf"));
        _sans = SKTypeface.FromFile(Path.Combine(fonts, "DejaVuSans.ttf"));
        var logo = Path.Combine(AppContext.BaseDirectory, "Assets", "yerli_uretim.jpg");
        if (File.Exists(logo)) _yerliLogo = SKBitmap.Decode(logo);
    }

    public SKBitmap Ciz(Stok.Urun u, EtiketSablonu sablon, EtiketAyarlari a)
    {
        int w = (int)Math.Round(a.GenislikMm * a.NoktaPerMm), h = (int)Math.Round(a.YukseklikMm * a.NoktaPerMm);
        var bmp = new SKBitmap(w, h, SKColorType.Gray8, SKAlphaType.Opaque);
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(SKColors.White);
            var mm = (float)a.NoktaPerMm;
            if (sablon == EtiketSablonu.Manav) CizManav(c, u, a, w, h, mm);
            else CizRaf(c, u, a, w, h, mm);
        }
        if (!a.Dondur90) return bmp;

        var don = new SKBitmap(h, w, SKColorType.Gray8, SKAlphaType.Opaque);
        using (var c = new SKCanvas(don))
        {
            c.Clear(SKColors.White);
            c.Translate(h, 0);
            c.RotateDegrees(90);
            c.DrawBitmap(bmp, 0, 0);
        }
        bmp.Dispose();
        return don;
    }

    // Orion raf dizaynı: üstte ürün adı; solda F.D TARİHİ; ortada "SATIŞ FİYATI" ve büyük fiyat + ₺; sağ altta barkod.
    private void CizRaf(SKCanvas c, Stok.Urun u, EtiketAyarlari a, int w, int h, float mm)
    {
        var kenar = 2.5f * mm;
        SatirSigdir(c, u.UrunAdi, _serifKalin, 4.2f * mm, kenar, 2.5f * mm, w - 2 * kenar, 2);

        Yaz(c, "F.D TARİHİ", _serifKalin, 2.4f * mm, kenar, 15.2f * mm);
        if (u.FiyatDegisimTarihi is { } t) Yaz(c, t.ToString("dd.MM.yyyy"), _serifKalin, 2.4f * mm, kenar, 18.8f * mm);
        Yaz(c, "SATIŞ FİYATI", _serifKalin, 3.2f * mm, 24 * mm, 15.6f * mm);

        var fiyat = u.Fiyat.ToString("N2", Tr);
        var fiyatBoy = 9.5f * mm;
        var sagKenar = 56f * mm;
        using (var p = Kalem(_sansKalin, fiyatBoy))
        {
            var gen = p.MeasureText(fiyat);
            if (gen > sagKenar - 18 * mm) { p.TextSize *= (sagKenar - 18 * mm) / gen; gen = p.MeasureText(fiyat); }
            c.DrawText(fiyat, sagKenar - gen, 30.5f * mm, p);
        }
        Yaz(c, "₺", _sansKalin, 6.5f * mm, sagKenar + 1.5f * mm, 30.5f * mm);

        if (a.EskiFiyatGoster && u.EskiFiyat is { } eski && eski > 0 && eski != u.Fiyat)
            Yaz(c, $"Eski fiyat: {eski.ToString("N2", Tr)} ₺", _sans, 2.4f * mm, kenar, 35.5f * mm);

        if (a.YerliUretimLogosu && u.YerliUretim && _yerliLogo is not null)
            c.DrawBitmap(_yerliLogo, SKRect.Create(w - 12 * mm, 9.5f * mm, 7 * mm, 11 * mm));

        Barkod(c, u.Barkod, new SKRect(66 * mm, 22 * mm, w - kenar, h - 2 * mm), mm);
    }

    // Manav dizaynı gelene kadar geçici yerleşim: ad, büyük kg fiyatı, menşei, barkod.
    private void CizManav(SKCanvas c, Stok.Urun u, EtiketAyarlari a, int w, int h, float mm)
    {
        var kenar = 2.5f * mm;
        SatirSigdir(c, u.UrunAdi, _sansKalin, 5f * mm, kenar, 2.5f * mm, w - 2 * kenar, 1);
        var birim = string.IsNullOrWhiteSpace(u.Birim) ? "" : $" / {u.Birim.ToUpper(Tr)}";
        using (var p = Kalem(_sansKalin, 12 * mm))
            c.DrawText($"{u.Fiyat.ToString("N2", Tr)} ₺{birim}", kenar, 22 * mm, p);
        if (!string.IsNullOrWhiteSpace(u.Mensei)) Yaz(c, $"Menşei: {u.Mensei}", _sans, 3f * mm, kenar, 29 * mm);
        if (u.FiyatDegisimTarihi is { } t) Yaz(c, $"F.D: {t:dd.MM.yyyy}", _sans, 2.4f * mm, kenar, 34.5f * mm);
        Barkod(c, u.Barkod, new SKRect(62 * mm, 25 * mm, w - kenar, h - 2 * mm), mm);
    }

    private static readonly System.Globalization.CultureInfo Tr = new("tr-TR");

    private static SKPaint Kalem(SKTypeface tf, float boy) => new()
    {
        Typeface = tf, TextSize = boy, Color = SKColors.Black, IsAntialias = false,
    };

    private static void Yaz(SKCanvas c, string metin, SKTypeface tf, float boy, float x, float tabanY)
    {
        using var p = Kalem(tf, boy);
        c.DrawText(metin, x, tabanY, p);
    }

    /// <summary>Metni en fazla <paramref name="satir"/> satıra kelime kelime böler; sığmazsa yazıyı küçültür.</summary>
    private static void SatirSigdir(SKCanvas c, string metin, SKTypeface tf, float boy, float x, float ustY, float genislik, int satir)
    {
        using var p = Kalem(tf, boy);
        for (var deneme = 0; deneme < 6; deneme++)
        {
            var satirlar = Bol(metin, p, genislik);
            if (satirlar.Count <= satir)
            {
                var y = ustY + p.TextSize;
                foreach (var s in satirlar) { c.DrawText(s, x, y, p); y += p.TextSize * 1.15f; }
                return;
            }
            p.TextSize *= 0.88f;
        }
        c.DrawText(metin, x, ustY + p.TextSize, p);
    }

    private static List<string> Bol(string metin, SKPaint p, float genislik)
    {
        var satirlar = new List<string> { "" };
        foreach (var kelime in metin.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var aday = satirlar[^1].Length == 0 ? kelime : $"{satirlar[^1]} {kelime}";
            if (p.MeasureText(aday) <= genislik || satirlar[^1].Length == 0) satirlar[^1] = aday;
            else satirlar.Add(kelime);
        }
        return satirlar;
    }

    /// <summary>EAN13/EAN8 geçerliyse onu, değilse Code128 basar (Orion'daki iki dizaynın karşılığı).
    /// Çubuklar tam nokta genişliğinde çizilir, okunabilirlik için kenar yumuşatma kapalı.</summary>
    private void Barkod(SKCanvas c, string kod, SKRect alan, float mm)
    {
        var format = kod.All(char.IsDigit) && kod.Length == 13 && EanGecerli(kod) ? BarcodeFormat.EAN_13
                   : kod.All(char.IsDigit) && kod.Length == 8 && EanGecerli(kod) ? BarcodeFormat.EAN_8
                   : BarcodeFormat.CODE_128;
        BitMatrix m;
        try { m = new MultiFormatWriter().encode(kod, format, 0, 0, new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 0 }); }
        catch { m = new MultiFormatWriter().encode(kod, BarcodeFormat.CODE_128, 0, 0); }

        var yaziBoy = 2.6f * mm;
        var modul = Math.Max(1, (int)Math.Floor(alan.Width / m.Width));
        var genislik = modul * m.Width;
        var x0 = (int)(alan.Right - genislik);
        var yukseklik = (int)(alan.Height - yaziBoy - 1 * mm);
        using var p = new SKPaint { Color = SKColors.Black, IsAntialias = false };
        for (var x = 0; x < m.Width; x++)
            if (m[x, m.Height / 2]) c.DrawRect(x0 + x * modul, alan.Top, modul, yukseklik, p);
        using var yp = Kalem(_sans, yaziBoy);
        var yg = yp.MeasureText(kod);
        c.DrawText(kod, x0 + (genislik - yg) / 2, alan.Bottom, yp);
    }

    private static bool EanGecerli(string kod)
    {
        var toplam = 0;
        for (var i = 0; i < kod.Length - 1; i++)
        {
            var d = kod[i] - '0';
            toplam += ((kod.Length - 1 - i) % 2 == 1) ? d * 3 : d;
        }
        return (10 - toplam % 10) % 10 == kod[^1] - '0';
    }
}
