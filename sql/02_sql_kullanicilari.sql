-- Bütçem API ve Orion için SQL girişleri. CANLI sunucuda bir kez, sysadmin ile çalıştırın.
-- Şifreleri değiştirin; aynı şifreleri API'nin appsettings.json dosyasına yazın.
-- Mikro'ya yalnızca OKUMA yetkisi verilir: API Mikro'ya hiçbir şey yazamaz.
-- (Fatura / çek / sevk yazma özellikleri eklendiğinde bu dosyaya ayrı, dar yetkiler eklenecek.)

USE master;
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = 'butcem_api')
    CREATE LOGIN butcem_api WITH PASSWORD = 'GUCLU-BIR-SIFRE-YAZIN', CHECK_POLICY = ON;
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = 'butcem_okuma')
    CREATE LOGIN butcem_okuma WITH PASSWORD = 'BASKA-GUCLU-BIR-SIFRE', CHECK_POLICY = ON;
GO

-- Mikro: okuma + stok miktarı fonksiyonu
USE MikroDB_V16_BUTCEM2024;
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'butcem_api')
    CREATE USER butcem_api FOR LOGIN butcem_api;
ALTER ROLE db_datareader ADD MEMBER butcem_api;
GRANT EXECUTE ON dbo.fn_DepodakiMiktar TO butcem_api;
DENY INSERT, UPDATE, DELETE ON SCHEMA::dbo TO butcem_api;
GO

-- Uygulama veritabanı (kullanıcılar, yazıcılar, etiket kuyruğu): okuma + yazma
USE ButcemApp;
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'butcem_api')
    CREATE USER butcem_api FOR LOGIN butcem_api;
ALTER ROLE db_datareader ADD MEMBER butcem_api;
ALTER ROLE db_datawriter ADD MEMBER butcem_api;
GO

-- Orion (merkez): yalnızca okuma. Şube Orion veritabanları da eklenecekse aynısını orada çalıştırın.
USE ORION;
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'butcem_okuma')
    CREATE USER butcem_okuma FOR LOGIN butcem_okuma;
ALTER ROLE db_datareader ADD MEMBER butcem_okuma;
GO
