namespace Butcem.Api.Infrastructure;

/// <summary>
/// Mikro v16 alan kodları. Anlamlar bu firmanın verisinden ve Mikro'nun veritabanı kodundan doğrulandı
/// (bkz. "Mikro v16 veritabanı analizi" dokümanı, Kod değerleri bölümü).
/// </summary>
public static class Mikro
{
    public static class StokHarTip { public const int Giris = 0, Cikis = 1, DepoTransfer = 2; }
    public static class StokHarCins { public const int Toptan = 0, Perakende = 1, DepolarArasi = 6, Sayim = 10; }
    public static class StokEvrakTip
    {
        public const int CikisFisi = 0, CikisIrsaliyesi = 1, DepoSevkFisi = 2, GirisFaturasi = 3,
                         CikisFaturasi = 4, GirisFisi = 12, GirisIrsaliyesi = 13;
    }

    public static class CariHarTip { public const int Borc = 0, Alacak = 1; }
    public static class CariEvrakTip
    {
        public const int AlisFaturasi = 0, TahsilatMakbuzu = 1, MusteriCekGirisBordrosu = 4,
                         SatisFaturasi = 63, TediyeMakbuzu = 64, CekCikisBordrosu = 67;
    }
    public static class CariCins { public const int Cari = 0, Banka = 2, Kasa = 4; }

    public static class CekTip
    {
        public const int MusteriCeki = 0, MusteriSenedi = 1, FirmaCeki = 2, FirmaSenedi = 3,
                         MusteriHavaleSozu = 4, MusteriKrediKarti = 6, FirmaHavaleEmri = 7;
    }

    /// <summary>Satış fiyat listesi (1 = "SATIŞ FİYATI", KDV dahil, tüm depolar için ortak).</summary>
    public const int SatisFiyatListesi = 1;

    public static string CekTipAdi(int tip) => tip switch
    {
        0 => "Müşteri çeki", 1 => "Müşteri senedi", 2 => "Firma çeki", 3 => "Firma senedi",
        4 => "Müşteri havale sözü", 5 => "Müşteri ödeme sözü", 6 => "Müşteri kredi kartı",
        7 => "Firma havale emri", 8 => "Firma ödeme emri", 9 => "Firma kredi kartı",
        _ => $"Tip {tip}",
    };

    /// <summary>Çek/senet son pozisyonu. 0–2 bu firmanın verisinde doğrulandı; diğerleri Mikro kodundaki yorumlardan.</summary>
    public static string CekPozisyonAdi(int tip, int poz) => (tip, poz) switch
    {
        (_, 0) => "Portföyde",
        (0 or 1, 1) => "Ciro edildi",
        (2 or 3, 1) => "Verildi",
        (_, 2) => "Tahsil edildi",
        (_, 4) => "İade edildi",
        (_, 7) => "Ödenmedi, iade",
        (_, 8) => "İcrada",
        (_, 9) => "Kısmen ödendi",
        (_, 10) => "Ödendi",
        _ => $"Pozisyon {poz}",
    };

    public static string CariEvrakAdi(int evrakTip, int cinsi, int normalIade) => (evrakTip, cinsi) switch
    {
        (0, _) => normalIade == 1 ? "Alış iade faturası" : "Alış faturası",
        (63, _) => normalIade == 1 ? "Satış iade faturası" : "Satış faturası",
        (1, 0) => "Tahsilat (nakit)",
        (1, 1) => "Tahsilat (çek)",
        (1, 17) => "Tahsilat (havale)",
        (1, 19) => "Tahsilat (kredi kartı)",
        (1, _) => "Tahsilat makbuzu",
        (64, _) => "Ödeme makbuzu",
        (4, _) => "Çek giriş bordrosu",
        (67, _) => "Çek çıkış bordrosu",
        (29, _) => "Devir",
        (_, 5) => "Dekont",
        _ => $"Evrak {evrakTip}",
    };
}
