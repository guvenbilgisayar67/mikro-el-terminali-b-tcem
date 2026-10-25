-- Mikro'ya evrak yazma (depolar arası sevk, çek giriş/çıkış, irsaliye/fatura) için ayrı SQL girişi.
-- Okuma girişi (butcem_api) yazamaz; yazma yalnızca bu girişle ve yalnızca aşağıdaki tablolara yapılır.
-- CANLI sunucuda, önce kopya veritabanında denendikten sonra, sysadmin ile bir kez çalıştırın.
-- Şifreyi değiştirin ve API'nin appsettings.json dosyasında ConnectionStrings:MikroYazma'ya yazın.

USE master;
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = 'butcem_yazma')
    CREATE LOGIN butcem_yazma WITH PASSWORD = 'UCUNCU-GUCLU-BIR-SIFRE', CHECK_POLICY = ON;
GO

USE MikroDB_V16_BUTCEM2024;
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'butcem_yazma')
    CREATE USER butcem_yazma FOR LOGIN butcem_yazma;
ALTER ROLE db_datareader ADD MEMBER butcem_yazma;

-- Evrak satırları: yalnız ekleme
GRANT INSERT ON dbo.STOK_HAREKETLERI       TO butcem_yazma;   -- sevk, irsaliye, fatura satırları
GRANT INSERT ON dbo.CARI_HESAP_HAREKETLERI TO butcem_yazma;   -- fatura ve çek bordrosu cari hareketi
GRANT INSERT ON dbo.ODEME_EMIRLERI         TO butcem_yazma;   -- yeni çek kartı
GRANT INSERT ON dbo.E_FATURA_ISLEMLERI     TO butcem_yazma;   -- satış faturasının e-belge kaydı (Mikro'dan gönderilir)

-- Güncelleme yalnız bu kolonlarda:
GRANT UPDATE ON dbo.ODEME_EMIRLERI (sck_nerede_cari_cins, sck_nerede_cari_kodu, sck_nerede_cari_grupno, sck_sonpoz,
                                    sck_son_hareket_tarihi, sck_lastup_user, sck_lastup_date) TO butcem_yazma;  -- çek ciro
GRANT UPDATE ON dbo.STOK_HAREKETLERI (sth_fat_uid, sth_lastup_user, sth_lastup_date) TO butcem_yazma;          -- irsaliyeyi faturaya bağlama

-- Silme yetkisi yok.
DENY DELETE ON SCHEMA::dbo TO butcem_yazma;
GO
