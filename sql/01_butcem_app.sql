/*
  Uygulamanın kendi veritabanı. Mikro ve Orion veritabanlarına HİÇBİR tablo eklenmez;
  kullanıcılar, yazıcılar, etiket kuyruğu ve işlem logu burada tutulur.

  Kurulum: SQL Server Management Studio'da Mikro'nun bulunduğu sunucuda bir kez çalıştırın.
*/
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO
IF DB_ID(N'ButcemApp') IS NULL
    CREATE DATABASE ButcemApp;
GO
USE ButcemApp;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.Kullanicilar') IS NULL
CREATE TABLE dbo.Kullanicilar (
    Id            int IDENTITY(1,1) PRIMARY KEY,
    KullaniciAdi  nvarchar(50)  NOT NULL UNIQUE,
    SifreHash     nvarchar(100) NOT NULL,          -- BCrypt
    AdSoyad       nvarchar(100) NOT NULL,
    Rol           nvarchar(20)  NOT NULL CHECK (Rol IN (N'admin', N'merkez', N'magaza')),
    DepoNo        int NULL,                         -- mağaza kullanıcısının deposu (Mikro dep_no)
    Aktif         bit NOT NULL DEFAULT 1,
    OlusturmaZamani datetime2 NOT NULL DEFAULT SYSDATETIME(),
    SonGiris      datetime2 NULL
);
GO
/* v0.3: ekran yetkileri, virgülle ayrılmış (ciro,satis_raporu,cari_ekstre,cek_raporu,cek_giris,cek_cikis,
   alis_fatura,satis_fatura,sevk,fiyat_degisikligi,etiket,siparis,sayim,sayim_onay). NULL: eski varsayılan (merkez: hepsi, mağaza: sevk + etiket). Admin her şeye yetkili. */
IF COL_LENGTH(N'dbo.Kullanicilar', N'Yetkiler') IS NULL
    ALTER TABLE dbo.Kullanicilar ADD Yetkiler nvarchar(400) NULL;
GO

/* Mağaza bilgisayarına kurulan yazdırma ajanları. Her ajan kendi anahtarıyla bağlanır. */
IF OBJECT_ID(N'dbo.YazdirmaAjanlari') IS NULL
CREATE TABLE dbo.YazdirmaAjanlari (
    Id          int IDENTITY(1,1) PRIMARY KEY,
    Ad          nvarchar(50) NOT NULL UNIQUE,      -- örn. TEPE-PC
    DepoNo      int NOT NULL,
    AnahtarHash nvarchar(100) NOT NULL,            -- BCrypt
    Aktif       bit NOT NULL DEFAULT 1,
    SonGorulme  datetime2 NULL
);
GO

IF OBJECT_ID(N'dbo.Yazicilar') IS NULL
CREATE TABLE dbo.Yazicilar (
    Id               int IDENTITY(1,1) PRIMARY KEY,
    AjanId           int NOT NULL REFERENCES dbo.YazdirmaAjanlari(Id),
    Ad               nvarchar(50)  NOT NULL,       -- ekranda görünen ad, örn. "Raf yazıcısı"
    WindowsYaziciAdi nvarchar(200) NOT NULL,       -- Windows'taki yazıcı adı
    Tur              nvarchar(10)  NOT NULL CHECK (Tur IN (N'etiket', N'a4')),
    Dil              nvarchar(10)  NOT NULL CHECK (Dil IN (N'TSPL', N'EPL', N'PDF')),
    Aktif            bit NOT NULL DEFAULT 1
);
GO

/* Yazdırma kuyruğu. API iş üretir, ajan alır ve basar. */
IF OBJECT_ID(N'dbo.EtiketIsleri') IS NULL
CREATE TABLE dbo.EtiketIsleri (
    Id              bigint IDENTITY(1,1) PRIMARY KEY,
    YaziciId        int NOT NULL REFERENCES dbo.Yazicilar(Id),
    Sablon          nvarchar(20) NOT NULL,          -- raf, manav, a4
    Adet            int NOT NULL,
    Icerik          varbinary(max) NOT NULL,         -- yazıcıya gidecek ham veri (TSPL/EPL) ya da PDF
    Durum           tinyint NOT NULL DEFAULT 0,      -- 0 bekliyor, 1 ajana verildi, 2 basıldı, 3 hata
    Hata            nvarchar(500) NULL,
    OlusturanId     int NOT NULL REFERENCES dbo.Kullanicilar(Id),
    OlusturmaZamani datetime2 NOT NULL DEFAULT SYSDATETIME(),
    AlinmaZamani    datetime2 NULL,
    BitisZamani     datetime2 NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_EtiketIsleri_Bekleyen')
    CREATE INDEX IX_EtiketIsleri_Bekleyen ON dbo.EtiketIsleri (YaziciId, Durum) WHERE Durum IN (0, 1);
GO

IF OBJECT_ID(N'dbo.IslemLog') IS NULL
CREATE TABLE dbo.IslemLog (
    Id          bigint IDENTITY(1,1) PRIMARY KEY,
    KullaniciId int NULL,
    Islem       nvarchar(50)  NOT NULL,             -- giris, etiket, sevk, cek_giris ...
    Detay       nvarchar(max) NULL,                 -- JSON
    Zaman       datetime2 NOT NULL DEFAULT SYSDATETIME()
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_IslemLog_Zaman')
    CREATE INDEX IX_IslemLog_Zaman ON dbo.IslemLog (Zaman);
GO

/* v0.5: sipariş gönderimleri ve tedarikçi / depo iletişim bilgileri (Mikro cari kartına yazılmaz). */
IF OBJECT_ID(N'dbo.SiparisGonderimleri') IS NULL
CREATE TABLE dbo.SiparisGonderimleri (
    Id          bigint IDENTITY(1,1) PRIMARY KEY,
    Tur         nvarchar(10)  NOT NULL,             -- tedarikci, depo
    Seri        nvarchar(20)  NOT NULL,
    Sira        int           NOT NULL,
    Kanal       nvarchar(10)  NOT NULL,             -- eposta, whatsapp
    Alici       nvarchar(200) NOT NULL,
    KullaniciId int NULL,
    Zaman       datetime2 NOT NULL DEFAULT SYSDATETIME()
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SiparisGonderimleri_Zaman')
    CREATE INDEX IX_SiparisGonderimleri_Zaman ON dbo.SiparisGonderimleri (Zaman);
GO
IF OBJECT_ID(N'dbo.TedarikciIletisim') IS NULL
CREATE TABLE dbo.TedarikciIletisim (
    Anahtar          nvarchar(30) PRIMARY KEY,      -- cari kodu ya da DEPO:<no>
    Eposta           nvarchar(200) NULL,
    Telefon          nvarchar(20)  NULL,
    GuncellemeZamani datetime2 NOT NULL DEFAULT SYSDATETIME()
);
GO

/* v0.6: stok sayımı. Terminalden / bilgisayardan okutulan sayımlar onaylanana kadar burada birikir. */
IF OBJECT_ID(N'dbo.Sayimlar') IS NULL
CREATE TABLE dbo.Sayimlar (
    Id              int IDENTITY(1,1) PRIMARY KEY,
    DepoNo          int NOT NULL,
    Tarih           date NOT NULL,                  -- Mikro'daki sayım tarihi; stok bu günün sonu itibarıyla eşitlenir
    Aciklama        nvarchar(200) NULL,
    Durum           tinyint NOT NULL DEFAULT 0,     -- 0 sayılıyor, 1 onay bekliyor, 2 onaylandı, 3 iptal, 4 işleniyor
    OlusturanId     int NULL,
    OlusturmaZamani datetime2 NOT NULL DEFAULT SYSDATETIME(),
    GonderenId      int NULL,
    GonderimZamani  datetime2 NULL,
    OnaylayanId     int NULL,
    OnayZamani      datetime2 NULL,
    MikroEvrakNo    int NULL,                       -- SAYIM_SONUCLARI.sym_evrakno
    GirisFisi       nvarchar(30) NULL,              -- örn. SYMG 7
    CikisFisi       nvarchar(30) NULL
);
GO
IF OBJECT_ID(N'dbo.SayimSatirlari') IS NULL
CREATE TABLE dbo.SayimSatirlari (
    SayimId         int NOT NULL REFERENCES dbo.Sayimlar(Id),
    StokKodu        nvarchar(50) NOT NULL,
    Barkod          nvarchar(100) NOT NULL,
    UrunAdi         nvarchar(100) NOT NULL,
    Birim           nvarchar(20) NOT NULL,
    TerminalMiktar  decimal(18,3) NOT NULL,         -- okutulanların toplamı
    Miktar          decimal(18,3) NOT NULL,         -- geçerli sayılan (merkez düzeltmişse düzeltilmiş)
    SonOkutanId     int NULL,
    SonOkutmaZamani datetime2 NOT NULL DEFAULT SYSDATETIME(),
    DuzeltenId      int NULL,
    DuzeltmeZamani  datetime2 NULL,
    SistemStok      decimal(18,3) NULL,             -- onay anında Mikro stoğu
    Fark            decimal(18,3) NULL,
    BirimMaliyet    decimal(18,6) NULL,
    PRIMARY KEY (SayimId, StokKodu)
);
GO
IF OBJECT_ID(N'dbo.SayimGirisleri') IS NULL
CREATE TABLE dbo.SayimGirisleri (
    Id          bigint IDENTITY(1,1) PRIMARY KEY,
    SayimId     int NOT NULL,
    StokKodu    nvarchar(50) NOT NULL,
    Miktar      decimal(18,3) NOT NULL,
    KullaniciId int NULL,
    Islem       nvarchar(10) NOT NULL,             -- okut, duzelt, cikar
    IstemciId   uniqueidentifier NULL,             -- terminalin okutma kimliği (çevrimdışı tekrar gönderimde çift sayılmasın)
    Zaman       datetime2 NOT NULL DEFAULT SYSDATETIME()
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_SayimGirisleri_Istemci')
    CREATE UNIQUE INDEX UX_SayimGirisleri_Istemci ON dbo.SayimGirisleri (IstemciId) WHERE IstemciId IS NOT NULL;
GO

/* v0.7: terminal çevrimdışıyken biriktirip sonra gönderdiği işlemler (sevk, etiket). Aynı işlem iki kez gelirse
   ikinci kez yapılmaz, ilk sonucun aynısı döner. Sonuc NULL: işleniyor. */
IF OBJECT_ID(N'dbo.IstemciIslemleri') IS NULL
CREATE TABLE dbo.IstemciIslemleri (
    IstemciId   uniqueidentifier NOT NULL PRIMARY KEY,
    Islem       nvarchar(20) NOT NULL,             -- sevk, etiket
    KullaniciId int NULL,
    Sonuc       nvarchar(max) NULL,                -- JSON
    Zaman       datetime2 NOT NULL DEFAULT SYSDATETIME(),
    BitisZamani datetime2 NULL
);
GO

/* v0.7: panelden düzenlenen etiket tasarımları. Kayıt yoksa programın içindeki raf ve manav tasarımı kullanılır. */
IF OBJECT_ID(N'dbo.EtiketSablonlari') IS NULL
CREATE TABLE dbo.EtiketSablonlari (
    Kod              nvarchar(20) NOT NULL PRIMARY KEY,   -- EtiketIsleri.Sablon
    Ad               nvarchar(60) NOT NULL,
    Tasarim          nvarchar(max) NOT NULL,              -- JSON: ölçü ve öğeler (mm)
    Aktif            bit NOT NULL DEFAULT 1,              -- pasif tasarım terminalde ve basım ekranında görünmez
    Sira             int NOT NULL DEFAULT 0,
    GuncelleyenId    int NULL,
    GuncellemeZamani datetime2 NOT NULL DEFAULT SYSDATETIME()
);
GO

/* İlk yönetici. Şifre: Butcem2026!  — ilk girişten sonra değiştirin. */
IF NOT EXISTS (SELECT 1 FROM dbo.Kullanicilar WHERE KullaniciAdi = N'admin')
INSERT INTO dbo.Kullanicilar (KullaniciAdi, SifreHash, AdSoyad, Rol)
VALUES (N'admin', N'$2a$11$bcjM6fVQwvnkKBOaBA8VtOYb8Hz0ty2yXqqdRkBMSlL8z59bx7GT6', N'Yönetici', N'admin');
GO
