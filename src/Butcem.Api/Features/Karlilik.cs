using System.Security.Claims;
using Butcem.Api.Infrastructure;
using Dapper;

namespace Butcem.Api.Features;

/// <summary>
/// Karlılık analizi. Satış: satış raporuyla aynı satırlar (satış faturası, KDV hariç net, iadeler düşülür).
/// Maliyet: Mikro'da maliyetlendirme çalıştırılmadığı için satış satırlarında maliyet boş (sth_maliyet_ana = 0);
/// her ürünün satış günündeki son alış fiyatı kullanılır (alış faturası / irsaliyesi, iskontolar düşülmüş, KDV hariç;
/// sevkteki maliyetle aynı kural). Alış kaydı olmayan ya da alış birim fiyatı satış fiyatıyla tutarsız olan
/// (koli / kasa ile girilmiş alış gibi) satırlar kâra katılmaz, "kontrol edilecek" listesinde ayrıca gösterilir.
/// </summary>
public static class Karlilik
{
    public enum Kirilim { Gun, Depo, Anagrup, Reyon, Marka, Urun }

    public sealed record Satir(string Anahtar, string Ad, decimal Miktar, decimal NetSatis, decimal Maliyet, decimal Kar,
                               decimal? KarOrani, decimal HaricSatis);

    public sealed record Kontrol(string StokKodu, string UrunAdi, decimal Miktar, decimal NetSatis, decimal? BirimSatis,
                                 decimal? BirimMaliyet, string Sebep);

    public sealed record Sonuc(DateOnly Baslangic, DateOnly Bitis, string Kirilim, decimal NetSatis, decimal Maliyet, decimal Kar,
                               decimal? KarOrani, decimal HaricSatis, IReadOnlyList<Satir> Satirlar, IReadOnlyList<Kontrol> Kontroller);

    /// <summary>Alış birim fiyatı satış birim fiyatının bu katından büyükse ya da bu oranından küçükse güvenilmez sayılır.</summary>
    public const decimal UstSinir = 3m, AltSinir = 0.05m;

    /// <summary>1: kâra katılır, 2: alış kaydı yok, 3: alış fiyatı satıştan çok yüksek, 4: çok düşük.</summary>
    public static int Durum(decimal miktar, decimal net, decimal? birimMaliyet)
    {
        if (birimMaliyet is null) return 2;
        if (miktar == 0 || net == 0) return 1;
        var birimSatis = Math.Abs(net / miktar);
        if (birimMaliyet > birimSatis * UstSinir) return 3;
        if (birimMaliyet < birimSatis * AltSinir) return 4;
        return 1;
    }

    private sealed record Ham(string Kod, string Anahtar, string Ad, decimal Miktar, decimal Net, decimal? BirimMaliyet);

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/karlilik", (DateOnly bas, DateOnly bit, Kirilim? kirilim, SatisRaporu.Tur? tur, int? depo, int? ilk,
                                     ClaimsPrincipal user, Db db) =>
        {
            if (bit < bas) return Results.BadRequest("Bitiş tarihi başlangıçtan önce olamaz.");
            if (bit.DayNumber - bas.DayNumber > 400) return Results.BadRequest("En fazla 400 günlük aralık sorgulanabilir.");
            if (user.MagazaKullanicisi()) depo = user.Depo();

            var k = kirilim ?? Kirilim.Anagrup;
            var (anahtar, ad, join) = k switch
            {
                Kirilim.Gun => ("CONVERT(varchar(10), d.tarih, 23)", "CONVERT(varchar(10), d.tarih, 104)", ""),
                Kirilim.Depo => ("CAST(d.depo AS varchar(10))", "ISNULL(dp.dep_adi, '')", "LEFT JOIN DEPOLAR dp WITH (NOLOCK) ON dp.dep_no = d.depo"),
                Kirilim.Anagrup => ("ISNULL(st.sto_anagrup_kod, '')", "ISNULL(g.san_isim, '(grupsuz)')",
                                    "LEFT JOIN STOK_ANA_GRUPLARI g WITH (NOLOCK) ON g.san_kod = st.sto_anagrup_kod"),
                Kirilim.Reyon => ("ISNULL(st.sto_reyon_kodu, '')", "ISNULL(r.ryn_ismi, '(reyonsuz)')",
                                  "LEFT JOIN STOK_REYONLARI r WITH (NOLOCK) ON r.ryn_kod = st.sto_reyon_kodu"),
                Kirilim.Marka => ("ISNULL(st.sto_marka_kodu, '')", "ISNULL(m.mrk_ismi, '(markasız)')",
                                  "LEFT JOIN STOK_MARKALARI m WITH (NOLOCK) ON m.mrk_kod = st.sto_marka_kodu"),
                Kirilim.Urun => ("d.kod", "ISNULL(st.sto_isim, d.kod)", ""),
                _ => throw new ArgumentOutOfRangeException(nameof(kirilim)),
            };
            var cinsFiltre = (tur ?? SatisRaporu.Tur.Hepsi) switch
            {
                SatisRaporu.Tur.Perakende => $"AND h.sth_cins = {Mikro.StokHarCins.Perakende}",
                SatisRaporu.Tur.Toptan => $"AND h.sth_cins = {Mikro.StokHarCins.Toptan}",
                _ => "",
            };

            // Son alış fiyatı pencere fonksiyonuyla: satış ve alış günleri ürün başına tarih sırasına dizilir,
            // her satış günü kendinden önceki (aynı gün dahil) son alış grubunun fiyatını alır.
            var sql = $"""
                WITH s AS (
                    SELECT h.sth_stok_kod AS kod, h.sth_tarih AS tarih, h.sth_cikis_depo_no AS depo,
                           SUM(CASE WHEN h.sth_normal_iade = 1 THEN -1 ELSE 1 END * h.sth_miktar) AS miktar,
                           SUM(CASE WHEN h.sth_normal_iade = 1 THEN -1 ELSE 1 END * (h.sth_tutar - (h.sth_iskonto1 + h.sth_iskonto2
                               + h.sth_iskonto3 + h.sth_iskonto4 + h.sth_iskonto5 + h.sth_iskonto6))) AS net
                    FROM STOK_HAREKETLERI h WITH (NOLOCK)
                    WHERE h.sth_tarih >= @bas AND h.sth_tarih < @bitSonrasi
                      AND h.sth_tip = {Mikro.StokHarTip.Cikis} AND h.sth_evraktip = {Mikro.StokEvrakTip.CikisFaturasi}
                      AND ISNULL(h.sth_iptal, 0) = 0 AND (@depo IS NULL OR h.sth_cikis_depo_no = @depo)
                      {cinsFiltre}
                    GROUP BY h.sth_stok_kod, h.sth_tarih, h.sth_cikis_depo_no
                ),
                a AS (
                    SELECT kod, tarih, f FROM (
                        SELECT x.sth_stok_kod AS kod, x.sth_tarih AS tarih,
                               (x.sth_tutar - x.sth_iskonto1 - x.sth_iskonto2 - x.sth_iskonto3 - x.sth_iskonto4 - x.sth_iskonto5
                                - x.sth_iskonto6) / x.sth_miktar AS f,
                               ROW_NUMBER() OVER (PARTITION BY x.sth_stok_kod, x.sth_tarih
                                                  ORDER BY x.sth_create_date DESC, x.sth_evrakno_sira DESC, x.sth_satirno DESC) AS r
                        FROM STOK_HAREKETLERI x WITH (NOLOCK)
                        WHERE x.sth_tip = 0 AND x.sth_evraktip IN (3, 13) AND x.sth_normal_iade = 0 AND x.sth_miktar > 0
                          AND x.sth_tarih < @bitSonrasi AND x.sth_stok_kod IN (SELECT kod FROM s)
                    ) y WHERE r = 1
                ),
                b AS (
                    SELECT kod, tarih, depo, miktar, net, CAST(NULL AS float) AS f, 1 AS satis FROM s
                    UNION ALL SELECT kod, tarih, NULL, NULL, NULL, f, 0 FROM a
                ),
                c AS (SELECT *, COUNT(f) OVER (PARTITION BY kod ORDER BY tarih, satis ROWS UNBOUNDED PRECEDING) AS grp FROM b),
                d AS (SELECT kod, tarih, depo, miktar, net, MAX(f) OVER (PARTITION BY kod, grp) AS bm, satis FROM c)
                SELECT d.kod AS Kod, {anahtar} AS Anahtar, {ad} AS Ad,
                       CAST(d.miktar AS decimal(18,3)) AS Miktar, CAST(d.net AS decimal(18,2)) AS Net, CAST(d.bm AS decimal(18,4)) AS BirimMaliyet
                FROM d
                LEFT JOIN STOKLAR st WITH (NOLOCK) ON st.sto_kod = d.kod
                {join}
                WHERE d.satis = 1
                """;

            using var conn = db.Mikro();
            var ham = conn.Query<Ham>(sql, new
            {
                bas = bas.ToDateTime(TimeOnly.MinValue),
                bitSonrasi = bit.AddDays(1).ToDateTime(TimeOnly.MinValue),
                depo,
            }, commandTimeout: 300).ToList();

            var satirlar = ham.GroupBy(h => h.Anahtar).Select(g =>
            {
                var dahil = g.Where(h => Durum(h.Miktar, h.Net, h.BirimMaliyet) == 1).ToList();
                var net = dahil.Sum(h => h.Net);
                var maliyet = Math.Round(dahil.Sum(h => h.Miktar * h.BirimMaliyet!.Value), 2);
                return new Satir(g.Key, g.First().Ad, g.Sum(h => h.Miktar), net, maliyet, net - maliyet,
                                 Oran(net - maliyet, net), g.Sum(h => h.Net) - net);
            });
            satirlar = k switch
            {
                Kirilim.Gun => satirlar.OrderBy(s => s.Anahtar),
                Kirilim.Depo => satirlar.OrderBy(s => int.TryParse(s.Anahtar, out var n) ? n : 0),
                _ => satirlar.OrderByDescending(s => s.Kar),
            };
            var liste = satirlar.ToList();
            if (k == Kirilim.Urun) liste = liste.Take(Math.Clamp(ilk ?? 500, 1, 5000)).ToList();

            // Kâra katılmayan satışlar, ürün başına (en büyük satış tutarı önce).
            var kontroller = ham.Where(h => Durum(h.Miktar, h.Net, h.BirimMaliyet) != 1)
                .GroupBy(h => h.Kod)
                .Select(g =>
                {
                    var miktar = g.Sum(h => h.Miktar);
                    var net = g.Sum(h => h.Net);
                    var bm = g.Max(h => h.BirimMaliyet);
                    var durum = g.Select(h => Durum(h.Miktar, h.Net, h.BirimMaliyet)).Max();
                    return new Kontrol(g.Key, k == Kirilim.Urun ? g.First().Ad : "", miktar, net,
                        miktar == 0 ? null : Math.Round(net / miktar, 2), bm is null ? null : Math.Round(bm.Value, 2),
                        durum switch
                        {
                            2 => "Alış kaydı yok",
                            3 => "Alış fiyatı satış fiyatının 3 katından fazla (koli / kasa ile girilmiş olabilir)",
                            _ => "Alış fiyatı satış fiyatına göre çok düşük",
                        });
                })
                .OrderByDescending(x => Math.Abs(x.NetSatis)).Take(100).ToList();
            if (k != Kirilim.Urun && kontroller.Count > 0)
            {
                var adlar = conn.Query<(string Kod, string Ad)>("SELECT sto_kod, sto_isim FROM STOKLAR WITH (NOLOCK) WHERE sto_kod IN @kodlar",
                    new { kodlar = kontroller.Select(x => x.StokKodu).ToList() }).ToDictionary(x => x.Kod, x => x.Ad);
                kontroller = kontroller.Select(x => x with { UrunAdi = adlar.GetValueOrDefault(x.StokKodu, x.StokKodu) }).ToList();
            }

            var tNet = ham.Where(h => Durum(h.Miktar, h.Net, h.BirimMaliyet) == 1).Sum(h => h.Net);
            var tMal = Math.Round(ham.Where(h => Durum(h.Miktar, h.Net, h.BirimMaliyet) == 1).Sum(h => h.Miktar * h.BirimMaliyet!.Value), 2);
            return Results.Ok(new Sonuc(bas, bit, k.ToString().ToLowerInvariant(), tNet, tMal, tNet - tMal, Oran(tNet - tMal, tNet),
                ham.Sum(h => h.Net) - tNet, liste, kontroller));
        })
        .RequireYetki(Yetkiler.Karlilik)
        .WithTags("Raporlar");
    }

    private static decimal? Oran(decimal kar, decimal net) => net == 0 ? null : Math.Round(kar / net * 100, 1);
}
