using System.Drawing;
using System.Drawing.Printing;
using System.IO.Compression;

namespace Butcem.Yazdirma;

/// <summary>
/// A4 etiket sayfaları sunucudan zip içinde 300 dpi PNG olarak gelir; her biri bir A4 sayfasına kenar boşluksuz basılır.
/// Epson L3250 / Brother normal Windows sürücüsüyle kullanılır.
/// </summary>
public static class A4Yazici
{
    public static void Bas(string yaziciAdi, byte[] zip)
    {
        var sayfalar = new List<Image>();
        try
        {
            using (var arsiv = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
                foreach (var g in arsiv.Entries.OrderBy(e => e.Name))
                {
                    using var s = g.Open();
                    var ms = new MemoryStream();
                    s.CopyTo(ms);
                    sayfalar.Add(Image.FromStream(ms));
                }

            using var belge = new PrintDocument();
            belge.PrinterSettings.PrinterName = yaziciAdi;
            if (!belge.PrinterSettings.IsValid) throw new InvalidOperationException($"Yazıcı bulunamadı: {yaziciAdi}");
            belge.DocumentName = "Butcem A4 etiket";
            belge.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
            belge.OriginAtMargins = false;
            var a4 = belge.PrinterSettings.PaperSizes.Cast<PaperSize>().FirstOrDefault(p => p.Kind == PaperKind.A4);
            if (a4 is not null) belge.DefaultPageSettings.PaperSize = a4;

            var i = 0;
            belge.PrintPage += (_, e) =>
            {
                // Sayfa birimi 1/100 inç: A4 = 827 x 1169. Resim (300 dpi) tam sayfaya oturtulur.
                var hedef = new RectangleF(-e.PageSettings.HardMarginX, -e.PageSettings.HardMarginY, 827f, 1169f);
                e.Graphics!.DrawImage(sayfalar[i], hedef);
                i++;
                e.HasMorePages = i < sayfalar.Count;
            };
            belge.Print();
        }
        finally { sayfalar.ForEach(s => s.Dispose()); }
    }
}
