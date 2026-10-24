using System.Security.Claims;
using Butcem.Api.Infrastructure;
using Dapper;

namespace Butcem.Api.Features;

/// <summary>
/// Satış raporu (madde 1). Kaynak: Mikro STOK_HAREKETLERI satış faturası satırları.
/// Kasa satışları POS'tan ertesi gün aktarıldığı için bugünün verisi eksik olabilir; anlık ciro için /api/ciro.
/// Net tutar = sth_tutar − iskonto1..6, KDV dahil = net + sth_vergi. İadeler eksi olarak düşülür.
/// </summary>
public static class SatisRaporu
{
    public enum Kirilim { Gun, Depo, Kasa, Anagrup, Reyon, Marka, Urun }
    public enum Tur { Hepsi, Perakende, Toptan }

    public sealed record Satir(string Anahtar, string Ad, int FaturaSayisi, decimal Miktar,
                               decimal NetTutar, decimal Kdv, decimal Toplam, decimal IadeToplam);

    public sealed record Sonuc(DateOnly Baslangic, DateOnly Bitis, string Kirilim, decimal GenelToplam,
                               decimal GenelKdv, IReadOnlyList<Satir> Satirlar, string Not);

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/satis", (DateOnly bas, DateOnly bit, Kirilim? kirilim, Tur? tur, int? depo, int? ilk,
                                  ClaimsPrincipal user, Db db) =>
        {
            if (bit < bas) return Results.BadRequest("Bitiş tarihi başlangıçtan önce olamaz.");
            if (bit.DayNumber - bas.DayNumber > 400) return Results.BadRequest("En fazla 400 günlük aralık sorgulanabilir.");

            // Mağaza kullanıcısı yalnızca kendi deposunu görür.
            if (user.MagazaKullanicisi()) depo = user.Depo();

            var k = kirilim ?? Kirilim.Gun;
            var (anahtar, ad, join) = k switch
            {
                Kirilim.Gun => ("CONVERT(varchar(10), h.sth_tarih, 23)", "CONVERT(varchar(10), h.sth_tarih, 104)", ""),
                Kirilim.Depo => ("CAST(h.sth_cikis_depo_no AS varchar(10))", "ISNULL(d.dep_adi, '')",
                                 "LEFT JOIN DEPOLAR d WITH (NOLOCK) ON d.dep_no = h.sth_cikis_depo_no"),
                Kirilim.Kasa => ("h.sth_evrakno_seri", "h.sth_evrakno_seri", ""),
                Kirilim.Anagrup => ("ISNULL(s.sto_anagrup_kod, '')", "ISNULL(g.san_isim, '(grupsuz)')",
                                    "LEFT JOIN STOKLAR s WITH (NOLOCK) ON s.sto_kod = h.sth_stok_kod " +
                                    "LEFT JOIN STOK_ANA_GRUPLARI g WITH (NOLOCK) ON g.san_kod = s.sto_anagrup_kod"),
                Kirilim.Reyon => ("ISNULL(s.sto_reyon_kodu, '')", "ISNULL(r.ryn_ismi, '(reyonsuz)')",
                                  "LEFT JOIN STOKLAR s WITH (NOLOCK) ON s.sto_kod = h.sth_stok_kod " +
                                  "LEFT JOIN STOK_REYONLARI r WITH (NOLOCK) ON r.ryn_kod = s.sto_reyon_kodu"),
                Kirilim.Marka => ("ISNULL(s.sto_marka_kodu, '')", "ISNULL(m.mrk_ismi, '(markasız)')",
                                  "LEFT JOIN STOKLAR s WITH (NOLOCK) ON s.sto_kod = h.sth_stok_kod " +
                                  "LEFT JOIN STOK_MARKALARI m WITH (NOLOCK) ON m.mrk_kod = s.sto_marka_kodu"),
                Kirilim.Urun => ("h.sth_stok_kod", "ISNULL(s.sto_isim, h.sth_stok_kod)",
                                 "LEFT JOIN STOKLAR s WITH (NOLOCK) ON s.sto_kod = h.sth_stok_kod"),
                _ => throw new ArgumentOutOfRangeException(nameof(kirilim)),
            };

            var cinsFiltre = (tur ?? Tur.Hepsi) switch
            {
                Tur.Perakende => $"AND h.sth_cins = {Mikro.StokHarCins.Perakende}",
                Tur.Toptan => $"AND h.sth_cins = {Mikro.StokHarCins.Toptan}",
                _ => "",
            };
            var top = k == Kirilim.Urun ? $"TOP ({Math.Clamp(ilk ?? 100, 1, 1000)})" : "";
            var sirala = k is Kirilim.Gun or Kirilim.Kasa ? "Anahtar" : "Toplam DESC";

            // Rapor sorguları NOLOCK ile çalışır: Mikro kullanıcılarının kayıtlarını bloklamamak için.
            var sql = $"""
                WITH h AS (
                    SELECT h.*, CASE WHEN h.sth_normal_iade = 1 THEN -1 ELSE 1 END AS isaret
                    FROM STOK_HAREKETLERI h WITH (NOLOCK)
                    WHERE h.sth_tarih >= @bas AND h.sth_tarih < @bitSonrasi
                      AND h.sth_tip = {Mikro.StokHarTip.Cikis}
                      AND h.sth_evraktip = {Mikro.StokEvrakTip.CikisFaturasi}
                      AND ISNULL(h.sth_iptal, 0) = 0
                      AND (@depo IS NULL OR h.sth_cikis_depo_no = @depo)
                      {cinsFiltre}
                )
                SELECT {top}
                    {anahtar} AS Anahtar,
                    MAX({ad}) AS Ad,
                    COUNT(DISTINCT CONCAT(h.sth_evrakno_seri, '-', h.sth_evrakno_sira)) AS FaturaSayisi,
                    CAST(SUM(h.isaret * h.sth_miktar) AS decimal(18,3)) AS Miktar,
                    CAST(SUM(h.isaret * (h.sth_tutar - (h.sth_iskonto1 + h.sth_iskonto2 + h.sth_iskonto3
                         + h.sth_iskonto4 + h.sth_iskonto5 + h.sth_iskonto6))) AS decimal(18,2)) AS NetTutar,
                    CAST(SUM(h.isaret * h.sth_vergi) AS decimal(18,2)) AS Kdv,
                    CAST(SUM(h.isaret * (h.sth_tutar - (h.sth_iskonto1 + h.sth_iskonto2 + h.sth_iskonto3
                         + h.sth_iskonto4 + h.sth_iskonto5 + h.sth_iskonto6) + h.sth_vergi)) AS decimal(18,2)) AS Toplam,
                    CAST(SUM(CASE WHEN h.isaret = -1 THEN h.sth_tutar - (h.sth_iskonto1 + h.sth_iskonto2 + h.sth_iskonto3
                         + h.sth_iskonto4 + h.sth_iskonto5 + h.sth_iskonto6) + h.sth_vergi ELSE 0 END) AS decimal(18,2)) AS IadeToplam
                FROM h {join}
                GROUP BY {anahtar}
                ORDER BY {sirala};
                """;

            using var conn = db.Mikro();
            var satirlar = conn.Query<Satir>(sql, new
            {
                bas = bas.ToDateTime(TimeOnly.MinValue),
                bitSonrasi = bit.AddDays(1).ToDateTime(TimeOnly.MinValue),
                depo,
            }, commandTimeout: 180).ToList();

            return Results.Ok(new Sonuc(bas, bit, k.ToString().ToLowerInvariant(),
                satirlar.Sum(s => s.Toplam), satirlar.Sum(s => s.Kdv), satirlar,
                "Kasa satışları Mikro'ya ertesi gün aktarılır; bugünün tutarı için /api/ciro kullanın."));
        })
        .RequireAuthorization(Roller.RaporOkuyabilir)
        .WithTags("Raporlar");
    }
}
