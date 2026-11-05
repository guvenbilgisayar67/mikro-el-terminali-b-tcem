using System.Security.Claims;
using Butcem.Api.Infrastructure;
using Dapper;

namespace Butcem.Api.Features;

/// <summary>
/// Doğrudan (irsaliyesiz) alış faturası (Mikro 062205), alış faturaları yönetimi listesi (Mikro 028012) ve
/// evrak ekranlarının satır girişi için ürün okuma / arama.
///
/// Alış faturası satış faturasıyla aynı yapıdadır: stok satırları (evrak tipi 3, giriş faturası, stoğa giriş)
/// ve tek cari satırı (evrak tipi 0, cari alacak) aynı sıra numarasını taşır; stok satırlarının sth_fat_uid'i
/// cari satırının cha_Guid'ini gösterir. Maliyet alanları alış irsaliyesindeki gibi doldurulur.
/// </summary>
public static partial class Faturalar
{
    public sealed record AlisFaturaIstek(string CariKod, int DepoNo, DateTime? Tarih, string? BelgeNo, DateTime? BelgeTarih,
                                         List<SatirIstek> Satirlar);

    /// <summary>Alış faturaları listesi satırı. Tutarlar: brüt ara toplam, iskonto, KDV, genel toplam.</summary>
    public sealed record AlisFaturaOzet(Guid Guid, DateTime Tarih, string Seri, int Sira, string BelgeNo, DateTime BelgeTarih,
                                        string CariKod, string CariAdi, bool Iade, int SatirSayisi, int? DepoNo,
                                        decimal AraToplam, decimal Iskonto, decimal Kdv, decimal Toplam);

    public sealed record AlisFaturaSatiri(int SatirNo, string StokKodu, string UrunAdi, string Birim, decimal Miktar,
                                          decimal BirimFiyat, decimal Tutar, decimal Iskonto, decimal Kdv);

    /// <summary>Satır girişinde okutulan / yazılan ürün. Önerilen alış fiyatı: son alış (KDV hariç, iskontolu).</summary>
    public sealed record UrunBilgi(string Kod, string Ad, string Birim, decimal SonAlis, decimal KdvOrani);

    public sealed record UrunAramaSonucu(string Kod, string Ad, string Birim);

    private static void MapAlis(RouteGroupBuilder g)
    {
        // Satır girişi: stok kodu ya da barkoddan ürün, son alış fiyatı ve KDV oranı.
        g.MapGet("/urun", (string k, DateTime? tarih, Db db, IConfiguration config) =>
        {
            using var conn = db.Mikro();
            var stok = MikroEvrak.Stok(conn, null, k);
            var fiyat = MikroEvrak.SonAlisFiyati(conn, null, stok.Kod, (tarih ?? DateTime.Today).Date);
            return Results.Ok(new UrunBilgi(stok.Kod, stok.Ad, stok.Birim ?? "", Math.Round(fiyat, 4), KdvOrani(config, stok.VergiPntr)));
        });

        // Satır girişi: ürün adının ya da kodun bir parçasıyla arama (en fazla 20, pasif ürünler hariç).
        g.MapGet("/urun-ara", (string? q, Db db) =>
        {
            var ara = q?.Trim() ?? "";
            if (ara.Length < 2) return Results.Ok(Array.Empty<UrunAramaSonucu>());
            using var conn = db.Mikro();
            return Results.Ok(conn.Query<UrunAramaSonucu>("""
                SELECT TOP 20 s.sto_kod AS Kod, s.sto_isim AS Ad, ISNULL(s.sto_birim1_ad, '') AS Birim
                FROM STOKLAR s WITH (NOLOCK)
                WHERE ISNULL(s.sto_pasif_fl, 0) = 0 AND (s.sto_kod LIKE @bas OR s.sto_isim LIKE @desen)
                ORDER BY CASE WHEN s.sto_kod LIKE @bas THEN 0 ELSE 1 END, s.sto_isim
                """, new { bas = ara + "%", desen = "%" + ara + "%" }));
        });

        g.MapPost("/alis", (AlisFaturaIstek istek, ClaimsPrincipal user, MikroYazici yazici, IConfiguration config, IslemLogu log) =>
        {
            user.DepoYetkisi(istek.DepoNo);
            SatirKontrol(istek.Satirlar, istek.Tarih);
            if (string.IsNullOrWhiteSpace(istek.BelgeNo)) throw new IslemHatasi("Fatura numarasını (belge no) yazın.");
            var sonuc = AlisFaturasi(yazici, config, istek);
            log.Yaz(user.Id(), "alis_faturasi", new { istek, sonuc });
            return Results.Ok(sonuc);
        }).RequireYetki(Yetkiler.AlisFatura);

        // Alış faturaları yönetimi: dönemdeki alış ve alış iade faturaları (irsaliyeden faturalaştırılanlar dahil).
        g.MapGet("/alis-faturalari", (DateOnly? bas, DateOnly? bit, string? cari, ClaimsPrincipal user, Db db) =>
        {
            var b = (bas ?? DateOnly.FromDateTime(DateTime.Today.AddDays(-30))).ToDateTime(TimeOnly.MinValue);
            var e = (bit ?? DateOnly.FromDateTime(DateTime.Today)).ToDateTime(TimeOnly.MinValue);
            if (e < b) throw new IslemHatasi("Bitiş tarihi başlangıçtan önce olamaz.");
            if ((e - b).TotalDays > 366) throw new IslemHatasi("En fazla bir yıllık dönem seçin.");
            using var conn = db.Mikro();
            return Results.Ok(conn.Query<AlisFaturaOzet>("""
                SELECT h.cha_Guid AS Guid, h.cha_tarihi AS Tarih, h.cha_evrakno_seri AS Seri, h.cha_evrakno_sira AS Sira,
                       h.cha_belge_no AS BelgeNo, h.cha_belge_tarih AS BelgeTarih, h.cha_kod AS CariKod,
                       ISNULL(c.cari_unvan1, '') AS CariAdi, CAST(CASE WHEN h.cha_normal_Iade = 1 THEN 1 ELSE 0 END AS bit) AS Iade,
                       ISNULL(s.Satir, 0) AS SatirSayisi, s.DepoNo,
                       CAST(h.cha_aratoplam AS decimal(18,2)) AS AraToplam,
                       CAST(h.cha_ft_iskonto1 + h.cha_ft_iskonto2 + h.cha_ft_iskonto3 + h.cha_ft_iskonto4 + h.cha_ft_iskonto5
                            + h.cha_ft_iskonto6 AS decimal(18,2)) AS Iskonto,
                       CAST(h.cha_meblag - h.cha_aratoplam + h.cha_ft_iskonto1 + h.cha_ft_iskonto2 + h.cha_ft_iskonto3
                            + h.cha_ft_iskonto4 + h.cha_ft_iskonto5 + h.cha_ft_iskonto6 AS decimal(18,2)) AS Kdv,
                       CAST(h.cha_meblag AS decimal(18,2)) AS Toplam
                FROM CARI_HESAP_HAREKETLERI h WITH (NOLOCK)
                LEFT JOIN CARI_HESAPLAR c WITH (NOLOCK) ON c.cari_kod = h.cha_kod
                OUTER APPLY (SELECT COUNT(*) AS Satir, MIN(x.sth_giris_depo_no) AS DepoNo
                             FROM STOK_HAREKETLERI x WITH (NOLOCK) WHERE x.sth_fat_uid = h.cha_Guid) s
                WHERE h.cha_evrak_tip = 0 AND h.cha_cari_cins = 0 AND ISNULL(h.cha_iptal, 0) = 0
                  AND h.cha_tarihi >= @b AND h.cha_tarihi <= @e
                  AND (@cari IS NULL OR h.cha_kod = @cari)
                  AND (@depo IS NULL OR s.DepoNo = @depo)
                ORDER BY h.cha_tarihi DESC, h.cha_evrakno_sira DESC
                """, new { b, e, cari = string.IsNullOrWhiteSpace(cari) ? null : cari.Trim(), depo = user.MagazaKullanicisi() ? user.Depo() : (int?)null }));
        }).RequireYetki(Yetkiler.AlisFatura);

        // Bir alış faturasının ürün satırları (listede satıra tıklanınca).
        g.MapGet("/alis-faturalari/{guid:guid}", (Guid guid, Db db) =>
        {
            using var conn = db.Mikro();
            return Results.Ok(conn.Query<AlisFaturaSatiri>("""
                SELECT s.sth_satirno AS SatirNo, s.sth_stok_kod AS StokKodu, ISNULL(st.sto_isim, '') AS UrunAdi,
                       ISNULL(st.sto_birim1_ad, '') AS Birim, CAST(s.sth_miktar AS decimal(18,3)) AS Miktar,
                       CAST(CASE WHEN s.sth_miktar = 0 THEN 0 ELSE s.sth_tutar / s.sth_miktar END AS decimal(18,4)) AS BirimFiyat,
                       CAST(s.sth_tutar AS decimal(18,2)) AS Tutar,
                       CAST(s.sth_iskonto1 + s.sth_iskonto2 + s.sth_iskonto3 + s.sth_iskonto4 + s.sth_iskonto5 + s.sth_iskonto6 AS decimal(18,2)) AS Iskonto,
                       CAST(s.sth_vergi AS decimal(18,2)) AS Kdv
                FROM STOK_HAREKETLERI s WITH (NOLOCK)
                LEFT JOIN STOKLAR st WITH (NOLOCK) ON st.sto_kod = s.sth_stok_kod
                WHERE s.sth_fat_uid = @guid
                ORDER BY s.sth_evrakno_seri, s.sth_evrakno_sira, s.sth_satirno
                """, new { guid }));
        }).RequireYetki(Yetkiler.AlisFatura);
    }

    public static FaturaSonuc AlisFaturasi(MikroYazici yazici, IConfiguration config, AlisFaturaIstek istek) => yazici.Calistir((conn, tx) =>
    {
        var tarih = (istek.Tarih ?? DateTime.Today).Date;
        var belgeTarih = (istek.BelgeTarih ?? tarih).Date;
        if (belgeTarih > DateTime.Today) throw new IslemHatasi("Fatura (belge) tarihi ileri olamaz.");
        var belgeNo = MikroEvrak.Kes(istek.BelgeNo?.Trim(), 50);
        MikroEvrak.DepoKontrol(conn, tx, istek.DepoNo, tarih);
        var cari = MikroEvrak.Cari(conn, tx, istek.CariKod);

        // Aynı tedarikçinin aynı belge numarası iki kez girilmesin (irsaliyeden faturalaştırılanlar dahil).
        var mukerrer = conn.ExecuteScalar<int?>("""
            SELECT TOP 1 cha_evrakno_sira FROM CARI_HESAP_HAREKETLERI WITH (NOLOCK)
            WHERE cha_evrak_tip = 0 AND cha_kod = @kod AND cha_belge_no = @belgeNo AND ISNULL(cha_iptal, 0) = 0
            """, new { kod = cari.Kod, belgeNo }, tx);
        if (mukerrer is { } m) throw new IslemHatasi($"Bu tedarikçinin {belgeNo} numaralı faturası zaten girilmiş (evrak {m}).");

        // Stok ve cari satırları aynı numarayı taşır; ikisinin de en büyüğünden devam edilir.
        var sira = Math.Max(
            yazici.SonrakiSira(conn, tx, "CARI_HESAP_HAREKETLERI", "cha_evrak_tip", Mikro.CariEvrakTip.AlisFaturasi, "cha_evrakno_seri", "", "cha_evrakno_sira"),
            yazici.SonrakiSira(conn, tx, "STOK_HAREKETLERI", "sth_evraktip", Mikro.StokEvrakTip.GirisFaturasi, "sth_evrakno_seri", "", "sth_evrakno_sira"));
        var altKur = yazici.AlternatifKur(conn, tx);
        var srm = MikroEvrak.DepoSorumlulukMerkezi(conn, tx, istek.DepoNo);
        var satirlar = Hesapla(conn, tx, config, istek.Satirlar);
        var chaGuid = MikroYazici.YeniGuid();
        var faturaSatirlari = new List<IrsaliyeSatiri>();

        for (var i = 0; i < satirlar.Count; i++)
        {
            var s = satirlar[i];
            var net = (double)(s.Tutar - s.Iskonto);
            var d = StokSatiri(yazici, Mikro.StokEvrakTip.GirisFaturasi, "", sira, i, tarih, belgeTarih, belgeNo, cari, s, altKur, istek.DepoNo);
            d["sth_tip"] = (byte)Mikro.StokHarTip.Giris;
            d["sth_fat_uid"] = chaGuid;
            d["sth_cari_srm_merkezi"] = srm;
            d["sth_stok_srm_merkezi"] = srm;
            d["sth_maliyet_ana"] = net;
            d["sth_maliyet_alternatif"] = Math.Round(net / altKur, 12);
            d["sth_maliyet_orjinal"] = net;
            for (var k = 1; k <= 10; k++) d[$"sth_isk_mas{k}"] = (byte)1;
            yazici.Ekle(conn, tx, "STOK_HAREKETLERI", d);
            faturaSatirlari.Add(new IrsaliyeSatiri(Guid.Empty, tarih, belgeTarih, belgeNo, cari.Kod, srm, s.Stok.VergiPntr,
                (double)s.Tutar, s.Isk(0), s.Isk(1), s.Isk(2), s.Isk(3), s.Isk(4), 0, s.Kdv, Guid.Empty));
        }

        var c = CariFaturaSatiri(yazici, chaGuid, MikroEvrak.Simdi(), Mikro.CariEvrakTip.AlisFaturasi, sira, tarih, belgeTarih, belgeNo,
                                 cari.Kod, srm, altKur, faturaSatirlari);
        c["cha_tip"] = (byte)Mikro.CariHarTip.Alacak;
        yazici.Ekle(conn, tx, "CARI_HESAP_HAREKETLERI", c);
        return FaturaOzeti(Mikro.CariEvrakTip.AlisFaturasi, "", sira, c);
    });
}
