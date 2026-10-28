using System.Data;
using System.Security.Claims;
using Butcem.Api.Infrastructure;
using Dapper;

namespace Butcem.Api.Features;

/// <summary>
/// Mağaza siparişi: satışa göre öneri, elle düzeltme, Mikro'ya kayıt, tedarikçiye / depoya gönderim.
///
/// Öneri: mağazanın son N gündeki net satışı (Satış raporuyla aynı tanım: çıkış faturası, iadeler düşülür) günlük ortalamaya
/// çevrilir; "kaç günlük" ihtiyaçtan depodaki stok ve açık siparişler düşülür. Ürün mağazaya en son alışla geldiyse
/// o tedarikçiye, depolar arası sevkle geldiyse o depoya sipariş önerilir. Hiç girişi yoksa stok kartındaki satıcı cari.
///
/// Kayıt, Mikro'nun kendi sipariş evrakları gibi (örnekler: HATIP 511 satınalma, HATIP 15 depolar arası):
///  - Tedarikçiye: SIPARISLER, sip_tip 1 (verilen sipariş), seri = mağazanın serisi; Mikro'da irsaliye girilirken çağrılabilir.
///  - Depoya:      DEPOLAR_ARASI_SIPARISLER, giriş deposu mağaza, çıkış deposu kaynak depo.
///  Mikro bu iki evrakta başka tablo yazmıyor; SIPARISLER_OZET'i tabloların kendi trigger'ı günceller.
/// </summary>
public static class Siparisler
{
    public const string Tedarikci = "tedarikci";
    public const string Depo = "depo";

    public sealed record Oneri(string Kod, string Ad, string Birim, string Barkod, string KaynakTur, string KaynakKod, string KaynakAd,
                               decimal Satis, decimal Gunluk, decimal Stok, decimal Acik, decimal Miktar, decimal BirimFiyat, DateTime? SonGiris);

    public sealed record SatirIstek(string Urun, decimal Miktar, decimal? BirimFiyat);
    public sealed record Istek(int Depo, string KaynakTur, string KaynakKod, DateTime? Tarih, DateTime? TeslimTarihi,
                               string? BelgeNo, List<SatirIstek> Satirlar);
    public sealed record Sonuc(string Tur, string Seri, int Sira, int SatirSayisi, decimal Tutar);

    public sealed record Ozet(string Tur, string Seri, int Sira, DateTime Tarih, int Depo, string KaynakKod, string KaynakAd,
                              int SatirSayisi, decimal Miktar, decimal Tutar, decimal Teslim, DateTime Olusturma)
    {
        public List<Gonderim> Gonderimler { get; init; } = [];
    }
    public sealed record Satir(int SatirNo, string StokKodu, string UrunAdi, string Birim, string Barkod, decimal Miktar, decimal Teslim, decimal BirimFiyat, decimal Tutar);
    public sealed record Gonderim(string Tur, string Seri, int Sira, string Kanal, string Alici, DateTime Zaman, string Kullanici);

    public sealed record Evrak(string Tur, string Seri, int Sira, DateTime Tarih, DateTime TeslimTarihi, int Depo, string DepoAdi,
                               string KaynakKod, string KaynakAd, string BelgeNo, List<Satir> Satirlar);

    /// <summary>Sipariş detayındaki ürün için merkezin görmek istediği bilgiler (sipariş deposuna göre).</summary>
    public sealed record UrunBilgisi(string StokKodu, decimal Stok, DateTime? SonGelisTarih, decimal? SonGelisMiktar, string? SonGelisTur,
                                     string? SonGelisKaynak, string? SonGelisEvrak, DateTime? SonAlisTarih, string? SonAlisCari,
                                     decimal? SonAlisFiyat, decimal KdvOrani, decimal? SonAlisKdvDahil, decimal? SatisFiyati,
                                     decimal? KarOrani, decimal Satis30, decimal? KacGunluk)
    {
        /// <summary>Her depoya (ana depo ve mağazalar) son geliş ve o depodaki stok; ürünün izlediği yol görünsün diye.</summary>
        public List<DepoGelisi> Depolar { get; init; } = [];
    }

    public sealed record DepoGelisi(int Depo, string DepoAdi, decimal Stok, DateTime? Tarih, decimal? Miktar, string? Tur, string? Kaynak);

    private sealed record HamDepoGelisi(string Kod, int Depo, DateTime Tarih, decimal Miktar, int Tip, string? Cari, int CikisDepo);

    private sealed record HamBilgi(string StokKodu, int VergiPntr, decimal Stok, DateTime? GTarih, decimal? GMiktar, int? GTip,
                                   string? GCari, int? GCikisDepo, string? GSeri, int? GSira, DateTime? ATarih, string? ACari,
                                   decimal? AFiyat, decimal? SatisFiyati, decimal Satis30);

    public sealed record GonderIstek(string Tur, string? Seri, int Sira, string Alici, string? Not);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/siparis").RequireYetki(Yetkiler.Siparis).WithTags("Sipariş");

        // Otomatik öneri: depodaki satışa, stoka ve açık siparişlere göre.
        g.MapGet("/oneri", (int depo, int? gecmis, int? gun, bool? hepsi, ClaimsPrincipal user, Db db) =>
        {
            user.DepoYetkisi(depo);
            using var conn = db.Mikro();
            var liste = Hesapla(conn, depo, Gecmis(gecmis), Gun(gun), null);
            return Results.Ok(hepsi == true ? liste : liste.Where(o => o.Miktar > 0).ToList());
        });

        // Elle eklenen ürün: aynı hesap, tek ürün (satışı olmasa da döner).
        g.MapGet("/urun", (string k, int depo, int? gecmis, int? gun, ClaimsPrincipal user, Db db) =>
        {
            user.DepoYetkisi(depo);
            using var conn = db.Mikro();
            var stok = MikroEvrak.Stok(conn, null, k);
            return Results.Ok(Hesapla(conn, depo, Gecmis(gecmis), Gun(gun), stok.Kod).Single());
        });

        g.MapPost("/", (Istek istek, ClaimsPrincipal user, MikroYazici yazici, IConfiguration config, IslemLogu log) =>
        {
            Dogrula(istek, user);
            var sonuc = Kaydet(yazici, config, istek);
            log.Yaz(user.Id(), "siparis", new { istek, sonuc });
            return Results.Ok(sonuc);
        });

        g.MapGet("/", (DateTime? bas, DateTime? bit, int? depo, ClaimsPrincipal user, Db db) =>
        {
            if (user.MagazaKullanicisi()) depo = user.Depo();
            var b = (bas ?? DateTime.Today.AddDays(-30)).Date;
            var e = (bit ?? DateTime.Today).Date;
            using var conn = db.Mikro();
            var liste = conn.Query<Ozet>("""
                SELECT TOP 200 * FROM (
                    SELECT 'tedarikci' AS Tur, sip_evrakno_seri AS Seri, sip_evrakno_sira AS Sira, MIN(sip_tarih) AS Tarih,
                           MIN(sip_depono) AS Depo, MIN(sip_musteri_kod) AS KaynakKod, ISNULL(MIN(c.cari_unvan1), '') AS KaynakAd,
                           COUNT(*) AS SatirSayisi, CAST(SUM(sip_miktar) AS decimal(18,3)) AS Miktar,
                           CAST(SUM(sip_tutar) AS decimal(18,2)) AS Tutar, CAST(SUM(sip_teslim_miktar) AS decimal(18,3)) AS Teslim,
                           MIN(sip_create_date) AS Olusturma
                    FROM SIPARISLER s WITH (NOLOCK)
                    LEFT JOIN CARI_HESAPLAR c WITH (NOLOCK) ON c.cari_kod = s.sip_musteri_kod
                    WHERE sip_tip = 1 AND sip_tarih BETWEEN @b AND @e AND ISNULL(sip_iptal, 0) = 0
                      AND (@depo IS NULL OR sip_depono = @depo)
                    GROUP BY sip_evrakno_seri, sip_evrakno_sira
                    UNION ALL
                    SELECT 'depo', ssip_evrakno_seri, ssip_evrakno_sira, MIN(ssip_tarih),
                           MIN(ssip_girdepo), CAST(MIN(ssip_cikdepo) AS nvarchar(25)), ISNULL(MIN(d.dep_adi), ''),
                           COUNT(*), CAST(SUM(ssip_miktar) AS decimal(18,3)), CAST(SUM(ssip_tutar) AS decimal(18,2)),
                           CAST(SUM(ssip_teslim_miktar) AS decimal(18,3)), MIN(ssip_create_date)
                    FROM DEPOLAR_ARASI_SIPARISLER s WITH (NOLOCK)
                    LEFT JOIN DEPOLAR d WITH (NOLOCK) ON d.dep_no = s.ssip_cikdepo
                    WHERE ssip_tarih BETWEEN @b AND @e AND ISNULL(ssip_iptal, 0) = 0
                      AND (@depo IS NULL OR ssip_girdepo = @depo)
                    GROUP BY ssip_evrakno_seri, ssip_evrakno_sira
                ) x
                ORDER BY Tarih DESC, Olusturma DESC
                """, new { b, e, depo }).ToList();

            if (liste.Count > 0)
            {
                using var appConn = db.App();
                var gonderimler = appConn.Query<Gonderim>("""
                    SELECT g.Tur, g.Seri, g.Sira, g.Kanal, g.Alici, g.Zaman, ISNULL(k.AdSoyad, '') AS Kullanici
                    FROM SiparisGonderimleri g LEFT JOIN Kullanicilar k ON k.Id = g.KullaniciId
                    WHERE g.Zaman >= @b ORDER BY g.Zaman
                    """, new { b = b.AddDays(-1) }).ToLookup(x => (x.Tur, x.Seri, x.Sira));
                liste = liste.Select(o => o with { Gonderimler = gonderimler[(o.Tur, o.Seri, o.Sira)].ToList() }).ToList();
            }
            return Results.Ok(liste);
        });

        g.MapGet("/detay", (string tur, string? seri, int sira, ClaimsPrincipal user, Db db) =>
        {
            using var conn = db.Mikro();
            var evrak = EvrakOku(conn, tur, seri ?? "", sira);
            user.DepoYetkisi(evrak.Depo);
            return Results.Ok(evrak);
        });

        // Sipariş satırlarının yanında: depodaki stok, depoya son gelen miktar (alış ya da sevk), son alış fiyatı
        // (KDV hariç / dahil), raf fiyatı, kâr oranı, son 30 gün satış ve stokun kaç günlük olduğu.
        g.MapGet("/bilgi", (string tur, string? seri, int sira, ClaimsPrincipal user, Db db, IConfiguration config) =>
        {
            using var conn = db.Mikro();
            var evrak = EvrakOku(conn, tur, seri ?? "", sira);
            user.DepoYetkisi(evrak.Depo);
            return Results.Ok(UrunBilgileri(conn, config, evrak.Depo, evrak.Satirlar.Select(x => x.StokKodu).Distinct().ToList()));
        });

        // Tedarikçiye gidecek sipariş formu (PDF). Fiyat yazılmaz; yalnız ürün, barkod, miktar.
        g.MapGet("/pdf", (string tur, string? seri, int sira, ClaimsPrincipal user, Db db, IConfiguration config) =>
        {
            using var conn = db.Mikro();
            var evrak = EvrakOku(conn, tur, seri ?? "", sira);
            user.DepoYetkisi(evrak.Depo);
            return Results.File(SiparisBelgesi.Pdf(evrak, config), "application/pdf", $"Siparis_{evrak.Seri}{evrak.Sira}.pdf");
        });

        // Gönderim için kayıtlı e-posta / telefon (önce Bütçem'de kaydedilen, yoksa Mikro cari kartı) ve WhatsApp metni.
        g.MapGet("/gonderim", (string tur, string? seri, int sira, ClaimsPrincipal user, Db db, IConfiguration config) =>
        {
            using var conn = db.Mikro();
            var evrak = EvrakOku(conn, tur, seri ?? "", sira);
            user.DepoYetkisi(evrak.Depo);
            var (eposta, telefon) = Iletisim(conn, db, evrak);
            return Results.Ok(new { eposta, telefon, metin = SiparisBelgesi.Metin(evrak, config), epostaHazir = Eposta.Hazir(config) });
        });

        g.MapPost("/eposta", (GonderIstek istek, ClaimsPrincipal user, Db db, IConfiguration config, IslemLogu log) =>
        {
            var adres = istek.Alici.Trim();
            if (!System.Net.Mail.MailAddress.TryCreate(adres, out _)) throw new IslemHatasi("Geçerli bir e-posta adresi yazın.");
            using var conn = db.Mikro();
            var evrak = EvrakOku(conn, istek.Tur, istek.Seri ?? "", istek.Sira);
            user.DepoYetkisi(evrak.Depo);
            var firma = config["Firma:Ad"] ?? "Bütçem";
            var konu = $"{firma} sipariş {evrak.Seri} {evrak.Sira} · {evrak.DepoAdi}";
            var govde = (string.IsNullOrWhiteSpace(istek.Not) ? "" : istek.Not.Trim() + "\n\n") + SiparisBelgesi.Metin(evrak, config);
            Eposta.Gonder(config, adres, konu, govde, SiparisBelgesi.Pdf(evrak, config), $"Siparis_{evrak.Seri}{evrak.Sira}.pdf");
            GonderimKaydet(db, user, evrak, "eposta", adres);
            log.Yaz(user.Id(), "siparis_eposta", new { evrak.Tur, evrak.Seri, evrak.Sira, adres });
            return Results.Ok(new { gonderildi = true });
        });

        // WhatsApp: mesaj kullanıcının telefonundaki / bilgisayarındaki WhatsApp'ta hazır açılır, gönder tuşuna kendisi basar.
        g.MapPost("/whatsapp", (GonderIstek istek, ClaimsPrincipal user, Db db, IConfiguration config, IslemLogu log) =>
        {
            var tel = Telefon(istek.Alici) ?? throw new IslemHatasi("Geçerli bir cep telefonu yazın (örn. 0532 123 45 67).");
            using var conn = db.Mikro();
            var evrak = EvrakOku(conn, istek.Tur, istek.Seri ?? "", istek.Sira);
            user.DepoYetkisi(evrak.Depo);
            var metin = (string.IsNullOrWhiteSpace(istek.Not) ? "" : istek.Not.Trim() + "\n\n") + SiparisBelgesi.Metin(evrak, config);
            GonderimKaydet(db, user, evrak, "whatsapp", tel);
            log.Yaz(user.Id(), "siparis_whatsapp", new { evrak.Tur, evrak.Seri, evrak.Sira, tel });
            return Results.Ok(new { link = $"https://wa.me/{tel}?text={Uri.EscapeDataString(metin)}" });
        });
    }

    private static int Gecmis(int? g) => Math.Clamp(g ?? 28, 7, 180);
    private static int Gun(int? g) => Math.Clamp(g ?? 7, 1, 60);

    /// <summary>Açık sipariş sayılırken bakılan süre: daha eski, kapatılmamış siparişler genelde unutulmuştur.</summary>
    private const int AcikSiparisGun = 30;

    /// <summary>Kaynağı bulmak için mağazaya son giriş en fazla bu kadar geriye aranır.</summary>
    private const int SonGirisGun = 365;

    private sealed record HamSatir(string Kod, string Ad, string Birim, string Barkod, string? GirisTur, string? GirisKaynak,
                                   DateTime? SonGiris, string SaticiCari, decimal Satis, decimal Stok, decimal Acik, decimal SonAlis);

    public static List<Oneri> Hesapla(IDbConnection conn, int depo, int gecmis, int gun, string? tekKod)
    {
        var bugun = DateTime.Today;
        var ham = conn.Query<HamSatir>("""
            WITH sat AS (
                SELECT h.sth_stok_kod AS kod,
                       SUM(CASE WHEN h.sth_normal_iade = 1 THEN -1 ELSE 1 END * h.sth_miktar) AS miktar
                FROM STOK_HAREKETLERI h WITH (NOLOCK)
                WHERE h.sth_tarih >= @bas AND h.sth_tarih < @yarin
                  AND h.sth_tip = 1 AND h.sth_evraktip = 4 AND ISNULL(h.sth_iptal, 0) = 0
                  AND h.sth_cikis_depo_no = @depo
                  AND (@tekKod IS NULL OR h.sth_stok_kod = @tekKod)
                GROUP BY h.sth_stok_kod
            ),
            urun AS (
                SELECT kod, miktar FROM sat WHERE miktar > 0 AND @tekKod IS NULL
                UNION ALL
                SELECT @tekKod, ISNULL((SELECT miktar FROM sat), 0) WHERE @tekKod IS NOT NULL
            ),
            giris AS (
                SELECT x.kod, x.tur, x.kaynak, x.tarih FROM (
                    SELECT h.sth_stok_kod AS kod,
                           CASE WHEN h.sth_tip = 0 THEN 'tedarikci' ELSE 'depo' END AS tur,
                           CASE WHEN h.sth_tip = 0 THEN h.sth_cari_kodu ELSE CAST(h.sth_cikis_depo_no AS nvarchar(25)) END AS kaynak,
                           h.sth_tarih AS tarih,
                           ROW_NUMBER() OVER (PARTITION BY h.sth_stok_kod ORDER BY h.sth_tarih DESC, h.sth_create_date DESC) AS r
                    FROM STOK_HAREKETLERI h WITH (NOLOCK)
                    JOIN urun u ON u.kod = h.sth_stok_kod
                    WHERE h.sth_tarih >= @girisBas AND h.sth_giris_depo_no = @depo AND h.sth_normal_iade = 0 AND h.sth_miktar > 0
                      AND ((h.sth_tip = 0 AND h.sth_evraktip IN (3, 13) AND h.sth_cari_kodu <> '')
                        OR (h.sth_tip = 2 AND h.sth_cins = 6 AND h.sth_cikis_depo_no <> @depo))
                ) x WHERE x.r = 1
            ),
            stok AS (
                SELECT o.sho_StokKodu AS kod, SUM(o.sho_GirisNormal + o.sho_GirisIade - o.sho_CikisNormal - o.sho_CikisIade) AS miktar
                FROM STOK_HAREKETLERI_OZET o WITH (NOLOCK)
                JOIN urun u ON u.kod = o.sho_StokKodu
                WHERE o.sho_Depo = @depo AND o.sho_HareketCins NOT IN (9, 15)
                GROUP BY o.sho_StokKodu
            ),
            acik AS (
                SELECT kod, SUM(kalan) AS miktar FROM (
                    SELECT s.sip_stok_kod AS kod, s.sip_miktar - s.sip_teslim_miktar AS kalan
                    FROM SIPARISLER s WITH (NOLOCK) JOIN urun u ON u.kod = s.sip_stok_kod
                    WHERE s.sip_tip = 1 AND s.sip_depono = @depo AND s.sip_kapat_fl = 0 AND ISNULL(s.sip_iptal, 0) = 0
                      AND s.sip_miktar > s.sip_teslim_miktar AND s.sip_tarih >= @acikBas
                    UNION ALL
                    SELECT s.ssip_stok_kod, s.ssip_miktar - s.ssip_teslim_miktar
                    FROM DEPOLAR_ARASI_SIPARISLER s WITH (NOLOCK) JOIN urun u ON u.kod = s.ssip_stok_kod
                    WHERE s.ssip_girdepo = @depo AND s.ssip_kapat_fl = 0 AND ISNULL(s.ssip_iptal, 0) = 0
                      AND s.ssip_miktar > s.ssip_teslim_miktar AND s.ssip_tarih >= @acikBas
                ) a GROUP BY kod
            )
            SELECT st.sto_kod AS Kod, st.sto_isim AS Ad, st.sto_birim1_ad AS Birim,
                   ISNULL((SELECT TOP 1 b.bar_kodu FROM BARKOD_TANIMLARI b WITH (NOLOCK) WHERE b.bar_stokkodu = st.sto_kod), st.sto_kod) AS Barkod,
                   g.tur AS GirisTur, g.kaynak AS GirisKaynak, g.tarih AS SonGiris, st.sto_sat_cari_kod AS SaticiCari,
                   CAST(u.miktar AS decimal(18,3)) AS Satis,
                   CAST(ISNULL(s.miktar, 0) AS decimal(18,3)) AS Stok,
                   CAST(ISNULL(a.miktar, 0) AS decimal(18,3)) AS Acik,
                   CAST(ISNULL(fiy.f, 0) AS decimal(18,4)) AS SonAlis
            FROM urun u
            JOIN STOKLAR st WITH (NOLOCK) ON st.sto_kod = u.kod
            LEFT JOIN giris g ON g.kod = u.kod
            LEFT JOIN stok s ON s.kod = u.kod
            LEFT JOIN acik a ON a.kod = u.kod
            OUTER APPLY (
                SELECT TOP 1 (h.sth_tutar - h.sth_iskonto1 - h.sth_iskonto2 - h.sth_iskonto3
                              - h.sth_iskonto4 - h.sth_iskonto5 - h.sth_iskonto6) / h.sth_miktar AS f
                FROM STOK_HAREKETLERI h WITH (NOLOCK)
                WHERE h.sth_stok_kod = u.kod AND h.sth_tip = 0 AND h.sth_evraktip IN (3, 13)
                  AND h.sth_normal_iade = 0 AND h.sth_miktar > 0
                ORDER BY h.sth_tarih DESC, h.sth_create_date DESC
            ) fiy
            WHERE ISNULL(st.sto_pasif_fl, 0) = 0 AND ISNULL(st.sto_siparis_dursun, 0) = 0
            """, new
        {
            depo,
            tekKod,
            bas = bugun.AddDays(-gecmis),
            yarin = bugun.AddDays(1),
            girisBas = bugun.AddDays(-SonGirisGun),
            acikBas = bugun.AddDays(-AcikSiparisGun),
        }, commandTimeout: 180).ToList();

        if (tekKod is not null && ham.Count == 0)
            throw new IslemHatasi("Bu ürün pasif ya da Mikro'da siparişi durdurulmuş.");

        var cariler = Adlar(conn, "SELECT cari_kod AS Kod, cari_unvan1 AS Ad FROM CARI_HESAPLAR WITH (NOLOCK) WHERE cari_kod IN @kodlar",
            ham.Select(h => h.GirisTur == Depo ? null : h.GirisKaynak ?? h.SaticiCari));
        var depolar = Adlar(conn, "SELECT CAST(dep_no AS nvarchar(25)) AS Kod, dep_adi AS Ad FROM DEPOLAR WITH (NOLOCK)", []);

        return ham.Select(h =>
        {
            var (tur, kaynak) = h.GirisTur == Depo ? (Depo, h.GirisKaynak!) : (Tedarikci, h.GirisKaynak ?? h.SaticiCari);
            var ad = (tur == Depo ? depolar : cariler).GetValueOrDefault(kaynak, "")?.Trim() ?? "";
            var gunluk = h.Satis / gecmis;
            return new Oneri(h.Kod, h.Ad, h.Birim, h.Barkod, tur, kaynak, ad, h.Satis, Math.Round(gunluk, 3), h.Stok, h.Acik,
                             OnerilenMiktar(gunluk, gun, h.Stok, h.Acik), Math.Round(h.SonAlis, 4), h.SonGiris);
        })
        .OrderBy(o => o.KaynakTur).ThenBy(o => o.KaynakAd).ThenBy(o => o.Ad)
        .ToList();
    }

    /// <summary>
    /// "gun" günlük satış kadar mal bulunsun: ihtiyaç − stok − açık sipariş, en yakın tam sayıya yuvarlanır
    /// (yarım ve üstü yukarı; ayda bir satan ürün için her seferinde 1 adet önerilmesin diye yukarı değil).
    /// Eksi stok (Mikro'da sayım/giriş eksikliği) sıfır sayılır.
    /// </summary>
    public static decimal OnerilenMiktar(decimal gunluk, int gun, decimal stok, decimal acik)
    {
        var eksik = gunluk * gun - Math.Max(stok, 0) - Math.Max(acik, 0);
        return eksik <= 0 ? 0 : Math.Round(eksik, MidpointRounding.AwayFromZero);
    }

    private static Dictionary<string, string> Adlar(IDbConnection conn, string sql, IEnumerable<string?> kodlar)
    {
        var liste = kodlar.Where(k => !string.IsNullOrEmpty(k)).Distinct().ToList();
        if (sql.Contains("@kodlar") && liste.Count == 0) return [];
        return conn.Query<(string Kod, string Ad)>(sql, new { kodlar = liste }).ToDictionary(x => x.Kod, x => x.Ad);
    }

    private static void Dogrula(Istek istek, ClaimsPrincipal user)
    {
        user.DepoYetkisi(istek.Depo);
        if (istek.KaynakTur is not (Tedarikci or Depo)) throw new IslemHatasi("Sipariş kaynağı tedarikçi ya da depo olmalı.");
        if (string.IsNullOrWhiteSpace(istek.KaynakKod)) throw new IslemHatasi("Tedarikçi ya da depo seçin.");
        if (istek.KaynakTur == Depo && (!int.TryParse(istek.KaynakKod, out var d) || d == istek.Depo))
            throw new IslemHatasi("Siparişin verileceği depo, mağazanın kendi deposu olamaz.");
        if (istek.Satirlar is not { Count: > 0 }) throw new IslemHatasi("En az bir ürün ekleyin.");
        if (istek.Satirlar.Count > 500) throw new IslemHatasi("Bir siparişte en fazla 500 satır olabilir.");
        if (istek.Satirlar.Any(s => s.Miktar <= 0)) throw new IslemHatasi("Miktar sıfırdan büyük olmalı.");
        if (istek.Satirlar.Any(s => s.BirimFiyat < 0)) throw new IslemHatasi("Fiyat negatif olamaz.");
        if (istek.Tarih is { } t && t.Date > DateTime.Today) throw new IslemHatasi("İleri tarihli sipariş girilemez.");
        if (istek.TeslimTarihi is { } tt && tt.Date < (istek.Tarih ?? DateTime.Today).Date)
            throw new IslemHatasi("Teslim tarihi sipariş tarihinden önce olamaz.");
    }

    public static Sonuc Kaydet(MikroYazici yazici, IConfiguration config, Istek istek) => yazici.Calistir((conn, tx) =>
    {
        var tarih = (istek.Tarih ?? DateTime.Today).Date;
        var teslim = (istek.TeslimTarihi ?? tarih).Date;
        var seri = yazici.DepoSerisi(istek.Depo);
        var belge = MikroEvrak.Kes(istek.BelgeNo?.Trim(), 50);
        var simdi = MikroEvrak.Simdi();
        var gorulen = new HashSet<string>();
        var toplam = 0m;

        if (istek.KaynakTur == Depo)
        {
            var cikis = int.Parse(istek.KaynakKod);
            MikroEvrak.DepoKontrol(conn, tx, istek.Depo, tarih);
            MikroEvrak.DepoKontrol(conn, tx, cikis, tarih);
            var sira = conn.ExecuteScalar<int>(
                "SELECT ISNULL(MAX(ssip_evrakno_sira), 0) + 1 FROM DEPOLAR_ARASI_SIPARISLER WITH (UPDLOCK, HOLDLOCK) WHERE ssip_evrakno_seri = @seri",
                new { seri }, tx);
            for (var i = 0; i < istek.Satirlar.Count; i++)
            {
                var stok = SatirStok(conn, tx, istek.Satirlar[i], gorulen);
                yazici.Ekle(conn, tx, "DEPOLAR_ARASI_SIPARISLER", new Dictionary<string, object?>
                {
                    ["ssip_Guid"] = MikroYazici.YeniGuid(),
                    ["ssip_fileid"] = (short)86,
                    ["ssip_create_user"] = yazici.KullaniciNo,
                    ["ssip_create_date"] = simdi,
                    ["ssip_lastup_user"] = yazici.KullaniciNo,
                    ["ssip_lastup_date"] = simdi,
                    ["ssip_tarih"] = tarih,
                    ["ssip_teslim_tarih"] = teslim,
                    ["ssip_evrakno_seri"] = seri,
                    ["ssip_evrakno_sira"] = sira,
                    ["ssip_satirno"] = i,
                    ["ssip_belgeno"] = belge,
                    ["ssip_belge_tarih"] = tarih,
                    ["ssip_stok_kod"] = stok.Kod,
                    ["ssip_miktar"] = (double)istek.Satirlar[i].Miktar,
                    ["ssip_girdepo"] = istek.Depo,
                    ["ssip_cikdepo"] = cikis,
                    ["ssip_birim_pntr"] = (byte)1,
                    // Mikro'nun kendi kaydında bu kolonlar NULL.
                    ["ssip_gecerlilik_tarihi"] = DBNull.Value,
                    ["ssip_rezervasyon_miktari"] = DBNull.Value,
                    ["ssip_rezerveden_teslim_edilen"] = DBNull.Value,
                });
            }
            return new Sonuc(Depo, seri, sira, istek.Satirlar.Count, 0);
        }
        else
        {
            MikroEvrak.DepoKontrol(conn, tx, istek.Depo, tarih);
            var cari = MikroEvrak.Cari(conn, tx, istek.KaynakKod);
            var srm = MikroEvrak.DepoSorumlulukMerkezi(conn, tx, istek.Depo);
            var sira = yazici.SonrakiSira(conn, tx, "SIPARISLER", "sip_tip", 1, "sip_evrakno_seri", seri, "sip_evrakno_sira");
            for (var i = 0; i < istek.Satirlar.Count; i++)
            {
                var s = istek.Satirlar[i];
                var stok = SatirStok(conn, tx, s, gorulen);
                var fiyat = Math.Round(s.BirimFiyat ?? MikroEvrak.SonAlisFiyati(conn, tx, stok.Kod, tarih), 4);
                var tutar = Math.Round(fiyat * s.Miktar, 2, MidpointRounding.AwayFromZero);
                var oran = Faturalar.KdvOrani(config, stok.VergiPntr);
                toplam += tutar;
                var d = new Dictionary<string, object?>
                {
                    ["sip_Guid"] = MikroYazici.YeniGuid(),
                    ["sip_fileid"] = (short)21,
                    ["sip_create_user"] = yazici.KullaniciNo,
                    ["sip_create_date"] = simdi,
                    ["sip_lastup_user"] = yazici.KullaniciNo,
                    ["sip_lastup_date"] = simdi,
                    ["sip_tarih"] = tarih,
                    ["sip_teslim_tarih"] = teslim,
                    ["sip_tip"] = (byte)1,
                    ["sip_cins"] = (byte)0,
                    ["sip_evrakno_seri"] = seri,
                    ["sip_evrakno_sira"] = sira,
                    ["sip_satirno"] = i,
                    ["sip_belgeno"] = belge,
                    ["sip_belge_tarih"] = tarih,
                    ["sip_musteri_kod"] = cari.Kod,
                    ["sip_stok_kod"] = stok.Kod,
                    ["sip_b_fiyat"] = (double)fiyat,
                    ["sip_miktar"] = (double)s.Miktar,
                    ["sip_birim_pntr"] = (byte)1,
                    ["sip_tutar"] = (double)tutar,
                    ["sip_vergi_pntr"] = (byte)stok.VergiPntr,
                    ["sip_vergi"] = (double)Math.Round(tutar * oran / 100, 10),
                    ["sip_depono"] = istek.Depo,
                    ["sip_cari_sormerk"] = srm,
                    ["sip_doviz_kuru"] = 1d,
                    ["sip_alt_doviz_kuru"] = 1d,
                    ["sip_adresno"] = 1,
                    ["sip_cagrilabilir_fl"] = true,
                };
                // Mikro'nun kaydında iskonto ve masraf türü bayrakları 1.
                for (var k = 1; k <= 6; k++) d[$"sip_iskonto{k}"] = (byte)1;
                for (var k = 1; k <= 4; k++) d[$"sip_masraf{k}"] = (byte)1;
                foreach (var k in SiparisNullKolonlari) d[k] = DBNull.Value;
                yazici.Ekle(conn, tx, "SIPARISLER", d);
            }
            return new Sonuc(Tedarikci, seri, sira, istek.Satirlar.Count, toplam);
        }
    });

    /// <summary>Mikro'nun kendi satınalma siparişinde NULL bıraktığı kolonlar (HATIP 511 ile karşılaştırıldı).</summary>
    private static readonly string[] SiparisNullKolonlari =
    [
        "sip_promosyon_fl", "sip_yetkili_uid", "sip_kapatmanedenkod", "sip_gecerlilik_tarihi", "sip_onodeme_evrak_tip",
        "sip_onodeme_evrak_seri", "sip_onodeme_evrak_sira", "sip_rezervasyon_miktari", "sip_rezerveden_teslim_edilen",
        "sip_HareketGrupKodu1", "sip_HareketGrupKodu2", "sip_HareketGrupKodu3", "sip_Olcu1", "sip_Olcu2", "sip_Olcu3",
        "sip_Olcu4", "sip_Olcu5", "sip_FormulMiktarNo", "sip_FormulMiktar", "sip_satis_fiyat_doviz_cinsi",
        "sip_satis_fiyat_doviz_kuru", "sip_eticaret_kanal_kodu", "sip_Tevkifat_turu", "sip_otv_tevkifat_turu",
        "sip_otv_tevkifat_tutari", "sip_tevkifat_sifirlandi_fl",
    ];

    private static MikroEvrak.StokBilgi SatirStok(IDbConnection conn, IDbTransaction tx, SatirIstek s, HashSet<string> gorulen)
    {
        var stok = MikroEvrak.Stok(conn, tx, s.Urun);
        if (!gorulen.Add(stok.Kod)) throw new IslemHatasi($"{stok.Ad} siparişte iki kez var.");
        return stok;
    }

    public static List<UrunBilgisi> UrunBilgileri(IDbConnection conn, IConfiguration config, int depo, List<string> kodlar)
    {
        if (kodlar.Count == 0) return [];
        var bugun = DateTime.Today;
        // Her bilgi ürün listesine göre ayrı süzülür (STOK_HAREKETLERI'nde stok kodu + tarih indeksi); ürün başına
        // alt sorgu (OUTER APPLY) büyük siparişte dakikalarca sürüyordu.
        var ham = conn.Query<HamBilgi>($"""
            WITH stk AS (
                SELECT o.sho_StokKodu AS kod, SUM(o.sho_GirisNormal + o.sho_GirisIade - o.sho_CikisNormal - o.sho_CikisIade) AS m
                FROM STOK_HAREKETLERI_OZET o WITH (NOLOCK)
                WHERE o.sho_StokKodu IN @kodlar AND o.sho_Depo = @depo AND o.sho_HareketCins NOT IN (9, 15)
                GROUP BY o.sho_StokKodu
            ),
            gl AS (
                -- Depoya son giriş: alış irsaliyesi / faturası ya da başka depodan sevk.
                SELECT * FROM (
                    SELECT h.sth_stok_kod AS kod, h.sth_tarih, CAST(h.sth_tip AS int) AS tip, h.sth_evraktip, h.sth_cari_kodu,
                           h.sth_cikis_depo_no, h.sth_evrakno_seri, h.sth_evrakno_sira,
                           ROW_NUMBER() OVER (PARTITION BY h.sth_stok_kod ORDER BY h.sth_tarih DESC, h.sth_create_date DESC) AS r
                    FROM STOK_HAREKETLERI h WITH (NOLOCK)
                    WHERE h.sth_stok_kod IN @kodlar AND h.sth_tarih >= @girisBas AND h.sth_giris_depo_no = @depo
                      AND h.sth_normal_iade = 0 AND h.sth_miktar > 0 AND ISNULL(h.sth_iptal, 0) = 0
                      AND ((h.sth_tip = 0 AND h.sth_evraktip IN (3, 13) AND h.sth_cari_kodu <> '')
                        OR (h.sth_tip = 2 AND h.sth_cins = 6 AND h.sth_cikis_depo_no <> @depo))
                ) x WHERE x.r = 1
            ),
            glm AS (
                -- O evrakta bu üründen gelen toplam (aynı ürün birkaç satırda olabilir).
                SELECT g.kod, SUM(h.sth_miktar) AS m
                FROM gl g
                JOIN STOK_HAREKETLERI h WITH (NOLOCK)
                  ON h.sth_evraktip = g.sth_evraktip AND h.sth_evrakno_seri = g.sth_evrakno_seri AND h.sth_evrakno_sira = g.sth_evrakno_sira
                 AND h.sth_stok_kod = g.kod AND h.sth_giris_depo_no = @depo AND h.sth_tip = g.tip
                GROUP BY g.kod
            ),
            al AS (
                SELECT * FROM (
                    SELECT h.sth_stok_kod AS kod, h.sth_tarih, h.sth_cari_kodu,
                           (h.sth_tutar - h.sth_iskonto1 - h.sth_iskonto2 - h.sth_iskonto3 - h.sth_iskonto4 - h.sth_iskonto5 - h.sth_iskonto6) / h.sth_miktar AS f,
                           ROW_NUMBER() OVER (PARTITION BY h.sth_stok_kod ORDER BY h.sth_tarih DESC, h.sth_create_date DESC) AS r
                    FROM STOK_HAREKETLERI h WITH (NOLOCK)
                    WHERE h.sth_stok_kod IN @kodlar AND h.sth_tip = 0 AND h.sth_evraktip IN (3, 13)
                      AND h.sth_normal_iade = 0 AND h.sth_miktar > 0
                ) x WHERE x.r = 1
            ),
            fy AS (
                SELECT * FROM (
                    SELECT sfiyat_stokkod AS kod, sfiyat_fiyati AS f,
                           ROW_NUMBER() OVER (PARTITION BY sfiyat_stokkod ORDER BY sfiyat_deposirano DESC) AS r
                    FROM STOK_SATIS_FIYAT_LISTELERI WITH (NOLOCK)
                    WHERE sfiyat_stokkod IN @kodlar AND sfiyat_listesirano = {Mikro.SatisFiyatListesi} AND sfiyat_deposirano IN (0, @depo)
                ) x WHERE x.r = 1
            ),
            sat AS (
                SELECT h.sth_stok_kod AS kod, SUM(CASE WHEN h.sth_normal_iade = 1 THEN -1 ELSE 1 END * h.sth_miktar) AS m
                FROM STOK_HAREKETLERI h WITH (NOLOCK)
                WHERE h.sth_stok_kod IN @kodlar AND h.sth_tarih >= @satisBas AND h.sth_tarih < @yarin
                  AND h.sth_tip = 1 AND h.sth_evraktip = 4 AND ISNULL(h.sth_iptal, 0) = 0 AND h.sth_cikis_depo_no = @depo
                GROUP BY h.sth_stok_kod
            )
            SELECT st.sto_kod AS StokKodu, CAST(st.sto_toptan_vergi AS int) AS VergiPntr,
                   CAST(ISNULL(stk.m, 0) AS decimal(18,3)) AS Stok,
                   gl.sth_tarih AS GTarih, CAST(glm.m AS decimal(18,3)) AS GMiktar, gl.tip AS GTip, gl.sth_cari_kodu AS GCari,
                   gl.sth_cikis_depo_no AS GCikisDepo, gl.sth_evrakno_seri AS GSeri, gl.sth_evrakno_sira AS GSira,
                   al.sth_tarih AS ATarih, al.sth_cari_kodu AS ACari, CAST(al.f AS decimal(18,4)) AS AFiyat,
                   CAST(fy.f AS decimal(18,2)) AS SatisFiyati,
                   CAST(ISNULL(sat.m, 0) AS decimal(18,3)) AS Satis30
            FROM STOKLAR st WITH (NOLOCK)
            LEFT JOIN stk ON stk.kod = st.sto_kod
            LEFT JOIN gl ON gl.kod = st.sto_kod
            LEFT JOIN glm ON glm.kod = st.sto_kod
            LEFT JOIN al ON al.kod = st.sto_kod
            LEFT JOIN fy ON fy.kod = st.sto_kod
            LEFT JOIN sat ON sat.kod = st.sto_kod
            WHERE st.sto_kod IN @kodlar
            """, new { depo, kodlar, girisBas = bugun.AddDays(-SonGirisGun), satisBas = bugun.AddDays(-30), yarin = bugun.AddDays(1) },
            commandTimeout: 120).ToList();

        var cariler = Adlar(conn, "SELECT cari_kod AS Kod, cari_unvan1 AS Ad FROM CARI_HESAPLAR WITH (NOLOCK) WHERE cari_kod IN @kodlar",
            ham.SelectMany(h => new[] { h.GCari, h.ACari }));
        var depolar = Adlar(conn, "SELECT CAST(dep_no AS nvarchar(25)) AS Kod, dep_adi AS Ad FROM DEPOLAR WITH (NOLOCK)", []);

        // Tüm depolar: son giriş (alış ya da başka depodan sevk; aynı evraktaki satırlar toplanır) ve stok.
        var gelisler = conn.Query<HamDepoGelisi>("""
            WITH g AS (
                SELECT h.sth_stok_kod AS kod, h.sth_giris_depo_no AS depo, h.sth_tarih, CAST(h.sth_tip AS int) AS tip, h.sth_evraktip,
                       h.sth_cari_kodu, h.sth_cikis_depo_no, h.sth_evrakno_seri, h.sth_evrakno_sira,
                       ROW_NUMBER() OVER (PARTITION BY h.sth_stok_kod, h.sth_giris_depo_no ORDER BY h.sth_tarih DESC, h.sth_create_date DESC) AS r
                FROM STOK_HAREKETLERI h WITH (NOLOCK)
                WHERE h.sth_stok_kod IN @kodlar AND h.sth_tarih >= @girisBas
                  AND h.sth_normal_iade = 0 AND h.sth_miktar > 0 AND ISNULL(h.sth_iptal, 0) = 0
                  AND ((h.sth_tip = 0 AND h.sth_evraktip IN (3, 13) AND h.sth_cari_kodu <> '')
                    OR (h.sth_tip = 2 AND h.sth_cins = 6 AND h.sth_cikis_depo_no <> h.sth_giris_depo_no))
            )
            SELECT g.kod AS Kod, g.depo AS Depo, g.sth_tarih AS Tarih, CAST(SUM(h.sth_miktar) AS decimal(18,3)) AS Miktar, g.tip AS Tip,
                   g.sth_cari_kodu AS Cari, g.sth_cikis_depo_no AS CikisDepo
            FROM g
            JOIN STOK_HAREKETLERI h WITH (NOLOCK)
              ON h.sth_evraktip = g.sth_evraktip AND h.sth_evrakno_seri = g.sth_evrakno_seri AND h.sth_evrakno_sira = g.sth_evrakno_sira
             AND h.sth_stok_kod = g.kod AND h.sth_giris_depo_no = g.depo AND h.sth_tip = g.tip
            WHERE g.r = 1
            GROUP BY g.kod, g.depo, g.sth_tarih, g.tip, g.sth_cari_kodu, g.sth_cikis_depo_no
            """, new { kodlar, girisBas = bugun.AddDays(-SonGirisGun) }, commandTimeout: 120).ToLookup(x => x.Kod);
        var stoklar = conn.Query<(string Kod, int Depo, decimal Stok)>("""
            SELECT o.sho_StokKodu, o.sho_Depo, CAST(SUM(o.sho_GirisNormal + o.sho_GirisIade - o.sho_CikisNormal - o.sho_CikisIade) AS decimal(18,3))
            FROM STOK_HAREKETLERI_OZET o WITH (NOLOCK)
            WHERE o.sho_StokKodu IN @kodlar AND o.sho_HareketCins NOT IN (9, 15)
            GROUP BY o.sho_StokKodu, o.sho_Depo
            """, new { kodlar }).ToLookup(x => x.Kod);
        var tumDepolar = conn.Query<(int No, string Ad)>("SELECT dep_no, dep_adi FROM DEPOLAR WITH (NOLOCK) ORDER BY dep_no").ToList();
        var gelisCarileri = Adlar(conn, "SELECT cari_kod AS Kod, cari_unvan1 AS Ad FROM CARI_HESAPLAR WITH (NOLOCK) WHERE cari_kod IN @kodlar",
            gelisler.SelectMany(g => g).Where(g => g.Tip == 0).Select(g => g.Cari));

        List<DepoGelisi> DepoListesi(string kod) => tumDepolar.Select(d =>
        {
            var gl = gelisler[kod].FirstOrDefault(x => x.Depo == d.No);
            var stok = stoklar[kod].FirstOrDefault(x => x.Depo == d.No).Stok;
            var kaynak = gl is null ? null
                : gl.Tip == 2 ? tumDepolar.FirstOrDefault(x => x.No == gl.CikisDepo).Ad?.Trim() ?? $"Depo {gl.CikisDepo}"
                : gelisCarileri.GetValueOrDefault(gl.Cari ?? "", gl.Cari ?? "").Trim();
            return new DepoGelisi(d.No, d.Ad.Trim(), stok, gl?.Tarih, gl?.Miktar, gl is null ? null : gl.Tip == 2 ? "sevk" : "alis", kaynak);
        })
        // Hiç girişi ve stoğu olmayan depo listelenmez.
        .Where(d => d.Tarih is not null || d.Stok != 0).ToList();

        return ham.Select(h =>
        {
            decimal kdv;
            try { kdv = Faturalar.KdvOrani(config, h.VergiPntr); } catch (IslemHatasi) { kdv = 0; }
            var sevk = h.GTip == 2;
            var kaynak = h.GTarih is null ? null
                : sevk ? $"Depo {h.GCikisDepo} · {depolar.GetValueOrDefault(h.GCikisDepo?.ToString() ?? "", "").Trim()}"
                : cariler.GetValueOrDefault(h.GCari ?? "", h.GCari ?? "").Trim();
            var dahil = h.AFiyat is { } f ? Math.Round(f * (1 + kdv / 100), 2) : (decimal?)null;
            // Kâr oranı: raf fiyatı (KDV dahil) ile son alışın KDV dahil hali arasındaki fark, satışa göre.
            var kar = h.SatisFiyati is > 0 && dahil is { } d ? Math.Round((h.SatisFiyati.Value - d) / h.SatisFiyati.Value * 100, 1) : (decimal?)null;
            var gunluk = h.Satis30 / 30;
            return new UrunBilgisi(h.StokKodu, h.Stok, h.GTarih, h.GMiktar, h.GTarih is null ? null : sevk ? "sevk" : "alis", kaynak,
                h.GTarih is null ? null : $"{h.GSeri}{h.GSira}", h.ATarih,
                h.ACari is null ? null : cariler.GetValueOrDefault(h.ACari, h.ACari).Trim(),
                h.AFiyat is { } a ? Math.Round(a, 4) : null, kdv, dahil, h.SatisFiyati, kar, h.Satis30,
                gunluk > 0 ? Math.Round(Math.Max(h.Stok, 0) / gunluk, 1) : null) { Depolar = DepoListesi(h.StokKodu) };
        }).ToList();
    }

    public static Evrak EvrakOku(IDbConnection conn, string tur, string seri, int sira)
    {
        var (bas, satirSql) = tur switch
        {
            Tedarikci => ("""
                SELECT TOP 1 s.sip_tarih AS Tarih, s.sip_teslim_tarih AS TeslimTarihi, s.sip_depono AS Depo, ISNULL(d.dep_adi, '') AS DepoAdi,
                       s.sip_musteri_kod AS KaynakKod, ISNULL(c.cari_unvan1, '') AS KaynakAd, s.sip_belgeno AS BelgeNo
                FROM SIPARISLER s WITH (NOLOCK)
                LEFT JOIN DEPOLAR d WITH (NOLOCK) ON d.dep_no = s.sip_depono
                LEFT JOIN CARI_HESAPLAR c WITH (NOLOCK) ON c.cari_kod = s.sip_musteri_kod
                WHERE s.sip_tip = 1 AND s.sip_evrakno_seri = @seri AND s.sip_evrakno_sira = @sira
                """, """
                SELECT s.sip_satirno AS SatirNo, s.sip_stok_kod AS StokKodu, ISNULL(st.sto_isim, '') AS UrunAdi, ISNULL(st.sto_birim1_ad, '') AS Birim,
                       ISNULL((SELECT TOP 1 b.bar_kodu FROM BARKOD_TANIMLARI b WITH (NOLOCK) WHERE b.bar_stokkodu = s.sip_stok_kod), s.sip_stok_kod) AS Barkod,
                       CAST(s.sip_miktar AS decimal(18,3)) AS Miktar, CAST(s.sip_teslim_miktar AS decimal(18,3)) AS Teslim,
                       CAST(s.sip_b_fiyat AS decimal(18,4)) AS BirimFiyat, CAST(s.sip_tutar AS decimal(18,2)) AS Tutar
                FROM SIPARISLER s WITH (NOLOCK) LEFT JOIN STOKLAR st WITH (NOLOCK) ON st.sto_kod = s.sip_stok_kod
                WHERE s.sip_tip = 1 AND s.sip_evrakno_seri = @seri AND s.sip_evrakno_sira = @sira
                ORDER BY s.sip_satirno
                """),
            Depo => ("""
                SELECT TOP 1 s.ssip_tarih AS Tarih, s.ssip_teslim_tarih AS TeslimTarihi, s.ssip_girdepo AS Depo, ISNULL(d.dep_adi, '') AS DepoAdi,
                       CAST(s.ssip_cikdepo AS nvarchar(25)) AS KaynakKod, ISNULL(k.dep_adi, '') AS KaynakAd, s.ssip_belgeno AS BelgeNo
                FROM DEPOLAR_ARASI_SIPARISLER s WITH (NOLOCK)
                LEFT JOIN DEPOLAR d WITH (NOLOCK) ON d.dep_no = s.ssip_girdepo
                LEFT JOIN DEPOLAR k WITH (NOLOCK) ON k.dep_no = s.ssip_cikdepo
                WHERE s.ssip_evrakno_seri = @seri AND s.ssip_evrakno_sira = @sira
                """, """
                SELECT s.ssip_satirno AS SatirNo, s.ssip_stok_kod AS StokKodu, ISNULL(st.sto_isim, '') AS UrunAdi, ISNULL(st.sto_birim1_ad, '') AS Birim,
                       ISNULL((SELECT TOP 1 b.bar_kodu FROM BARKOD_TANIMLARI b WITH (NOLOCK) WHERE b.bar_stokkodu = s.ssip_stok_kod), s.ssip_stok_kod) AS Barkod,
                       CAST(s.ssip_miktar AS decimal(18,3)) AS Miktar, CAST(s.ssip_teslim_miktar AS decimal(18,3)) AS Teslim,
                       CAST(s.ssip_b_fiyat AS decimal(18,4)) AS BirimFiyat, CAST(s.ssip_tutar AS decimal(18,2)) AS Tutar
                FROM DEPOLAR_ARASI_SIPARISLER s WITH (NOLOCK) LEFT JOIN STOKLAR st WITH (NOLOCK) ON st.sto_kod = s.ssip_stok_kod
                WHERE s.ssip_evrakno_seri = @seri AND s.ssip_evrakno_sira = @sira
                ORDER BY s.ssip_satirno
                """),
            _ => throw new IslemHatasi("Sipariş türü tedarikci ya da depo olmalı."),
        };
        var x = conn.QueryFirstOrDefault<EvrakBasi>(bas, new { seri, sira })
                ?? throw new IslemHatasi($"Sipariş bulunamadı: {seri} {sira}");
        var satirlar = conn.Query<Satir>(satirSql, new { seri, sira }).ToList();
        return new Evrak(tur, seri, sira, x.Tarih, x.TeslimTarihi, x.Depo, x.DepoAdi.Trim(), x.KaynakKod, x.KaynakAd.Trim(), x.BelgeNo, satirlar);
    }

    private sealed record EvrakBasi(DateTime Tarih, DateTime TeslimTarihi, int Depo, string DepoAdi, string KaynakKod, string KaynakAd, string BelgeNo);

    private static string IletisimAnahtari(Evrak e) => e.Tur == Depo ? $"DEPO:{e.KaynakKod}" : e.KaynakKod;

    /// <summary>Bütçem'de en son kullanılan adres/telefon; yoksa Mikro cari kartındaki e-posta ve cep telefonu.</summary>
    private static (string Eposta, string Telefon) Iletisim(IDbConnection conn, Db db, Evrak e)
    {
        using var appConn = db.App();
        var kayit = appConn.QueryFirstOrDefault<IletisimKaydi>(
            "SELECT Eposta, Telefon FROM TedarikciIletisim WHERE Anahtar = @a", new { a = IletisimAnahtari(e) });
        string eposta = kayit?.Eposta ?? "", telefon = kayit?.Telefon ?? "";
        if (e.Tur == Tedarikci && (eposta == "" || telefon == ""))
        {
            var c = conn.QueryFirstOrDefault<(string Eposta, string Cep)>(
                "SELECT ISNULL(cari_EMail, ''), ISNULL(cari_CepTel, '') FROM CARI_HESAPLAR WITH (NOLOCK) WHERE cari_kod = @k", new { k = e.KaynakKod });
            if (eposta == "") eposta = c.Eposta.Trim();
            if (telefon == "") telefon = c.Cep.Trim();
        }
        return (eposta, telefon);
    }

    private sealed record IletisimKaydi(string? Eposta, string? Telefon);

    private static void GonderimKaydet(Db db, ClaimsPrincipal user, Evrak e, string kanal, string alici)
    {
        using var conn = db.App();
        conn.Execute("""
            INSERT INTO SiparisGonderimleri (Tur, Seri, Sira, Kanal, Alici, KullaniciId) VALUES (@Tur, @Seri, @Sira, @kanal, @alici, @id);
            MERGE TedarikciIletisim AS t USING (SELECT @anahtar AS Anahtar) AS s ON t.Anahtar = s.Anahtar
            WHEN MATCHED THEN UPDATE SET Eposta = CASE WHEN @kanal = 'eposta' THEN @alici ELSE t.Eposta END,
                                         Telefon = CASE WHEN @kanal = 'whatsapp' THEN @alici ELSE t.Telefon END,
                                         GuncellemeZamani = SYSDATETIME()
            WHEN NOT MATCHED THEN INSERT (Anahtar, Eposta, Telefon)
                 VALUES (@anahtar, CASE WHEN @kanal = 'eposta' THEN @alici END, CASE WHEN @kanal = 'whatsapp' THEN @alici END);
            """, new { e.Tur, e.Seri, e.Sira, kanal, alici, id = user.Id(), anahtar = IletisimAnahtari(e) });
    }

    /// <summary>Türkiye cep numarasını WhatsApp biçimine çevirir: 0532 123 45 67 → 905321234567.</summary>
    public static string? Telefon(string? s)
    {
        var r = new string((s ?? "").Where(char.IsDigit).ToArray());
        if (r.StartsWith("00")) r = r[2..];
        if (r.Length == 11 && r[0] == '0') r = "9" + r;
        if (r.Length == 10 && r[0] == '5') r = "90" + r;
        return r.Length is >= 11 and <= 15 ? r : null;
    }
}
