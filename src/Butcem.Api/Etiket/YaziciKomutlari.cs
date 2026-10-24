using System.Text;
using SkiaSharp;

namespace Butcem.Api.Etiket;

/// <summary>
/// Çizilmiş etiketi yazıcının diline çevirir.
///  - TSPL: Xprinter XP-470B (ve TSC uyumlu yazıcılar)
///  - EPL:  Argox OS-214 plus (PPLB modu EPL2 uyumludur)
/// Her iki dilde de resim verisinde 0 biti siyah nokta demektir.
/// </summary>
public static class YaziciKomutlari
{
    public static byte[] Tspl(SKBitmap bmp, double genislikMm, double yukseklikMm, int adet, double bosklukMm = 2)
    {
        var (veri, satirBayt) = Paketle(bmp);
        var ms = new MemoryStream();
        void Yaz(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b); }
        Yaz(FormattableString.Invariant($"SIZE {genislikMm:0.#} mm,{yukseklikMm:0.#} mm\r\n"));
        Yaz(FormattableString.Invariant($"GAP {bosklukMm:0.#} mm,0 mm\r\n"));
        Yaz("DIRECTION 1\r\nREFERENCE 0,0\r\nCLS\r\n");
        Yaz($"BITMAP 0,0,{satirBayt},{bmp.Height},0,");
        ms.Write(veri);
        Yaz($"\r\nPRINT 1,{adet}\r\n");
        return ms.ToArray();
    }

    public static byte[] Epl(SKBitmap bmp, int boslukNokta, int adet)
    {
        var (veri, satirBayt) = Paketle(bmp);
        var ms = new MemoryStream();
        void Yaz(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b); }
        Yaz("\r\nN\r\n");
        Yaz($"q{bmp.Width}\r\n");
        Yaz($"Q{bmp.Height},{boslukNokta}\r\n");
        Yaz($"GW0,0,{satirBayt},{bmp.Height},");
        ms.Write(veri);
        Yaz($"\r\nP{adet}\r\n");
        return ms.ToArray();
    }

    /// <summary>1 bit/nokta, soldan sağa, en anlamlı bit önce; 1 = beyaz, 0 = siyah.</summary>
    private static (byte[] Veri, int SatirBayt) Paketle(SKBitmap bmp)
    {
        var satirBayt = (bmp.Width + 7) / 8;
        var veri = new byte[satirBayt * bmp.Height];
        Array.Fill(veri, (byte)0xFF);
        for (var y = 0; y < bmp.Height; y++)
        for (var x = 0; x < bmp.Width; x++)
        {
            var renk = bmp.GetPixel(x, y);
            var parlaklik = (renk.Red * 299 + renk.Green * 587 + renk.Blue * 114) / 1000;
            if (parlaklik < 128) veri[y * satirBayt + x / 8] &= (byte)~(0x80 >> (x % 8));
        }
        return (veri, satirBayt);
    }

    /// <summary>A4 sayfası: etiketleri 2 sütun × 7 satır (100 × 39 mm) dizer, 300 dpi PNG sayfaları üretir.</summary>
    public static List<byte[]> A4Sayfalari(IReadOnlyList<SKBitmap> etiketler, double etiketGenMm, double etiketYukMm)
    {
        const double dpi = 300, a4G = 210, a4Y = 297;
        int sg = (int)(a4G / 25.4 * dpi), sy = (int)(a4Y / 25.4 * dpi);
        int ew = (int)(etiketGenMm / 25.4 * dpi), eh = (int)(etiketYukMm / 25.4 * dpi);
        int sutun = Math.Max(1, (int)(a4G / etiketGenMm)), satir = Math.Max(1, (int)(a4Y / etiketYukMm));
        int solBosluk = (sg - sutun * ew) / 2, ustBosluk = (sy - satir * eh) / 2;

        var sayfalar = new List<byte[]>();
        for (var i = 0; i < etiketler.Count; i += sutun * satir)
        {
            using var sayfa = new SKBitmap(sg, sy, SKColorType.Gray8, SKAlphaType.Opaque);
            using (var c = new SKCanvas(sayfa))
            {
                c.Clear(SKColors.White);
                using var kesik = new SKPaint { Color = SKColors.Gray, StrokeWidth = 1, IsStroke = true,
                                                PathEffect = SKPathEffect.CreateDash([12, 12], 0) };
                for (var j = 0; j < sutun * satir && i + j < etiketler.Count; j++)
                {
                    var hedef = SKRect.Create(solBosluk + j % sutun * ew, ustBosluk + j / sutun * eh, ew, eh);
                    c.DrawBitmap(etiketler[i + j], hedef);
                    c.DrawRect(hedef, kesik);
                }
            }
            using var img = SKImage.FromBitmap(sayfa);
            using var png = img.Encode(SKEncodedImageFormat.Png, 90);
            sayfalar.Add(png.ToArray());
        }
        return sayfalar;
    }
}
