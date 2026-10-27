using Butcem.Api.Features;
using SkiaSharp;
using ZXing;
using ZXing.Common;

namespace Butcem.Api.Etiket;

/// <summary>Yazıcı ayarları ve programın içindeki tasarımların ölçüsü (100 × 39 mm, Orion RAFEAN13DIKEY / RAFEAN8DIKEY).
/// Eski fiyat ve yerli üretim seçenekleri yalnız yerleşik raf tasarımına öğe ekler; panelden kaydedilen tasarım kendi öğelerini kullanır.</summary>
public sealed record EtiketAyarlari
{
    public double GenislikMm { get; init; } = 100;
    public double YukseklikMm { get; init; } = 39;
    public int NoktaPerMm { get; init; } = 8;          // 203 dpi (XP-470B, OS-214 plus)
    public bool Dondur90 { get; init; }               // etiket yazıcıya dikey besleniyorsa
    public bool EskiFiyatGoster { get; init; }        // Orion dizaynında gizli
    public bool YerliUretimLogosu { get; init; }      // Orion dizaynında gizli

    public static EtiketAyarlari Oku(IConfiguration cfg) => cfg.GetSection("Etiket").Get<EtiketAyarlari>() ?? new EtiketAyarlari();
}

/// <summary>
/// Etiketi tek parça siyah-beyaz resim olarak çizer. Yazıcıya resim olarak gönderildiği için
/// Türkçe karakterler ve büyük fiyat yazısı her iki yazıcı markasında aynı çıkar.
/// </summary>
public sealed class EtiketCizici
{
    private readonly SKTypeface _serifKalin, _serif, _sansKalin, _sans;
    private readonly SKBitmap? _yerliLogo;

    public EtiketCizici(IWebHostEnvironment env)
    {
        var fonts = Path.Combine(AppContext.BaseDirectory, "Fonts");
        _serifKalin = SKTypeface.FromFile(Path.Combine(fonts, "DejaVuSerif-Bold.ttf"));
        _serif = SKTypeface.FromFile(Path.Combine(fonts, "DejaVuSerif.ttf"));
        _sansKalin = SKTypeface.FromFile(Path.Combine(fonts, "DejaVuSans-Bold.ttf"));
        _sans = SKTypeface.FromFile(Path.Combine(fonts, "DejaVuSans.ttf"));
        var logo = Path.Combine(AppContext.BaseDirectory, "Assets", "yerli_uretim.jpg");
        if (File.Exists(logo)) _yerliLogo = SKBitmap.Decode(logo);
    }

    /// <summary>Tasarımı ürünle doldurup çizer. <paramref name="dondur90"/>: etiket yazıcıya dikey besleniyorsa.</summary>
    public SKBitmap Ciz(Stok.Urun u, EtiketTasarimi t, int noktaPerMm, bool dondur90)
    {
        var mm = (float)noktaPerMm;
        int w = (int)Math.Round(t.GenislikMm * mm), h = (int)Math.Round(t.YukseklikMm * mm);
        var bmp = new SKBitmap(w, h, SKColorType.Gray8, SKAlphaType.Opaque);
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(SKColors.White);
            var degerler = Degerler(u);
            foreach (var o in t.Ogeler)
            {
                var alan = SKRect.Create((float)o.X * mm, (float)o.Y * mm, (float)o.Genislik * mm, (float)o.Yukseklik * mm);
                switch (o.Tur)
                {
                    case "yazi": YaziCiz(c, o, alan, degerler, mm); break;
                    case "barkod": Barkod(c, u.Barkod, alan, mm, o.Hiza, o.BarkodYazisi); break;
                    case "kutu": KutuCiz(c, o, alan, mm); break;
                    case "logo" when u.YerliUretim && _yerliLogo is not null: c.DrawBitmap(_yerliLogo, alan); break;
                }
            }
        }
        if (!dondur90) return bmp;

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

    /// <summary>Yazı öğelerindeki {alan} karşılıkları. Boş olanların öğesi basılmaz.</summary>
    public static Dictionary<string, string> Degerler(Stok.Urun u)
    {
        var fiyat = u.Fiyat.ToString("N2", Tr);
        var virgul = fiyat.LastIndexOf(',');
        return new()
        {
            ["urunAdi"] = u.UrunAdi.Trim(),
            ["fiyat"] = fiyat,
            ["fiyatLira"] = virgul < 0 ? fiyat : fiyat[..virgul],
            ["fiyatKurus"] = virgul < 0 ? "00" : fiyat[(virgul + 1)..],
            ["birim"] = u.Birim?.Trim() ?? "",
            ["fdTarih"] = u.FiyatDegisimTarihi?.ToString("dd.MM.yyyy") ?? "",
            ["eskiFiyat"] = u.EskiFiyat is { } e && e > 0 && e != u.Fiyat ? e.ToString("N2", Tr) : "",
            ["mensei"] = u.Mensei?.Trim() ?? "",
            ["barkod"] = u.Barkod,
            ["stokKodu"] = u.StokKodu,
            ["reyon"] = u.ReyonKod?.Trim() ?? "",
            ["bugun"] = DateTime.Today.ToString("dd.MM.yyyy"),
        };
    }

    /// <summary>Yer tutucuları doldurur; metinde alan varsa ve hepsi boşsa null döner (öğe basılmaz).</summary>
    public static string? MetniDoldur(string metin, IReadOnlyDictionary<string, string> degerler)
    {
        var alanVar = false;
        var doluVar = false;
        var sonuc = EtiketSablonlari.AlanKalibi.Replace(metin, m =>
        {
            alanVar = true;
            var d = degerler.GetValueOrDefault(m.Groups[1].Value, "");
            if (d.Length > 0) doluVar = true;
            return d;
        });
        return alanVar && !doluVar ? null : sonuc;
    }

    private void YaziCiz(SKCanvas c, EtiketOgesi o, SKRect alan, IReadOnlyDictionary<string, string> degerler, float mm)
    {
        var metin = MetniDoldur(o.Metin, degerler);
        if (string.IsNullOrWhiteSpace(metin)) return;
        if (o.BuyukHarf) metin = metin.ToUpper(Tr);

        var tf = o.YaziTipi == "serif" ? (o.Kalin ? _serifKalin : _serif) : (o.Kalin ? _sansKalin : _sans);
        using var p = Kalem(tf, (float)o.Boyut * mm);
        var satirlar = new List<string> { metin };
        for (var deneme = 0; deneme < 14; deneme++)
        {
            satirlar = o.SatirSayisi == 1 ? [metin] : Bol(metin, p, alan.Width);
            var enGenis = satirlar.Max(s => p.MeasureText(s));
            var yukseklik = p.TextSize * (1 + (satirlar.Count - 1) * 1.15f);
            if (satirlar.Count <= o.SatirSayisi && enGenis <= alan.Width + 0.5f && yukseklik <= alan.Height * 1.05f) break;
            // Tek satırda tam oranında, çok satırda adım adım küçült.
            var oran = o.SatirSayisi == 1 ? Math.Min(alan.Width / enGenis, alan.Height * 1.05f / p.TextSize) : 0.92f;
            p.TextSize *= Math.Min(0.98f, oran);
        }

        if (o.Ters)
        {
            using var zemin = new SKPaint { Color = SKColors.Black, IsAntialias = false };
            c.DrawRect(alan, zemin);
            p.Color = SKColors.White;
        }
        var blok = p.TextSize * (1 + (satirlar.Count - 1) * 1.15f);
        var y = o.DikeyHiza switch
        {
            "orta" => alan.Top + (alan.Height - blok) / 2 + p.TextSize * 0.9f,
            "alt" => alan.Bottom - p.TextSize * 0.22f - (satirlar.Count - 1) * p.TextSize * 1.15f,
            _ => alan.Top + p.TextSize,
        };
        foreach (var s in satirlar)
        {
            var g = p.MeasureText(s);
            var x = o.Hiza switch { "orta" => alan.Left + (alan.Width - g) / 2, "sag" => alan.Right - g, _ => alan.Left };
            c.DrawText(s, x, y, p);
            y += p.TextSize * 1.15f;
        }
    }

    private static void KutuCiz(SKCanvas c, EtiketOgesi o, SKRect alan, float mm)
    {
        using var p = new SKPaint { Color = SKColors.Black, IsAntialias = false };
        if (o.Dolu) { c.DrawRect(alan, p); return; }
        var k = Math.Max(1f, (float)o.Kalinlik * mm);
        p.IsStroke = true;
        p.StrokeWidth = k;
        alan.Inflate(-k / 2, -k / 2);
        c.DrawRect(alan, p);
    }

    private static readonly System.Globalization.CultureInfo Tr = new("tr-TR");

    private static SKPaint Kalem(SKTypeface tf, float boy) => new()
    {
        Typeface = tf, TextSize = boy, Color = SKColors.Black, IsAntialias = false,
    };

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
    private void Barkod(SKCanvas c, string kod, SKRect alan, float mm, string hiza, bool yazili)
    {
        var format = kod.All(char.IsDigit) && kod.Length == 13 && EanGecerli(kod) ? BarcodeFormat.EAN_13
                   : kod.All(char.IsDigit) && kod.Length == 8 && EanGecerli(kod) ? BarcodeFormat.EAN_8
                   : BarcodeFormat.CODE_128;
        BitMatrix m;
        try { m = new MultiFormatWriter().encode(kod, format, 0, 0, new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 0 }); }
        catch { m = new MultiFormatWriter().encode(kod, BarcodeFormat.CODE_128, 0, 0); }

        var yaziBoy = yazili ? 2.6f * mm : 0;
        var modul = Math.Max(1, (int)Math.Floor(alan.Width / m.Width));
        var genislik = modul * m.Width;
        var x0 = (int)(hiza switch { "sol" => alan.Left, "orta" => alan.Left + (alan.Width - genislik) / 2, _ => alan.Right - genislik });
        var yukseklik = (int)(alan.Height - (yazili ? yaziBoy + 1 * mm : 0));
        using var p = new SKPaint { Color = SKColors.Black, IsAntialias = false };
        for (var x = 0; x < m.Width; x++)
            if (m[x, m.Height / 2]) c.DrawRect(x0 + x * modul, alan.Top, modul, yukseklik, p);
        if (!yazili) return;
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
