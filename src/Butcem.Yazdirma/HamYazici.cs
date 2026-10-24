using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Butcem.Yazdirma;

/// <summary>
/// Windows yazıcı kuyruğuna RAW veri gönderir (TSPL / EPL komutları sürücüden geçmeden yazıcıya ulaşır).
/// Xprinter ve Argox, Windows'ta USB ile "yazıcı" olarak kurulu olmalı; ad, yönetim ekranındaki "Windows yazıcı adı" ile aynı olmalı.
/// </summary>
public static class HamYazici
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class DOCINFO
    {
        public string pDocName = "Butcem etiket";
        public string? pOutputFile;
        public string pDataType = "RAW";
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);
    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int StartDocPrinter(IntPtr hPrinter, int level, [In] DOCINFO di);
    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);
    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);
    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);
    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, byte[] pBytes, int dwCount, out int dwWritten);

    public static void Gonder(string yaziciAdi, byte[] veri)
    {
        if (!OpenPrinter(yaziciAdi, out var h, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Yazıcı açılamadı: {yaziciAdi}");
        try
        {
            if (StartDocPrinter(h, 1, new DOCINFO()) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!StartPagePrinter(h)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!WritePrinter(h, veri, veri.Length, out var yazilan) || yazilan != veri.Length)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Yazıcıya veri gönderilemedi.");
                EndPagePrinter(h);
            }
            finally { EndDocPrinter(h); }
        }
        finally { ClosePrinter(h); }
    }
}
