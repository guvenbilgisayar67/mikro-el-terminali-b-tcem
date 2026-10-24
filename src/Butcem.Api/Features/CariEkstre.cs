using Butcem.Api.Infrastructure;
using Dapper;

namespace Butcem.Api.Features;

/// <summary>
/// Cari hesap listesi ve ekstresi (madde 2). Kaynak: CARI_HESAPLAR, CARI_HESAP_HAREKETLERI.
/// cha_tip 0 = borç, 1 = alacak; tutar cha_meblag. Bu firmada tüm hareketler TL (cha_d_cins = 0).
/// </summary>
public static class CariEkstre
{
    public sealed record Cari(string Kod, string Unvan, string? VergiNo, string? Telefon, string? Eposta,
                              decimal Borc, decimal Alacak, decimal Bakiye);

    public sealed record Hareket(DateTime Tarih, string EvrakNo, string EvrakAdi, string? BelgeNo,
                                 string? Aciklama, decimal Borc, decimal Alacak, decimal Bakiye,
                                 DateTime? Vade, Guid Guid);

    public sealed record Ekstre(Cari Cari, DateOnly Baslangic, DateOnly Bitis, decimal DevirBakiye,
                                decimal DonemBorc, decimal DonemAlacak, decimal SonBakiye,
                                IReadOnlyList<Hareket> Hareketler);

    private sealed record HamHareket(DateTime Tarih, string? Seri, int Sira, int EvrakTip, int Cinsi, int NormalIade,
                                     string? BelgeNo, string? Aciklama, decimal Borc, decimal Alacak,
                                     DateTime? Vade, Guid Guid);

    private const string BakiyeSql = """
        SELECT c.cari_kod AS Kod, c.cari_unvan1 AS Unvan, c.cari_VergiKimlikNo AS VergiNo,
               c.cari_CepTel AS Telefon, c.cari_EMail AS Eposta,
               CAST(ISNULL(SUM(CASE WHEN h.cha_tip = 0 THEN h.cha_meblag END), 0) AS decimal(18,2)) AS Borc,
               CAST(ISNULL(SUM(CASE WHEN h.cha_tip = 1 THEN h.cha_meblag END), 0) AS decimal(18,2)) AS Alacak,
               CAST(ISNULL(SUM(CASE WHEN h.cha_tip = 0 THEN h.cha_meblag ELSE -h.cha_meblag END), 0) AS decimal(18,2)) AS Bakiye
        FROM CARI_HESAPLAR c WITH (NOLOCK)
        LEFT JOIN CARI_HESAP_HAREKETLERI h WITH (NOLOCK)
               ON h.cha_cari_cins = 0 AND h.cha_kod = c.cari_kod AND ISNULL(h.cha_iptal, 0) = 0
        """;

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/cari").RequireAuthorization(Roller.RaporOkuyabilir).WithTags("Cari");

        // Arama: kod ya da ünvan içinde geçen; bakiyesiyle birlikte.
        g.MapGet("/", (string? ara, int? ilk, Db db) =>
        {
            using var conn = db.Mikro();
            var liste = conn.Query<Cari>($"""
                {BakiyeSql}
                WHERE (@ara IS NULL OR c.cari_kod LIKE @desen OR c.cari_unvan1 LIKE @desen)
                GROUP BY c.cari_kod, c.cari_unvan1, c.cari_VergiKimlikNo, c.cari_CepTel, c.cari_EMail
                ORDER BY c.cari_unvan1
                OFFSET 0 ROWS FETCH NEXT @ilk ROWS ONLY;
                """, new { ara, desen = $"%{ara}%", ilk = Math.Clamp(ilk ?? 50, 1, 500) });
            return Results.Ok(liste);
        });

        g.MapGet("/{kod}/ekstre", (string kod, DateOnly? bas, DateOnly? bit, Db db) =>
        {
            var b = bas ?? new DateOnly(DateTime.Today.Year, 1, 1);
            var e = bit ?? DateOnly.FromDateTime(DateTime.Today);
            using var conn = db.Mikro();

            var cari = conn.QuerySingleOrDefault<Cari>($"""
                {BakiyeSql}
                WHERE c.cari_kod = @kod
                GROUP BY c.cari_kod, c.cari_unvan1, c.cari_VergiKimlikNo, c.cari_CepTel, c.cari_EMail;
                """, new { kod });
            if (cari is null) return Results.NotFound($"Cari bulunamadı: {kod}");

            var p = new { kod, bas = b.ToDateTime(TimeOnly.MinValue), bitSonrasi = e.AddDays(1).ToDateTime(TimeOnly.MinValue) };

            var devir = conn.ExecuteScalar<decimal>("""
                SELECT CAST(ISNULL(SUM(CASE WHEN cha_tip = 0 THEN cha_meblag ELSE -cha_meblag END), 0) AS decimal(18,2))
                FROM CARI_HESAP_HAREKETLERI WITH (NOLOCK)
                WHERE cha_cari_cins = 0 AND cha_kod = @kod AND ISNULL(cha_iptal, 0) = 0 AND cha_tarihi < @bas;
                """, p);

            // Çek satırlarında vade ODEME_EMIRLERI'nden gelir (cha_trefno = sck_refno).
            var ham = conn.Query<HamHareket>("""
                SELECT h.cha_tarihi AS Tarih, h.cha_evrakno_seri AS Seri, h.cha_evrakno_sira AS Sira,
                       CAST(h.cha_evrak_tip AS int) AS EvrakTip, CAST(h.cha_cinsi AS int) AS Cinsi, CAST(h.cha_normal_Iade AS int) AS NormalIade,
                       h.cha_belge_no AS BelgeNo, h.cha_aciklama AS Aciklama,
                       CAST(CASE WHEN h.cha_tip = 0 THEN h.cha_meblag ELSE 0 END AS decimal(18,2)) AS Borc,
                       CAST(CASE WHEN h.cha_tip = 1 THEN h.cha_meblag ELSE 0 END AS decimal(18,2)) AS Alacak,
                       o.sck_vade AS Vade, h.cha_Guid AS Guid
                FROM CARI_HESAP_HAREKETLERI h WITH (NOLOCK)
                LEFT JOIN ODEME_EMIRLERI o WITH (NOLOCK) ON h.cha_trefno <> '' AND o.sck_refno = h.cha_trefno
                WHERE h.cha_cari_cins = 0 AND h.cha_kod = @kod AND ISNULL(h.cha_iptal, 0) = 0
                  AND h.cha_tarihi >= @bas AND h.cha_tarihi < @bitSonrasi
                ORDER BY h.cha_tarihi, h.cha_create_date, h.cha_evrakno_sira, h.cha_satir_no;
                """, p);

            var bakiye = devir;
            var hareketler = new List<Hareket>();
            foreach (var h in ham)
            {
                bakiye += h.Borc - h.Alacak;
                var evrakNo = string.IsNullOrWhiteSpace(h.Seri) ? h.Sira.ToString() : $"{h.Seri}-{h.Sira}";
                hareketler.Add(new Hareket(h.Tarih, evrakNo, Mikro.CariEvrakAdi(h.EvrakTip, h.Cinsi, h.NormalIade),
                    h.BelgeNo, h.Aciklama, h.Borc, h.Alacak, bakiye, h.Vade, h.Guid));
            }

            return Results.Ok(new Ekstre(cari, b, e, devir, hareketler.Sum(x => x.Borc),
                hareketler.Sum(x => x.Alacak), bakiye, hareketler));
        });
    }
}
