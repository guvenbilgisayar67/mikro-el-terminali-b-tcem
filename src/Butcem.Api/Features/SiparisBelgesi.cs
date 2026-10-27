using System.Globalization;
using System.Text;
using SkiaSharp;

namespace Butcem.Api.Features;

/// <summary>Tedarikçiye / depoya gönderilen sipariş: A4 PDF form ve WhatsApp / e-posta metni. Fiyat yazılmaz.</summary>
public static class SiparisBelgesi
{
    private static readonly CultureInfo Tr = new("tr-TR");
    private static readonly Lazy<(SKTypeface Normal, SKTypeface Kalin)> Yazitipi = new(() =>
    {
        var klasor = Path.Combine(AppContext.BaseDirectory, "Fonts");
        return (SKTypeface.FromFile(Path.Combine(klasor, "DejaVuSans.ttf")), SKTypeface.FromFile(Path.Combine(klasor, "DejaVuSans-Bold.ttf")));
    });

    private static string Miktar(decimal m) => m.ToString("0.###", Tr);

    private static string Baslik(Siparisler.Evrak e) => e.Tur == Siparisler.Depo ? $"{e.KaynakKod} · {e.KaynakAd}" : e.KaynakAd;

    public static string Metin(Siparisler.Evrak e, IConfiguration config)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"*{config["Firma:Ad"] ?? "Bütçem"} · SİPARİŞ {e.Seri} {e.Sira}*");
        sb.AppendLine($"Kime: {Baslik(e)}");
        sb.AppendLine($"Teslim yeri: {e.DepoAdi}");
        sb.AppendLine($"Tarih: {e.Tarih:dd.MM.yyyy} · Teslim: {e.TeslimTarihi:dd.MM.yyyy}");
        sb.AppendLine();
        var i = 1;
        foreach (var s in e.Satirlar)
            sb.AppendLine($"{i++}) {s.UrunAdi.Trim()} ({s.Barkod}): {Miktar(s.Miktar)} {s.Birim}");
        sb.AppendLine();
        sb.Append($"Toplam {e.Satirlar.Count} kalem.");
        return sb.ToString();
    }

    public static byte[] Pdf(Siparisler.Evrak e, IConfiguration config)
    {
        const float G = 595, Y = 842, Kenar = 40;   // A4, punto
        var (normal, kalin) = Yazitipi.Value;
        using var akis = new MemoryStream();
        using (var doc = SKDocument.CreatePdf(akis, new SKDocumentPdfMetadata { Title = $"Sipariş {e.Seri} {e.Sira}", Creator = "Bütçem" }))
        {
            using var yazi = new SKPaint { Typeface = normal, TextSize = 10, IsAntialias = true, Color = SKColors.Black };
            using var kalinYazi = new SKPaint { Typeface = kalin, TextSize = 10, IsAntialias = true, Color = SKColors.Black };
            using var buyuk = new SKPaint { Typeface = kalin, TextSize = 18, IsAntialias = true, Color = SKColors.Black };
            using var cizgi = new SKPaint { Color = new SKColor(0xBB, 0xBB, 0xBB), StrokeWidth = 0.5f };
            // Sütunlar: sıra, barkod, ürün, miktar, birim
            float[] x = [Kenar, Kenar + 28, Kenar + 140, G - Kenar - 90, G - Kenar - 40];
            var sayfaNo = 0;
            var satir = 0;
            SKCanvas? c = null;
            float y = 0;

            void YeniSayfa()
            {
                if (c is not null) doc.EndPage();
                c = doc.BeginPage(G, Y);
                sayfaNo++;
                y = Kenar;
                if (sayfaNo == 1)
                {
                    c.DrawText(config["Firma:Ad"] ?? "Bütçem", Kenar, y + 14, buyuk);
                    c.DrawText($"SİPARİŞ {e.Seri} {e.Sira}", G - Kenar - buyuk.MeasureText($"SİPARİŞ {e.Seri} {e.Sira}"), y + 14, buyuk);
                    y += 40;
                    (string, string)[] bilgi =
                    [
                        ("Kime", Baslik(e)),
                        ("Teslim yeri", e.DepoAdi),
                        ("Sipariş tarihi", e.Tarih.ToString("dd.MM.yyyy")),
                        ("Teslim tarihi", e.TeslimTarihi.ToString("dd.MM.yyyy")),
                    ];
                    foreach (var (k, v) in bilgi)
                    {
                        c.DrawText(k, Kenar, y, kalinYazi);
                        c.DrawText(v, Kenar + 90, y, yazi);
                        y += 15;
                    }
                    y += 10;
                }
                c.DrawText("#", x[0], y, kalinYazi);
                c.DrawText("Barkod", x[1], y, kalinYazi);
                c.DrawText("Ürün", x[2], y, kalinYazi);
                c.DrawText("Miktar", x[4] - 6 - kalinYazi.MeasureText("Miktar"), y, kalinYazi);
                c.DrawText("Birim", x[4], y, kalinYazi);
                y += 5;
                c.DrawLine(Kenar, y, G - Kenar, y, cizgi);
                y += 13;
            }

            YeniSayfa();
            foreach (var s in e.Satirlar)
            {
                if (y > Y - Kenar - 30) YeniSayfa();
                satir++;
                c!.DrawText(satir.ToString(), x[0], y, yazi);
                c.DrawText(s.Barkod, x[1], y, yazi);
                c.DrawText(Sigdir(s.UrunAdi.Trim(), yazi, x[3] - x[2] - 8), x[2], y, yazi);
                var m = Miktar(s.Miktar);
                c.DrawText(m, x[4] - 6 - kalinYazi.MeasureText(m), y, kalinYazi);
                c.DrawText(s.Birim, x[4], y, yazi);
                y += 4;
                c.DrawLine(Kenar, y, G - Kenar, y, cizgi);
                y += 11;
            }
            y += 8;
            c!.DrawText($"Toplam {e.Satirlar.Count} kalem.", Kenar, y, kalinYazi);
            doc.EndPage();
            doc.Close();
        }
        return akis.ToArray();
    }

    private static string Sigdir(string metin, SKPaint p, float genislik)
    {
        if (p.MeasureText(metin) <= genislik) return metin;
        while (metin.Length > 1 && p.MeasureText(metin + "…") > genislik) metin = metin[..^1];
        return metin + "…";
    }
}
