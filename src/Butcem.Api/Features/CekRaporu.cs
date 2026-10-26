using Butcem.Api.Infrastructure;
using Dapper;

namespace Butcem.Api.Features;

/// <summary>
/// Çek/senet raporu (madde 6). Kaynak: ODEME_EMIRLERI (sck_). Hareket geçmişi: CARI_HESAP_HAREKETLERI.cha_trefno = sck_refno.
/// Varsayılan olarak müşteri çekleri (0) ve firma çekleri (2); kredi kartı slipleri (6) hariç.
/// </summary>
public static class CekRaporu
{
    public sealed record Cek(Guid Guid, int Tip, string TipAdi, string RefNo, string? SeriNo, string? Borclu,
                             string? Banka, DateTime Vade, decimal Tutar, decimal Odenen, int Pozisyon,
                             string PozisyonAdi, string? SahipKod, string? SahipAdi, string? NeredeKod,
                             string? NeredeAdi, DateTime? IlkHareket, DateTime? SonHareket, int VadeyeKalanGun);

    public sealed record Ozet(int Adet, decimal Toplam, decimal Vadesi30Gun, decimal VadesiGecmis);

    public sealed record Hareket(DateTime Tarih, string EvrakAdi, string? EvrakNo, string? CariKod, string? CariAdi,
                                 int Tip, decimal Tutar, string PozisyonAdi);

    private sealed record HamCek(Guid Guid, int Tip, string RefNo, string? SeriNo, string? Borclu, string? Banka,
                                 DateTime Vade, decimal Tutar, decimal Odenen, int Pozisyon, string? SahipKod,
                                 string? SahipAdi, int NeredeCins, string? NeredeKod, string? NeredeAdi,
                                 DateTime? IlkHareket, DateTime? SonHareket);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/cek").RequireYetki(Yetkiler.CekRaporu).WithTags("Çek");

        g.MapGet("/", (string? tipler, string? durum, DateOnly? vadeBas, DateOnly? vadeBit, string? cari, string? ara, Db db) =>
        {
            var tipListesi = (tipler ?? "0,2").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(int.Parse).ToArray();

            // durum: portfoy (0), cikan (ciro/verildi, 1), tahsil (2), hepsi
            int[]? pozlar = durum switch
            {
                null or "" or "hepsi" => null,
                "portfoy" => [0],
                "cikan" => [1],
                "tahsil" => [2, 9, 10],
                _ => throw new BadHttpRequestException("durum: portfoy | cikan | tahsil | hepsi"),
            };

            using var conn = db.Mikro();
            var ham = conn.Query<HamCek>($"""
                SELECT o.sck_Guid AS Guid, CAST(o.sck_tip AS int) AS Tip, o.sck_refno AS RefNo, o.sck_no AS SeriNo,
                       o.sck_borclu AS Borclu, o.sck_bankano AS Banka, o.sck_vade AS Vade,
                       CAST(o.sck_tutar AS decimal(18,2)) AS Tutar, CAST(o.sck_odenen AS decimal(18,2)) AS Odenen,
                       CAST(o.sck_sonpoz AS int) AS Pozisyon,
                       o.sck_sahip_cari_kodu AS SahipKod, cs.cari_unvan1 AS SahipAdi,
                       CAST(o.sck_nerede_cari_cins AS int) AS NeredeCins, o.sck_nerede_cari_kodu AS NeredeKod,
                       COALESCE(cn.cari_unvan1, k.kas_isim, b.ban_ismi) AS NeredeAdi,
                       o.sck_ilk_hareket_tarihi AS IlkHareket, o.sck_son_hareket_tarihi AS SonHareket
                FROM ODEME_EMIRLERI o WITH (NOLOCK)
                LEFT JOIN CARI_HESAPLAR cs WITH (NOLOCK) ON cs.cari_kod = o.sck_sahip_cari_kodu
                LEFT JOIN CARI_HESAPLAR cn WITH (NOLOCK) ON o.sck_nerede_cari_cins = 0 AND cn.cari_kod = o.sck_nerede_cari_kodu
                LEFT JOIN KASALAR k WITH (NOLOCK) ON o.sck_nerede_cari_cins = 4 AND k.kas_kod = o.sck_nerede_cari_kodu
                LEFT JOIN BANKALAR b WITH (NOLOCK) ON o.sck_nerede_cari_cins = 2 AND b.ban_kod = o.sck_nerede_cari_kodu
                WHERE ISNULL(o.sck_iptal, 0) = 0
                  AND o.sck_tip IN @tipListesi
                  {(pozlar is null ? "" : "AND o.sck_sonpoz IN @pozlar")}
                  AND (@vadeBas IS NULL OR o.sck_vade >= @vadeBas)
                  AND (@vadeBit IS NULL OR o.sck_vade < @vadeBitSonrasi)
                  AND (@cari IS NULL OR o.sck_sahip_cari_kodu = @cari OR o.sck_nerede_cari_kodu = @cari)
                  AND (@ara IS NULL OR o.sck_refno LIKE @desen OR o.sck_no LIKE @desen OR o.sck_borclu LIKE @desen)
                ORDER BY o.sck_vade, o.sck_refno;
                """, new
            {
                tipListesi, pozlar, cari, ara, desen = $"%{ara}%",
                vadeBas = vadeBas?.ToDateTime(TimeOnly.MinValue),
                vadeBit = vadeBit?.ToDateTime(TimeOnly.MinValue),
                vadeBitSonrasi = vadeBit?.AddDays(1).ToDateTime(TimeOnly.MinValue),
            });

            var bugun = DateTime.Today;
            var liste = ham.Select(c => new Cek(c.Guid, c.Tip, Mikro.CekTipAdi(c.Tip), c.RefNo, c.SeriNo, c.Borclu,
                c.Banka, c.Vade, c.Tutar, c.Odenen, c.Pozisyon, Mikro.CekPozisyonAdi(c.Tip, c.Pozisyon),
                c.SahipKod, c.SahipAdi, c.NeredeKod, c.NeredeAdi, c.IlkHareket, c.SonHareket,
                (c.Vade.Date - bugun).Days)).ToList();

            var ozet = new Ozet(liste.Count, liste.Sum(c => c.Tutar),
                liste.Where(c => c.VadeyeKalanGun is >= 0 and <= 30).Sum(c => c.Tutar),
                liste.Where(c => c.VadeyeKalanGun < 0 && c.Pozisyon == 0).Sum(c => c.Tutar));

            return Results.Ok(new { ozet, cekler = liste });
        });

        g.MapGet("/{refno}/hareketler", (string refno, Db db) =>
        {
            using var conn = db.Mikro();
            var liste = conn.Query<(DateTime Tarih, int EvrakTip, int Cinsi, int NormalIade, string? Seri, int Sira,
                                    string? Kod, string? Unvan, int Tip, decimal Tutar, int Poz, int SckTip)>("""
                SELECT h.cha_tarihi, CAST(h.cha_evrak_tip AS int), CAST(h.cha_cinsi AS int), CAST(h.cha_normal_Iade AS int), h.cha_evrakno_seri, h.cha_evrakno_sira,
                       h.cha_kod, c.cari_unvan1, CAST(h.cha_tip AS int), CAST(h.cha_meblag AS decimal(18,2)), CAST(h.cha_sntck_poz AS int), CAST(o.sck_tip AS int)
                FROM CARI_HESAP_HAREKETLERI h WITH (NOLOCK)
                JOIN ODEME_EMIRLERI o WITH (NOLOCK) ON o.sck_refno = h.cha_trefno
                LEFT JOIN CARI_HESAPLAR c WITH (NOLOCK) ON h.cha_cari_cins = 0 AND c.cari_kod = h.cha_kod
                WHERE h.cha_trefno = @refno AND ISNULL(h.cha_iptal, 0) = 0
                ORDER BY h.cha_tarihi, h.cha_create_date;
                """, new { refno })
                .Select(r => new Hareket(r.Tarih, Mikro.CariEvrakAdi(r.EvrakTip, r.Cinsi, r.NormalIade),
                    string.IsNullOrWhiteSpace(r.Seri) ? r.Sira.ToString() : $"{r.Seri}-{r.Sira}",
                    r.Kod, r.Unvan, r.Tip, r.Tutar, Mikro.CekPozisyonAdi(r.SckTip, r.Poz)));
            return Results.Ok(liste);
        });
    }
}
