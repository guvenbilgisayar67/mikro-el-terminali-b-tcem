using System.Security.Claims;
using Butcem.Api.Infrastructure;
using Dapper;

namespace Butcem.Api.Features;

/// <summary>Barkoddan ürün bilgisi (el terminali fiyat sorgu ve etiket). Kaynak: Mikro.</summary>
public static class Stok
{
    public sealed record Urun(string StokKodu, string UrunAdi, string Barkod, string? Birim, decimal Fiyat,
                              DateTime? FiyatDegisimTarihi, decimal? EskiFiyat, bool YerliUretim,
                              string? Mensei, string? AnagrupKod, string? ReyonKod);

    public sealed record DepoMiktari(int DepoNo, string DepoAdi, decimal Miktar);

    public sealed record Depo(int No, string Ad);

    /// <summary>Etiket ve fiyat sorgusu. Fiyat listesi 1, depo 0 (tüm depolar), birim 1.</summary>
    private const string UrunSecim = """
        SELECT TOP 1 s.sto_kod AS StokKodu, s.sto_isim AS UrunAdi, ISNULL(b.bar_kodu, s.sto_kod) AS Barkod,
               s.sto_birim1_ad AS Birim, CAST(ISNULL(f.sfiyat_fiyati, 0) AS decimal(18,2)) AS Fiyat,
               fd.fid_tarih AS FiyatDegisimTarihi, CAST(fd.fid_eskifiy_tutar AS decimal(18,2)) AS EskiFiyat,
               CAST(CASE WHEN UPPER(s.sto_mensei) IN (N'TÜRKİYE', N'TURKIYE', N'TÜRKIYE') THEN 1 ELSE 0 END AS bit) AS YerliUretim,
               s.sto_mensei AS Mensei, s.sto_anagrup_kod AS AnagrupKod, s.sto_reyon_kodu AS ReyonKod
        """;

    private const string UrunEkler = """
        LEFT JOIN STOK_SATIS_FIYAT_LISTELERI f WITH (NOLOCK)
               ON f.sfiyat_stokkod = s.sto_kod AND f.sfiyat_listesirano = 1
              AND f.sfiyat_deposirano = 0 AND f.sfiyat_birim_pntr = 1
        OUTER APPLY (SELECT TOP 1 x.fid_tarih, x.fid_eskifiy_tutar
                     FROM STOK_FIYAT_DEGISIKLIKLERI x WITH (NOLOCK)
                     WHERE x.fid_stok_kod = s.sto_kod AND x.fid_fiyat_no = 1
                     ORDER BY x.fid_tarih DESC, x.fid_create_date DESC) fd
        """;

    public static Urun? BarkodBul(System.Data.IDbConnection conn, string barkod) =>
        conn.QueryFirstOrDefault<Urun>($"""
            {UrunSecim}
            FROM BARKOD_TANIMLARI b WITH (NOLOCK)
            JOIN STOKLAR s WITH (NOLOCK) ON s.sto_kod = b.bar_stokkodu
            {UrunEkler}
            WHERE b.bar_kodu = @barkod
            """, new { barkod })
        // Barkod tanımı yoksa stok kodu olarak dene (bu firmada stok kodu çoğu üründe barkodun kendisi).
        ?? conn.QueryFirstOrDefault<Urun>($"""
            {UrunSecim}
            FROM STOKLAR s WITH (NOLOCK)
            LEFT JOIN BARKOD_TANIMLARI b WITH (NOLOCK) ON b.bar_stokkodu = s.sto_kod
            {UrunEkler}
            WHERE s.sto_kod = @barkod
            """, new { barkod });

    public static IEnumerable<Urun> StokKodlariylaBul(System.Data.IDbConnection conn, IEnumerable<string> stokKodlari) =>
        conn.Query<Urun>($"""
            SELECT u.* FROM (SELECT DISTINCT value AS kod FROM STRING_SPLIT(@kodlar, '|')) k
            CROSS APPLY (
                {UrunSecim}
                FROM STOKLAR s WITH (NOLOCK)
                LEFT JOIN BARKOD_TANIMLARI b WITH (NOLOCK) ON b.bar_stokkodu = s.sto_kod
                {UrunEkler}
                WHERE s.sto_kod = k.kod
            ) u
            """, new { kodlar = string.Join('|', stokKodlari) });

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/stok/{barkod}", (string barkod, bool? miktar, ClaimsPrincipal user, Db db) =>
        {
            using var conn = db.Mikro();
            var urun = BarkodBul(conn, barkod.Trim());
            if (urun is null) return Results.NotFound($"Barkod bulunamadı: {barkod}");

            IEnumerable<DepoMiktari>? miktarlar = null;
            if (miktar != false)
            {
                miktarlar = conn.Query<DepoMiktari>("""
                    SELECT d.dep_no AS DepoNo, d.dep_adi AS DepoAdi,
                           CAST(dbo.fn_DepodakiMiktar(@kod, d.dep_no, GETDATE()) AS decimal(18,3)) AS Miktar
                    FROM DEPOLAR d WITH (NOLOCK)
                    WHERE (@depo IS NULL OR d.dep_no = @depo)
                    ORDER BY d.dep_no;
                    """, new { kod = urun.StokKodu, depo = user.MagazaKullanicisi() ? user.Depo() : null });
            }
            return Results.Ok(new { urun, miktarlar });
        })
        .RequireAuthorization(Roller.Herkes)
        .WithTags("Stok");

        // Terminalin cihazda tuttuğu ürün listesi (çevrimdışı fiyat sorgu, etiket, sevk, sayım):
        // [barkod, stok kodu, ad, birim, satış fiyatı, fiyat değişim tarihi (yyyy-MM-dd ya da ""), depodaki miktar ya da null].
        // Miktar yalnız depo verilirse (mağaza kullanıcısında kendi deposu) ve listenin indirildiği anki stoktur.
        app.MapGet("/api/katalog", (int? depo, ClaimsPrincipal user, Db db) =>
        {
            if (user.MagazaKullanicisi()) depo = user.Depo();
            using var conn = db.Mikro();
            var liste = conn.Query<(string Barkod, string Kod, string Ad, string? Birim, decimal Fiyat, DateTime? Fd, decimal? Miktar)>("""
                WITH urun AS (
                    SELECT b.bar_kodu AS barkod, s.sto_kod AS kod, s.sto_isim AS ad, s.sto_birim1_ad AS birim
                    FROM BARKOD_TANIMLARI b WITH (NOLOCK) JOIN STOKLAR s WITH (NOLOCK) ON s.sto_kod = b.bar_stokkodu
                    WHERE ISNULL(s.sto_pasif_fl, 0) = 0
                    UNION
                    SELECT s.sto_kod, s.sto_kod, s.sto_isim, s.sto_birim1_ad FROM STOKLAR s WITH (NOLOCK) WHERE ISNULL(s.sto_pasif_fl, 0) = 0
                ),
                fiyat AS (
                    SELECT sfiyat_stokkod AS kod, MAX(sfiyat_fiyati) AS fiyat FROM STOK_SATIS_FIYAT_LISTELERI WITH (NOLOCK)
                    WHERE sfiyat_listesirano = 1 AND sfiyat_deposirano = 0 AND sfiyat_birim_pntr = 1
                    GROUP BY sfiyat_stokkod
                ),
                fd AS (
                    SELECT fid_stok_kod AS kod, MAX(fid_tarih) AS tarih FROM STOK_FIYAT_DEGISIKLIKLERI WITH (NOLOCK)
                    WHERE fid_fiyat_no = 1 GROUP BY fid_stok_kod
                ),
                stok AS (
                    SELECT sho_StokKodu AS kod, SUM(sho_GirisNormal + sho_GirisIade - sho_CikisNormal - sho_CikisIade) AS miktar
                    FROM STOK_HAREKETLERI_OZET WITH (NOLOCK)
                    WHERE @depo IS NOT NULL AND sho_Depo = @depo AND sho_HareketCins NOT IN (9, 15)
                    GROUP BY sho_StokKodu
                )
                SELECT u.barkod, u.kod, u.ad, u.birim, CAST(ISNULL(f.fiyat, 0) AS decimal(18,2)), fd.tarih,
                       CASE WHEN @depo IS NULL THEN NULL ELSE CAST(ISNULL(st.miktar, 0) AS decimal(18,3)) END
                FROM urun u
                LEFT JOIN fiyat f ON f.kod = u.kod
                LEFT JOIN fd ON fd.kod = u.kod
                LEFT JOIN stok st ON st.kod = u.kod
                """, new { depo }, commandTimeout: 120);
            return Results.Ok(new
            {
                zaman = DateTime.Now,
                depo,
                urunler = liste.Select(x => new object?[]
                {
                    x.Barkod, x.Kod, x.Ad.Trim(), x.Birim ?? "", x.Fiyat, x.Fd?.ToString("yyyy-MM-dd") ?? "", x.Miktar,
                }),
            });
        })
        .RequireAuthorization(Roller.Herkes)
        .WithTags("Stok");

        app.MapGet("/api/depolar", (Db db) =>
        {
            using var conn = db.Mikro();
            return Results.Ok(conn.Query<Depo>("SELECT dep_no AS No, dep_adi AS Ad FROM DEPOLAR WITH (NOLOCK) ORDER BY dep_no"));
        })
        .RequireAuthorization(Roller.Herkes)
        .WithTags("Stok");
    }
}
