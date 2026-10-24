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

/* İlk yönetici. Şifre: Butcem2026!  — ilk girişten sonra değiştirin. */
IF NOT EXISTS (SELECT 1 FROM dbo.Kullanicilar WHERE KullaniciAdi = N'admin')
INSERT INTO dbo.Kullanicilar (KullaniciAdi, SifreHash, AdSoyad, Rol)
VALUES (N'admin', N'$2a$11$bcjM6fVQwvnkKBOaBA8VtOYb8Hz0ty2yXqqdRkBMSlL8z59bx7GT6', N'Yönetici', N'admin');
GO
