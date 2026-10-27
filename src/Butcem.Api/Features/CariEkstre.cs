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

    public sealed record EvrakBasi(Guid Guid, DateTime Tarih, string EvrakNo, string EvrakAdi, string? BelgeNo, string? Aciklama,
                                   string CariKod, string CariAdi, decimal Borc, decimal Alacak, DateTime? Vade);

    public sealed record StokSatiri(int SatirNo, string StokKodu, string UrunAdi, string? Birim, decimal Miktar, decimal BirimFiyat,
                                    decimal Tutar, decimal Iskonto, decimal Masraf, decimal Kdv, decimal Net, int DepoNo,
                                    string? Irsaliye);

    public sealed record Belge(string RefNo, string? SeriNo, string? Borclu, string? Banka, DateTime? Vade, decimal Tutar, string Tur);

    public sealed record EvrakDetayi(EvrakBasi Baslik, IReadOnlyList<StokSatiri> Satirlar, IReadOnlyList<Belge> Belgeler,
                                     IReadOnlyList<EvrakBasi> DigerSatirlar);

    private sealed record HamBelge(string RefNo, string? SeriNo, string? Borclu, string? Banka, DateTime? Vade, decimal Tutar, int Tip);

    private sealed record HamBas(Guid Guid, DateTime Tarih, string? Seri, int Sira, int EvrakTip, int Cinsi, int NormalIade,
                                 string? BelgeNo, string? Aciklama, string CariKod, string? CariAdi, decimal Borc, decimal Alacak,
                                 string? TRefNo, DateTime? Vade);

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
        var g = app.MapGroup("/api/cari").RequireYetki(Yetkiler.CariArayabilir).WithTags("Cari");

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
        }).RequireYetki(Yetkiler.CariEkstre);

        // Ekstre satırının içeriği: faturada ürün satırları (sth_fat_uid = cha_Guid), çek / kart tahsilatında belgeler
        // (cha_trefno = sck_refno), ve aynı evrağın diğer cari satırları (ör. bordrodaki diğer çekler, dekont karşılığı).
        g.MapGet("/evrak/{guid:guid}", (Guid guid, Db db) =>
        {
            using var conn = db.Mikro();
            const string basSql = """
                SELECT h.cha_Guid AS Guid, h.cha_tarihi AS Tarih, h.cha_evrakno_seri AS Seri, h.cha_evrakno_sira AS Sira,
                       CAST(h.cha_evrak_tip AS int) AS EvrakTip, CAST(h.cha_cinsi AS int) AS Cinsi, CAST(h.cha_normal_Iade AS int) AS NormalIade,
                       h.cha_belge_no AS BelgeNo, h.cha_aciklama AS Aciklama, h.cha_kod AS CariKod, c.cari_unvan1 AS CariAdi,
                       CAST(CASE WHEN h.cha_tip = 0 THEN h.cha_meblag ELSE 0 END AS decimal(18,2)) AS Borc,
                       CAST(CASE WHEN h.cha_tip = 1 THEN h.cha_meblag ELSE 0 END AS decimal(18,2)) AS Alacak,
                       h.cha_trefno AS TRefNo, o.sck_vade AS Vade
                FROM CARI_HESAP_HAREKETLERI h WITH (NOLOCK)
                LEFT JOIN CARI_HESAPLAR c WITH (NOLOCK) ON h.cha_cari_cins = 0 AND c.cari_kod = h.cha_kod
                LEFT JOIN ODEME_EMIRLERI o WITH (NOLOCK) ON h.cha_trefno <> '' AND o.sck_refno = h.cha_trefno
                """;
            var bas = conn.QuerySingleOrDefault<HamBas>($"{basSql} WHERE h.cha_Guid = @guid", new { guid });
            if (bas is null) return Results.NotFound("Evrak bulunamadı.");

            var satirlar = conn.Query<StokSatiri>("""
                SELECT s.sth_satirno AS SatirNo, s.sth_stok_kod AS StokKodu, ISNULL(st.sto_isim, '') AS UrunAdi, st.sto_birim1_ad AS Birim,
                       CAST(s.sth_miktar AS decimal(18,3)) AS Miktar,
                       CAST(CASE WHEN s.sth_miktar = 0 THEN 0 ELSE s.sth_tutar / s.sth_miktar END AS decimal(18,4)) AS BirimFiyat,
                       CAST(s.sth_tutar AS decimal(18,2)) AS Tutar,
                       CAST(s.sth_iskonto1 + s.sth_iskonto2 + s.sth_iskonto3 + s.sth_iskonto4 + s.sth_iskonto5 + s.sth_iskonto6 AS decimal(18,2)) AS Iskonto,
                       CAST(s.sth_masraf1 + s.sth_masraf2 + s.sth_masraf3 + s.sth_masraf4 AS decimal(18,2)) AS Masraf,
                       CAST(s.sth_vergi AS decimal(18,2)) AS Kdv,
                       CAST(s.sth_tutar - (s.sth_iskonto1 + s.sth_iskonto2 + s.sth_iskonto3 + s.sth_iskonto4 + s.sth_iskonto5 + s.sth_iskonto6)
                            + s.sth_masraf1 + s.sth_masraf2 + s.sth_masraf3 + s.sth_masraf4 + s.sth_vergi AS decimal(18,2)) AS Net,
                       CASE WHEN s.sth_tip = 0 THEN s.sth_giris_depo_no ELSE s.sth_cikis_depo_no END AS DepoNo,
                       CASE WHEN s.sth_evraktip IN (1, 13) THEN CONCAT(NULLIF(s.sth_evrakno_seri, ''), CASE WHEN s.sth_evrakno_seri <> '' THEN '-' END, s.sth_evrakno_sira) END AS Irsaliye
                FROM STOK_HAREKETLERI s WITH (NOLOCK)
                LEFT JOIN STOKLAR st WITH (NOLOCK) ON st.sto_kod = s.sth_stok_kod
                WHERE s.sth_fat_uid = @guid
                ORDER BY s.sth_evrakno_seri, s.sth_evrakno_sira, s.sth_satirno
                """, new { guid }).ToList();

            var belgeler = string.IsNullOrWhiteSpace(bas.TRefNo) ? [] : conn.Query<HamBelge>("""
                SELECT o.sck_refno AS RefNo, o.sck_no AS SeriNo, o.sck_borclu AS Borclu, o.sck_bankano AS Banka, o.sck_vade AS Vade,
                       CAST(o.sck_tutar AS decimal(18,2)) AS Tutar, CAST(o.sck_tip AS int) AS Tip
                FROM ODEME_EMIRLERI o WITH (NOLOCK) WHERE o.sck_refno = @r
                """, new { r = bas.TRefNo })
                .Select(x => new Belge(x.RefNo, x.SeriNo, x.Borclu, x.Banka, x.Vade, x.Tutar, Mikro.CekTipAdi(x.Tip))).ToList();

            // Aynı evrağın diğer cari satırları (fatura dışı evraklar için).
            var diger = satirlar.Count > 0 ? [] : conn.Query<HamBas>($"""
                {basSql}
                WHERE h.cha_evrak_tip = @EvrakTip AND h.cha_evrakno_seri = @seri AND h.cha_evrakno_sira = @Sira
                  AND h.cha_Guid <> @Guid AND ISNULL(h.cha_iptal, 0) = 0
                ORDER BY h.cha_satir_no
                """, new { bas.EvrakTip, seri = bas.Seri ?? "", bas.Sira, bas.Guid }).Select(Bas).ToList();

            return Results.Ok(new EvrakDetayi(Bas(bas), satirlar, belgeler, diger));
        }).RequireYetki(Yetkiler.CariEkstre);
    }

    private static EvrakBasi Bas(HamBas h) => new(h.Guid, h.Tarih,
        string.IsNullOrWhiteSpace(h.Seri) ? h.Sira.ToString() : $"{h.Seri}-{h.Sira}",
        Mikro.CariEvrakAdi(h.EvrakTip, h.Cinsi, h.NormalIade), h.BelgeNo, h.Aciklama, h.CariKod, h.CariAdi ?? "", h.Borc, h.Alacak, h.Vade);
}
