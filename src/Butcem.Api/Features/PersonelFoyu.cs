using System.Data;
using Butcem.Api.Infrastructure;
using Dapper;

namespace Butcem.Api.Features;

/// <summary>
/// Cari personel hareket föyü (Mikro 041436). Personel hesapları (ör. 335.027) cari hareketlerinde cha_cari_cins 1
/// ile tutulur; maaş ödemeleri borç dekontuyla (043100, ör. 05.12.2024 evrak 1445: personel borç, karşı hesap
/// Yapı Kredi 102.002) girilir. Personel bir evrakta karşı hesap (cha_kasa_hizmet 1 / cha_kasa_hizkod) olarak da
/// geçebilir; o zaman yön terstir. Yalnız okur.
/// </summary>
public static class PersonelFoyu
{
    public sealed record Personel(string Kod, string Ad, decimal Bakiye);

    public sealed record Hareket(Guid Guid, DateTime Tarih, string Evrak, string EvrakAdi, string BelgeNo, string KarsiHesap,
                                 string Aciklama, decimal Borc, decimal Alacak, decimal Bakiye);

    public sealed record Foy(Personel Personel, DateOnly Bas, DateOnly Bit, decimal Devir, decimal Borc, decimal Alacak,
                             decimal Bakiye, List<Hareket> Hareketler);

    private sealed record Ham(Guid Guid, DateTime Tarih, int EvrakTip, int Cinsi, int NormalIade, string Seri, int Sira, string BelgeNo,
                              int KarsiCins, string KarsiKod, string Aciklama, decimal Borc, decimal Alacak);

    private const int PersonelCinsi = 1;

    // Personel hesabının taraf olduğu hareketler; borç/alacak personel açısından.
    private const string HareketSql = """
        SELECT h.cha_Guid AS Guid, h.cha_tarihi AS Tarih, CAST(h.cha_evrak_tip AS int) AS EvrakTip, CAST(h.cha_cinsi AS int) AS Cinsi,
               CAST(h.cha_normal_Iade AS int) AS NormalIade, h.cha_evrakno_seri AS Seri, h.cha_evrakno_sira AS Sira, h.cha_belge_no AS BelgeNo,
               CAST(CASE WHEN h.cha_cari_cins = 1 AND h.cha_kod = @kod THEN h.cha_kasa_hizmet ELSE h.cha_cari_cins END AS int) AS KarsiCins,
               CASE WHEN h.cha_cari_cins = 1 AND h.cha_kod = @kod THEN h.cha_kasa_hizkod ELSE h.cha_kod END AS KarsiKod,
               h.cha_aciklama AS Aciklama,
               CAST(CASE WHEN (h.cha_cari_cins = 1 AND h.cha_kod = @kod AND h.cha_tip = 0)
                           OR (h.cha_kasa_hizmet = 1 AND h.cha_kasa_hizkod = @kod AND h.cha_tip = 1) THEN h.cha_meblag ELSE 0 END AS decimal(18,2)) AS Borc,
               CAST(CASE WHEN (h.cha_cari_cins = 1 AND h.cha_kod = @kod AND h.cha_tip = 1)
                           OR (h.cha_kasa_hizmet = 1 AND h.cha_kasa_hizkod = @kod AND h.cha_tip = 0) THEN h.cha_meblag ELSE 0 END AS decimal(18,2)) AS Alacak
        FROM CARI_HESAP_HAREKETLERI h WITH (NOLOCK)
        WHERE ISNULL(h.cha_iptal, 0) = 0
          AND ((h.cha_cari_cins = 1 AND h.cha_kod = @kod) OR (h.cha_kasa_hizmet = 1 AND h.cha_kasa_hizkod = @kod))
        """;

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/personel").RequireYetki(Yetkiler.CariEkstre).WithTags("Personel");

        // Personel listesi: hareketi olan personel hesapları, bakiyeleriyle (borç - alacak).
        g.MapGet("/", (string? ara, Db db) =>
        {
            using var conn = db.Mikro();
            var adlar = Adlar(conn);
            var liste = conn.Query<(string Kod, decimal Bakiye)>("""
                SELECT cha_kod, CAST(SUM(CASE WHEN cha_tip = 0 THEN cha_meblag ELSE -cha_meblag END) AS decimal(18,2))
                FROM CARI_HESAP_HAREKETLERI WITH (NOLOCK)
                WHERE cha_cari_cins = 1 AND ISNULL(cha_iptal, 0) = 0 AND cha_kod <> ''
                GROUP BY cha_kod
                """).ToDictionary(x => x.Kod, x => x.Bakiye);
            foreach (var k in adlar.Keys) liste.TryAdd(k, 0);
            var q = ara?.Trim() ?? "";
            return Results.Ok(liste
                .Select(x => new Personel(x.Key, adlar.GetValueOrDefault(x.Key, ""), x.Value))
                .Where(p => q == "" || p.Kod.Contains(q, StringComparison.OrdinalIgnoreCase) || p.Ad.Contains(q, StringComparison.CurrentCultureIgnoreCase))
                .OrderBy(p => p.Kod));
        });

        g.MapGet("/{kod}/foy", (string kod, DateOnly? bas, DateOnly? bit, Db db) =>
        {
            var b = bas ?? new DateOnly(DateTime.Today.Year, 1, 1);
            var e = bit ?? DateOnly.FromDateTime(DateTime.Today);
            if (e < b) throw new IslemHatasi("Bitiş tarihi başlangıçtan önce olamaz.");
            using var conn = db.Mikro();
            var p = new { kod = kod.Trim(), b = b.ToDateTime(TimeOnly.MinValue), e = e.ToDateTime(TimeOnly.MinValue) };
            var devir = conn.ExecuteScalar<decimal?>($"SELECT SUM(Borc - Alacak) FROM ({HareketSql} AND h.cha_tarihi < @b) x", p) ?? 0m;
            var ham = conn.Query<Ham>($"{HareketSql} AND h.cha_tarihi >= @b AND h.cha_tarihi <= @e ORDER BY h.cha_tarihi, h.cha_create_date, h.cha_satir_no", p).ToList();
            var karsiAdlari = KarsiAdlari(conn, ham);
            var bakiye = devir;
            var hareketler = ham.Select(x =>
            {
                bakiye += x.Borc - x.Alacak;
                var ad = karsiAdlari.GetValueOrDefault((x.KarsiCins, x.KarsiKod), "");
                return new Hareket(x.Guid, x.Tarih, string.IsNullOrWhiteSpace(x.Seri) ? x.Sira.ToString() : $"{x.Seri}-{x.Sira}",
                    Mikro.CariEvrakAdi(x.EvrakTip, x.Cinsi, x.NormalIade), x.BelgeNo,
                    string.IsNullOrWhiteSpace(ad) ? x.KarsiKod : $"{x.KarsiKod} {ad}", x.Aciklama, x.Borc, x.Alacak, bakiye);
            }).ToList();
            var personel = new Personel(p.kod, Adlar(conn).GetValueOrDefault(p.kod, ""), bakiye);
            return Results.Ok(new Foy(personel, b, e, devir, hareketler.Sum(x => x.Borc), hareketler.Sum(x => x.Alacak), bakiye, hareketler));
        });
    }

    /// <summary>Personel adları CARI_PERSONEL_TANIMLARI'ndan (kolonlar kurulumda kontrol edilir; tablo yoksa ad boş kalır).</summary>
    private static Dictionary<string, string> Adlar(IDbConnection conn)
    {
        var kolonlar = conn.Query<string>("SELECT name FROM sys.columns WHERE object_id = OBJECT_ID('CARI_PERSONEL_TANIMLARI')").ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!kolonlar.Contains("cari_per_kod")) return [];
        var ad = (kolonlar.Contains("cari_per_adi"), kolonlar.Contains("cari_per_soyadi")) switch
        {
            (true, true) => "LTRIM(RTRIM(ISNULL(cari_per_adi, '') + ' ' + ISNULL(cari_per_soyadi, '')))",
            (true, false) => "ISNULL(cari_per_adi, '')",
            _ => "''",
        };
        return conn.Query<(string Kod, string Ad)>($"SELECT cari_per_kod, {ad} FROM CARI_PERSONEL_TANIMLARI WITH (NOLOCK)")
                   .GroupBy(x => x.Kod).ToDictionary(x => x.Key, x => x.First().Ad);
    }

    private static Dictionary<(int, string), string> KarsiAdlari(IDbConnection conn, List<Ham> ham)
    {
        var d = new Dictionary<(int, string), string>();
        void Ekle(int cins, string sql)
        {
            var kodlar = ham.Where(h => h.KarsiCins == cins && h.KarsiKod != "").Select(h => h.KarsiKod).Distinct().ToArray();
            if (kodlar.Length == 0) return;
            foreach (var (k, a) in conn.Query<(string, string)>(sql, new { kodlar })) d[(cins, k)] = a;
        }
        Ekle(0, "SELECT cari_kod, cari_unvan1 FROM CARI_HESAPLAR WITH (NOLOCK) WHERE cari_kod IN @kodlar");
        Ekle(2, "SELECT ban_kod, ban_ismi FROM BANKALAR WITH (NOLOCK) WHERE ban_kod IN @kodlar");
        Ekle(4, "SELECT kas_kod, kas_isim FROM KASALAR WITH (NOLOCK) WHERE kas_kod IN @kodlar");
        if (ham.Any(h => h.KarsiCins == PersonelCinsi))
            foreach (var (k, a) in Adlar(conn)) d[(PersonelCinsi, k)] = a;
        return d;
    }
}
