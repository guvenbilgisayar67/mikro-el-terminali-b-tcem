using System.Data;
using Dapper;

namespace Butcem.Api.Infrastructure;

/// <summary>Evrak yazan özelliklerin ortak kontrolleri ve okumaları.</summary>
public static class MikroEvrak
{
    public sealed record StokBilgi(string Kod, string Ad, int VergiPntr, string Birim);

    private sealed record DepoKilit(string Ad, DateTime Kilit);

    public sealed record CariBilgi(string Kod, string Unvan, string VergiDairesi, string VergiNo, int FaturaAdresNo);

    /// <summary>Stok kodu ya da barkoddan stok kartı. Bulunamazsa hata.</summary>
    public static StokBilgi Stok(IDbConnection conn, IDbTransaction? tx, string kodVeyaBarkod)
    {
        var s = conn.QueryFirstOrDefault<StokBilgi>("""
            SELECT TOP 1 s.sto_kod AS Kod, s.sto_isim AS Ad, CAST(s.sto_toptan_vergi AS int) AS VergiPntr, s.sto_birim1_ad AS Birim
            FROM STOKLAR s WITH (NOLOCK)
            WHERE s.sto_kod = @k
               OR s.sto_kod = (SELECT TOP 1 b.bar_stokkodu FROM BARKOD_TANIMLARI b WITH (NOLOCK) WHERE b.bar_kodu = @k)
            ORDER BY CASE WHEN s.sto_kod = @k THEN 0 ELSE 1 END
            """, new { k = kodVeyaBarkod.Trim() }, tx);
        return s ?? throw new IslemHatasi($"Ürün bulunamadı: {kodVeyaBarkod}");
    }

    public static CariBilgi Cari(IDbConnection conn, IDbTransaction? tx, string kod)
    {
        var c = conn.QueryFirstOrDefault<CariBilgi>("""
            SELECT cari_kod AS Kod, cari_unvan1 AS Unvan, cari_vdaire_adi AS VergiDairesi, cari_vdaire_no AS VergiNo,
                   CAST(cari_fatura_adres_no AS int) AS FaturaAdresNo
            FROM CARI_HESAPLAR WITH (NOLOCK) WHERE cari_kod = @kod
            """, new { kod = kod.Trim() }, tx);
        return c ?? throw new IslemHatasi($"Cari bulunamadı: {kod}");
    }

    /// <summary>Deponun sorumluluk merkezi (faturada cari ve stok sorumluluk merkezi buradan gelir).</summary>
    public static string DepoSorumlulukMerkezi(IDbConnection conn, IDbTransaction? tx, int depoNo) =>
        conn.ExecuteScalar<string?>("SELECT dep_sor_mer_kodu FROM DEPOLAR WITH (NOLOCK) WHERE dep_no = @depoNo", new { depoNo }, tx) ?? "";

    /// <summary>Depo var mı ve evrak tarihi deponun kilit tarihinden sonra mı.</summary>
    public static void DepoKontrol(IDbConnection conn, IDbTransaction? tx, int depoNo, DateTime tarih)
    {
        var d = conn.QueryFirstOrDefault<DepoKilit>(
            "SELECT dep_adi AS Ad, dep_KilitTarihi AS Kilit FROM DEPOLAR WITH (NOLOCK) WHERE dep_no = @depoNo", new { depoNo }, tx)
            ?? throw new IslemHatasi($"Depo bulunamadı: {depoNo}");
        if (d.Kilit > MikroYazici.BosTarih && tarih.Date <= d.Kilit.Date)
            throw new IslemHatasi($"\"{d.Ad.Trim()}\" deposu Mikro'da {d.Kilit:dd.MM.yyyy} tarihine kadar kilitli; bu tarihe evrak girilemez.");
    }

    /// <summary>
    /// Son alış birim fiyatı (iskontolar düşülmüş). Normal alış faturası/irsaliyesi, iadeler hariç.
    /// Depolar arası sevkte Mikro'nun önerdiği maliyet fiyatıyla örneklerde aynı çıktı.
    /// </summary>
    public static decimal SonAlisFiyati(IDbConnection conn, IDbTransaction? tx, string stokKod, DateTime tarih) =>
        conn.ExecuteScalar<decimal?>("""
            SELECT TOP 1 CAST((h.sth_tutar - h.sth_iskonto1 - h.sth_iskonto2 - h.sth_iskonto3
                               - h.sth_iskonto4 - h.sth_iskonto5 - h.sth_iskonto6) / h.sth_miktar AS decimal(18,6))
            FROM STOK_HAREKETLERI h WITH (NOLOCK)
            WHERE h.sth_stok_kod = @stokKod AND h.sth_tip = 0 AND h.sth_evraktip IN (3, 13)
              AND h.sth_normal_iade = 0 AND h.sth_miktar > 0 AND h.sth_tarih <= @tarih
            ORDER BY h.sth_tarih DESC, h.sth_create_date DESC
            """, new { stokKod, tarih }, tx) ?? 0m;

    /// <summary>Mikro tarih alanlarına milisaniyesiyle yazılan an (create/lastup).</summary>
    public static DateTime Simdi() => DateTime.Now;

    public static DateTime Gun(DateTime t) => t.Date;

    public static double Yuvarla(decimal d, int basamak = 2) => (double)Math.Round(d, basamak, MidpointRounding.AwayFromZero);

    public static string Kes(string? s, int uzunluk) => s is null ? "" : s.Length <= uzunluk ? s : s[..uzunluk];
}
