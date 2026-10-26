using System.Security.Claims;
using Butcem.Api.Infrastructure;
using Dapper;

namespace Butcem.Api.Features;

/// <summary>
/// Satış fiyatı değişikliği fişi. Mikro'da: Stok > Fiyat değişiklik fişi. Firma tek fiyatla çalışır:
/// 1 numaralı satış fiyat listesi, tüm depolar (sfiyat_deposirano = 0).
/// Mikro'nun yazdığı (fiş 25777 ile karşılaştırıldı):
///  - STOK_FIYAT_DEGISIKLIKLERI: fiş başına seri '', sıra MAX+1; her ürün bir satır (fid_fiyat_no 1, eski ve yeni fiyat, fid_yapildi_fl 1)
///  - STOK_SATIS_FIYAT_LISTELERI: sfiyat_fiyati yeni fiyat, lastup alanları
///  - STOKLAR: yalnız sto_lastup_user / sto_lastup_date (kasaya değişen kart olarak gider)
/// </summary>
public static class FiyatDegisikligi
{
    private static readonly System.Globalization.CultureInfo Tr = new("tr-TR");

    public sealed record SatirIstek(string Urun, decimal YeniFiyat);
    public sealed record Istek(DateTime? Tarih, List<SatirIstek> Satirlar);
    public sealed record Sonuc(int Sira, int SatirSayisi);

    public sealed record Urun(string Kod, string Ad, string Birim, decimal Fiyat, decimal KdvOrani, decimal SonAlis, DateTime? SonDegisim);
    public sealed record Ozet(int Sira, DateTime Tarih, int SatirSayisi, DateTime Olusturma);
    public sealed record Satir(int SatirNo, string StokKodu, string UrunAdi, decimal EskiFiyat, decimal YeniFiyat);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/fiyat").RequireYetki(Yetkiler.FiyatDegisikligi).WithTags("Fiyat değişikliği");

        // Okutulan ürünün şu anki satış fiyatı ve kâr hesabı için son alış fiyatı (KDV hariç, iskontolar düşülmüş).
        g.MapGet("/urun", (string k, Db db, IConfiguration config) =>
        {
            using var conn = db.Mikro();
            return Results.Ok(UrunBilgisi(conn, null, config, k));
        });

        g.MapPost("/", (Istek istek, ClaimsPrincipal user, MikroYazici yazici, IConfiguration config, IslemLogu log) =>
        {
            Dogrula(istek);
            var sonuc = Kaydet(yazici, config, istek);
            log.Yaz(user.Id(), "fiyat_degisikligi", new { istek, sonuc });
            return Results.Ok(sonuc);
        });

        g.MapGet("/", (DateTime? bas, DateTime? bit, Db db) =>
        {
            using var conn = db.Mikro();
            return Results.Ok(conn.Query<Ozet>("""
                SELECT TOP 30 fid_evrak_sira_no AS Sira, MIN(fid_evrak_tarih) AS Tarih, COUNT(*) AS SatirSayisi, MIN(fid_create_date) AS Olusturma
                FROM STOK_FIYAT_DEGISIKLIKLERI WITH (NOLOCK)
                WHERE fid_evrak_seri_no = '' AND fid_evrak_tarih BETWEEN @bas AND @bit AND ISNULL(fid_iptal, 0) = 0
                GROUP BY fid_evrak_sira_no
                ORDER BY fid_evrak_sira_no DESC
                """, new { bas = (bas ?? DateTime.Today.AddDays(-30)).Date, bit = (bit ?? DateTime.Today).Date }));
        });

        g.MapGet("/detay", (int sira, Db db) =>
        {
            using var conn = db.Mikro();
            return Results.Ok(conn.Query<Satir>("""
                SELECT f.fid_evrak_satir_no AS SatirNo, f.fid_stok_kod AS StokKodu, ISNULL(s.sto_isim, '') AS UrunAdi,
                       CAST(f.fid_eskifiy_tutar AS decimal(18,2)) AS EskiFiyat, CAST(f.fid_yenifiy_tutar AS decimal(18,2)) AS YeniFiyat
                FROM STOK_FIYAT_DEGISIKLIKLERI f WITH (NOLOCK)
                LEFT JOIN STOKLAR s WITH (NOLOCK) ON s.sto_kod = f.fid_stok_kod
                WHERE f.fid_evrak_seri_no = '' AND f.fid_evrak_sira_no = @sira
                ORDER BY f.fid_evrak_satir_no
                """, new { sira }));
        });
    }

    private static Urun UrunBilgisi(System.Data.IDbConnection conn, System.Data.IDbTransaction? tx, IConfiguration config, string k)
    {
        var stok = MikroEvrak.Stok(conn, tx, k);
        var fiyat = conn.ExecuteScalar<decimal?>("""
            SELECT sfiyat_fiyati FROM STOK_SATIS_FIYAT_LISTELERI WITH (NOLOCK)
            WHERE sfiyat_stokkod = @kod AND sfiyat_listesirano = @liste AND sfiyat_deposirano = 0
            """, new { kod = stok.Kod, liste = Mikro.SatisFiyatListesi }, tx) ?? 0;
        var sonDegisim = conn.ExecuteScalar<DateTime?>("""
            SELECT MAX(fid_tarih) FROM STOK_FIYAT_DEGISIKLIKLERI WITH (NOLOCK)
            WHERE fid_stok_kod = @kod AND fid_fiyat_no = @liste AND ISNULL(fid_iptal, 0) = 0
            """, new { kod = stok.Kod, liste = Mikro.SatisFiyatListesi }, tx);
        var kdv = Faturalar.KdvOrani(config, stok.VergiPntr);
        var alis = MikroEvrak.SonAlisFiyati(conn, tx, stok.Kod, DateTime.Today);
        return new Urun(stok.Kod, stok.Ad, stok.Birim, Math.Round(fiyat, 2), kdv, Math.Round(alis, 4), sonDegisim);
    }

    private static void Dogrula(Istek istek)
    {
        if (istek.Satirlar is not { Count: > 0 }) throw new IslemHatasi("En az bir ürün ekleyin.");
        if (istek.Satirlar.Count > 1000) throw new IslemHatasi("Bir fişte en fazla 1000 ürün olabilir.");
        if (istek.Satirlar.Any(s => s.YeniFiyat <= 0)) throw new IslemHatasi("Yeni fiyat sıfırdan büyük olmalı.");
        if (istek.Tarih is { } t && t.Date > DateTime.Today) throw new IslemHatasi("İleri tarihli fiyat değişikliği girilemez.");
    }

    public static Sonuc Kaydet(MikroYazici yazici, IConfiguration config, Istek istek) => yazici.Calistir((conn, tx) =>
    {
        var tarih = (istek.Tarih ?? DateTime.Today).Date;
        var sira = conn.ExecuteScalar<int>(
            "SELECT ISNULL(MAX(fid_evrak_sira_no), 0) + 1 FROM STOK_FIYAT_DEGISIKLIKLERI WITH (UPDLOCK, HOLDLOCK) WHERE fid_evrak_seri_no = ''",
            transaction: tx);
        var simdi = MikroEvrak.Simdi();
        var gorulen = new HashSet<string>();
        var satirNo = 0;

        foreach (var s in istek.Satirlar)
        {
            var stok = MikroEvrak.Stok(conn, tx, s.Urun);
            if (!gorulen.Add(stok.Kod)) throw new IslemHatasi($"{stok.Ad} fişte iki kez var.");
            var yeni = Math.Round(s.YeniFiyat, 2, MidpointRounding.AwayFromZero);
            var eski = conn.ExecuteScalar<decimal?>("""
                SELECT sfiyat_fiyati FROM STOK_SATIS_FIYAT_LISTELERI WITH (UPDLOCK, ROWLOCK)
                WHERE sfiyat_stokkod = @kod AND sfiyat_listesirano = @liste AND sfiyat_deposirano = 0
                """, new { kod = stok.Kod, liste = Mikro.SatisFiyatListesi }, tx);
            if (eski == yeni) throw new IslemHatasi($"{stok.Ad}: yeni fiyat eskisiyle aynı ({yeni.ToString("N2", Tr)}).");

            yazici.Ekle(conn, tx, "STOK_FIYAT_DEGISIKLIKLERI", new Dictionary<string, object?>
            {
                ["fid_Guid"] = MikroYazici.YeniGuid(),
                ["fid_fileid"] = (short)17,
                ["fid_create_user"] = yazici.KullaniciNo,
                ["fid_create_date"] = simdi,
                ["fid_lastup_user"] = yazici.KullaniciNo,
                ["fid_lastup_date"] = simdi,
                ["fid_evrak_satir_no"] = satirNo++,
                ["fid_evrak_seri_no"] = "",
                ["fid_evrak_sira_no"] = sira,
                ["fid_evrak_tarih"] = tarih,
                ["fid_belge_tarih"] = tarih,
                ["fid_stok_kod"] = stok.Kod,
                ["fid_tarih"] = tarih,
                ["fid_fiyat_no"] = Mikro.SatisFiyatListesi,
                ["fid_eskifiy_tutar"] = (double)(eski ?? 0),
                ["fid_yenifiy_tutar"] = (double)yeni,
                ["fid_yapildi_fl"] = true,
                ["fid_birim_pntr"] = (byte)1,
            });

            if (eski is null)
                yazici.Ekle(conn, tx, "STOK_SATIS_FIYAT_LISTELERI", new Dictionary<string, object?>
                {
                    ["sfiyat_Guid"] = MikroYazici.YeniGuid(),
                    ["sfiyat_fileid"] = (short)228,
                    ["sfiyat_create_user"] = yazici.KullaniciNo,
                    ["sfiyat_create_date"] = simdi,
                    ["sfiyat_lastup_user"] = yazici.KullaniciNo,
                    ["sfiyat_lastup_date"] = simdi,
                    ["sfiyat_stokkod"] = stok.Kod,
                    ["sfiyat_listesirano"] = Mikro.SatisFiyatListesi,
                    ["sfiyat_deposirano"] = 0,
                    ["sfiyat_birim_pntr"] = (byte)1,
                    ["sfiyat_fiyati"] = (double)yeni,
                });
            else
                conn.Execute("""
                    UPDATE STOK_SATIS_FIYAT_LISTELERI SET sfiyat_fiyati = @yeni, sfiyat_lastup_user = @k, sfiyat_lastup_date = @simdi
                    WHERE sfiyat_stokkod = @kod AND sfiyat_listesirano = @liste AND sfiyat_deposirano = 0
                    """, new { yeni = (double)yeni, k = yazici.KullaniciNo, simdi, kod = stok.Kod, liste = Mikro.SatisFiyatListesi }, tx);

            // Mikro stok kartının değiştiği zamanı da günceller; kasa programları değişen kartları buradan alır.
            conn.Execute("UPDATE STOKLAR SET sto_lastup_user = @k, sto_lastup_date = @simdi WHERE sto_kod = @kod",
                          new { k = yazici.KullaniciNo, simdi, kod = stok.Kod }, tx);
        }
        return new Sonuc(sira, satirNo);
    });
}
