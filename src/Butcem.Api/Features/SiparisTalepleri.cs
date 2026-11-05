using System.Data;
using System.Security.Claims;
using Butcem.Api.Infrastructure;
using Dapper;

namespace Butcem.Api.Features;

/// <summary>
/// Onaylı sipariş: mağaza (terminal ya da panel) sipariş talebi verir, talep Bütçem'in kendi veritabanında bekler
/// (ButcemApp.SiparisTalepleri / SiparisTalepSatirlari). Sipariş onay yetkisi olan kişi (patron) miktarları artırıp
/// azaltır, satır çıkarır, kaynağı bilinmeyen ürüne tedarikçi atar ve onaylar; onayda Mikro'ya
/// <see cref="Siparisler.Kaydet"/> ile yazılır (her tedarikçi / depo için ayrı sipariş evrakı, panelden girilenle aynı).
///
/// Planlı sipariş: satışa göre öneri listesinden (<see cref="Siparisler.Hesapla"/>); her ürün önerinin kaynağına gider.
/// Plansız sipariş: talep eden tedarikçiyi ya da depoyu seçer, ürünleri okutur.
/// </summary>
public static class SiparisTalepleri
{
    public static class Durumlar { public const string Bekliyor = "bekliyor", Onaylandi = "onaylandi", Reddedildi = "reddedildi"; }
    public const string Planli = "planli", Plansiz = "plansiz";

    public sealed record SatirIstek(string Urun, decimal Miktar, string? KaynakTur, string? KaynakKod, decimal? Oneri);
    public sealed record Istek(string Tur, int Depo, string? KaynakTur, string? KaynakKod, string? Not, List<SatirIstek> Satirlar);
    public sealed record Sonuc(int Id, int SatirSayisi);

    public sealed record Ozet(int Id, string Tur, int Depo, string DepoAdi, string Durum, string Not, string Olusturan, DateTime Zaman,
                              int SatirSayisi, decimal Miktar, string? Onaylayan, DateTime? OnayZamani, string? RedSebebi, string? Evraklar);

    public sealed record Satir(int Id, string StokKodu, string UrunAdi, string Birim, string KaynakTur, string KaynakKod, string KaynakAd,
                               decimal? Oneri, decimal IstenenMiktar, decimal? OnayMiktar, decimal? BirimFiyat, string? Seri, int? Sira);

    public sealed record Detay(Ozet Talep, List<Satir> Satirlar);

    public sealed record OnaySatiri(int Id, decimal Miktar, string? KaynakTur, string? KaynakKod);
    public sealed record EkSatir(string Urun, decimal Miktar, string? KaynakTur, string? KaynakKod);
    public sealed record OnayIstek(List<OnaySatiri> Satirlar, List<EkSatir>? Ekler, DateTime? TeslimTarihi);
    public sealed record OnaySonuc(List<Siparisler.Sonuc> Evraklar, string? Hata);
    public sealed record RedIstek(string? Sebep);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/siparis-talep").RequireYetki(Yetkiler.SiparisTalep, Yetkiler.SiparisOnay, Yetkiler.Siparis)
                   .WithTags("Sipariş talebi");

        // Planlı sipariş: deponun öneri listesi (panelle aynı hesap).
        g.MapGet("/oneri", (int depo, int? gecmis, int? gun, ClaimsPrincipal user, Db db) =>
        {
            user.DepoYetkisi(depo);
            using var conn = db.Mikro();
            return Results.Ok(Siparisler.Hesapla(conn, depo, Siparisler.Gecmis(gecmis), Siparisler.Gun(gun), null)
                                        .Where(o => o.Miktar > 0).ToList());
        });

        // Okutulan ürün: önerinin aynısı (kaynak, stok, satış), satışı olmasa da döner.
        g.MapGet("/urun", (string k, int depo, ClaimsPrincipal user, Db db) =>
        {
            user.DepoYetkisi(depo);
            using var conn = db.Mikro();
            var stok = MikroEvrak.Stok(conn, null, k.Trim());
            return Results.Ok(Siparisler.Hesapla(conn, depo, Siparisler.Gecmis(null), Siparisler.Gun(null), stok.Kod).Single());
        });

        g.MapPost("/", (Istek istek, HttpRequest req, ClaimsPrincipal user, Db db, IslemLogu log) =>
        {
            user.DepoYetkisi(istek.Depo);
            return TekrarKoruma.Calistir(req, db, "siparis_talep", user.Id(), () =>
            {
                var sonuc = Olustur(db, user, istek);
                log.Yaz(user.Id(), "siparis_talep", new { istek, sonuc });
                return sonuc;
            });
        });

        // Liste: mağaza kullanıcısı kendi deposununkileri görür.
        g.MapGet("/", (string? durum, int? depo, DateTime? bas, ClaimsPrincipal user, Db db) =>
        {
            if (user.MagazaKullanicisi()) depo = user.Depo();
            using var conn = db.App();
            var liste = conn.Query<Ozet>(OzetSql + """
                WHERE (@durum IS NULL OR t.Durum = @durum) AND (@depo IS NULL OR t.Depo = @depo) AND t.Zaman >= @bas
                ORDER BY CASE t.Durum WHEN 'bekliyor' THEN 0 ELSE 1 END, t.Zaman DESC
                """, new { durum = string.IsNullOrWhiteSpace(durum) ? null : durum, depo, bas = (bas ?? DateTime.Today.AddDays(-60)).Date })
                .ToList();
            return Results.Ok(DepoAdlari(db, liste));
        });

        g.MapGet("/{id:int}", (int id, ClaimsPrincipal user, Db db) => Results.Ok(DetayOku(db, user, id)));

        g.MapPost("/{id:int}/onayla", (int id, OnayIstek istek, ClaimsPrincipal user, Db db, MikroYazici yazici, IConfiguration config, IslemLogu log) =>
        {
            var sonuc = Onayla(db, yazici, config, user, id, istek);
            log.Yaz(user.Id(), "siparis_onay", new { id, istek, sonuc });
            return Results.Ok(sonuc);
        }).RequireYetki(Yetkiler.SiparisOnay);

        g.MapPost("/{id:int}/reddet", (int id, RedIstek istek, ClaimsPrincipal user, Db db, IslemLogu log) =>
        {
            using var conn = db.App();
            var n = conn.Execute("""
                UPDATE SiparisTalepleri SET Durum = 'reddedildi', OnaylayanId = @kid, OnayZamani = SYSDATETIME(), RedSebebi = @sebep
                WHERE Id = @id AND Durum = 'bekliyor'
                  AND NOT EXISTS (SELECT 1 FROM SiparisTalepSatirlari WHERE TalepId = @id AND Sira IS NOT NULL)
                """, new { id, kid = user.Id(), sebep = Kes(istek.Sebep?.Trim(), 200) });
            if (n == 0) throw new IslemHatasi("Talep bekleyen durumda değil ya da bir kısmı Mikro'ya aktarılmış.");
            log.Yaz(user.Id(), "siparis_red", new { id, istek });
            return Results.Ok();
        }).RequireYetki(Yetkiler.SiparisOnay);
    }

    private const string OzetSql = """
        SELECT t.Id, t.Tur, t.Depo, CAST('' AS nvarchar(60)) AS DepoAdi, t.Durum, ISNULL(t.Aciklama, '') AS [Not],
               ISNULL(k.AdSoyad, '') AS Olusturan, t.Zaman,
               (SELECT COUNT(*) FROM SiparisTalepSatirlari s WHERE s.TalepId = t.Id) AS SatirSayisi,
               ISNULL((SELECT SUM(ISNULL(s.OnayMiktar, s.IstenenMiktar)) FROM SiparisTalepSatirlari s WHERE s.TalepId = t.Id), 0) AS Miktar,
               o.AdSoyad AS Onaylayan, t.OnayZamani, t.RedSebebi,
               STUFF((SELECT DISTINCT ', ' + s.Seri + ' ' + CAST(s.Sira AS nvarchar(10)) FROM SiparisTalepSatirlari s
                      WHERE s.TalepId = t.Id AND s.Sira IS NOT NULL FOR XML PATH('')), 1, 2, '') AS Evraklar
        FROM SiparisTalepleri t
        LEFT JOIN Kullanicilar k ON k.Id = t.OlusturanId
        LEFT JOIN Kullanicilar o ON o.Id = t.OnaylayanId
        """;

    private static List<Ozet> DepoAdlari(Db db, List<Ozet> liste)
    {
        if (liste.Count == 0) return liste;
        using var m = db.Mikro();
        var adlar = m.Query<(int No, string Ad)>("SELECT dep_no, dep_adi FROM DEPOLAR WITH (NOLOCK)")
                     .ToDictionary(x => x.No, x => x.Ad?.Trim() ?? "");
        return liste.Select(o => o with { DepoAdi = adlar.GetValueOrDefault(o.Depo, "") }).ToList();
    }

    private static string? Kes(string? s, int n) => s is null ? null : s.Length <= n ? s : s[..n];

    private static void KaynakKontrol(string tur, string kod, int depo)
    {
        if (tur is not (Siparisler.Tedarikci or Siparisler.Depo)) throw new IslemHatasi("Sipariş kaynağı tedarikçi ya da depo olmalı.");
        if (tur == Siparisler.Depo && (!int.TryParse(kod, out var d) || d == depo))
            throw new IslemHatasi("Siparişin verileceği depo, mağazanın kendi deposu olamaz.");
    }

    public static Sonuc Olustur(Db db, ClaimsPrincipal user, Istek istek)
    {
        if (istek.Tur is not (Planli or Plansiz)) throw new IslemHatasi("Sipariş türü planlı ya da plansız olmalı.");
        if (istek.Satirlar is not { Count: > 0 }) throw new IslemHatasi("En az bir ürün ekleyin.");
        if (istek.Satirlar.Count > 500) throw new IslemHatasi("Bir siparişte en fazla 500 satır olabilir.");
        if (istek.Satirlar.Any(s => s.Miktar <= 0)) throw new IslemHatasi("Miktar sıfırdan büyük olmalı.");

        using var mikro = db.Mikro();
        // Kaynağı gelmeyen satırlar için öneri hesabındaki kaynak (son alış / son sevk / stok kartındaki satıcı).
        Dictionary<string, Siparisler.Oneri>? oneriler = null;
        var satirlar = new List<(MikroEvrak.StokBilgi Stok, SatirIstek S, string Tur, string Kod, string Ad, decimal? Fiyat)>();
        var gorulen = new HashSet<string>();
        foreach (var s in istek.Satirlar)
        {
            var stok = MikroEvrak.Stok(mikro, null, s.Urun.Trim());
            if (!gorulen.Add(stok.Kod)) throw new IslemHatasi($"{stok.Ad} listede iki kez var.");
            var tur = s.KaynakTur ?? istek.KaynakTur;
            var kod = s.KaynakKod ?? istek.KaynakKod;
            string ad = "";
            if (string.IsNullOrWhiteSpace(tur) || string.IsNullOrWhiteSpace(kod))
            {
                oneriler ??= Siparisler.Hesapla(mikro, istek.Depo, Siparisler.Gecmis(null), Siparisler.Gun(null), null)
                                       .ToDictionary(o => o.Kod);
                var o = oneriler.GetValueOrDefault(stok.Kod)
                        ?? Siparisler.Hesapla(mikro, istek.Depo, Siparisler.Gecmis(null), Siparisler.Gun(null), stok.Kod).Single();
                tur = o.KaynakTur; kod = o.KaynakKod; ad = o.KaynakAd;
            }
            kod = kod?.Trim() ?? "";
            if (kod.Length > 0) KaynakKontrol(tur!, kod, istek.Depo);
            satirlar.Add((stok, s, tur!, kod, ad, null));
        }

        // Kaynak adları ve tedarikçi için son alış fiyatı (onay ekranında yaklaşık tutar için).
        var cariler = satirlar.Where(x => x.Tur == Siparisler.Tedarikci && x.Kod.Length > 0).Select(x => x.Kod).Distinct().ToList();
        var cariAd = cariler.Count == 0 ? [] : mikro.Query<(string Kod, string Ad)>(
            "SELECT cari_kod, cari_unvan1 FROM CARI_HESAPLAR WITH (NOLOCK) WHERE cari_kod IN @cariler", new { cariler })
            .ToDictionary(x => x.Kod, x => x.Ad?.Trim() ?? "");
        var depoAd = mikro.Query<(int No, string Ad)>("SELECT dep_no, dep_adi FROM DEPOLAR WITH (NOLOCK)")
                          .ToDictionary(x => x.No.ToString(), x => x.Ad?.Trim() ?? "");
        var bugun = DateTime.Today;
        satirlar = satirlar.Select(x => x with
        {
            Ad = x.Ad.Length > 0 ? x.Ad : x.Tur == Siparisler.Depo ? depoAd.GetValueOrDefault(x.Kod, x.Kod) : cariAd.GetValueOrDefault(x.Kod, ""),
            Fiyat = x.Tur == Siparisler.Tedarikci ? Math.Round(MikroEvrak.SonAlisFiyati(mikro, null, x.Stok.Kod, bugun), 4) : null,
        }).ToList();

        using var conn = db.App();
        conn.Open();
        using var tx = conn.BeginTransaction();
        var id = conn.ExecuteScalar<int>("""
            INSERT INTO SiparisTalepleri (Tur, Depo, Aciklama, OlusturanId) VALUES (@Tur, @Depo, @not, @kid);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """, new { istek.Tur, istek.Depo, not = Kes(istek.Not?.Trim(), 200), kid = user.Id() }, tx);
        foreach (var x in satirlar)
            conn.Execute("""
                INSERT INTO SiparisTalepSatirlari (TalepId, StokKodu, UrunAdi, Birim, KaynakTur, KaynakKod, KaynakAd, Oneri, IstenenMiktar, BirimFiyat)
                VALUES (@id, @kod, @ad, @birim, @tur, @kaynak, @kaynakAd, @oneri, @miktar, @fiyat)
                """, new { id, kod = x.Stok.Kod, ad = Kes(x.Stok.Ad, 127), birim = x.Stok.Birim ?? "", tur = x.Tur, kaynak = x.Kod,
                           kaynakAd = Kes(x.Ad, 127), oneri = x.S.Oneri, miktar = x.S.Miktar, fiyat = x.Fiyat }, tx);
        tx.Commit();
        return new Sonuc(id, satirlar.Count);
    }

    private static Detay DetayOku(Db db, ClaimsPrincipal user, int id)
    {
        using var conn = db.App();
        var t = conn.QuerySingleOrDefault<Ozet>(OzetSql + " WHERE t.Id = @id", new { id })
                ?? throw new IslemHatasi("Sipariş talebi bulunamadı.");
        if (user.MagazaKullanicisi() && user.Depo() != t.Depo) throw new IslemHatasi("Bu talep başka bir mağazanın.");
        var satirlar = conn.Query<Satir>("""
            SELECT Id, StokKodu, UrunAdi, Birim, KaynakTur, KaynakKod, ISNULL(KaynakAd, '') AS KaynakAd, Oneri, IstenenMiktar,
                   OnayMiktar, BirimFiyat, Seri, Sira
            FROM SiparisTalepSatirlari WHERE TalepId = @id ORDER BY KaynakAd, UrunAdi
            """, new { id }).ToList();
        return new Detay(DepoAdlari(db, [t])[0], satirlar);
    }

    /// <summary>
    /// Patronun miktarları kaydedilir, eklenen ürünler talebe eklenir; miktarı sıfırdan büyük satırlar kaynağa göre
    /// gruplanıp her grup ayrı Mikro siparişi olur. Bir grup yazılamazsa (ör. kilitli depo) yazılanlar işaretli kalır,
    /// talep bekliyor durumunda kalır; tekrar onaylanınca yalnız kalanlar yazılır.
    /// </summary>
    public static OnaySonuc Onayla(Db db, MikroYazici yazici, IConfiguration config, ClaimsPrincipal user, int id, OnayIstek istek)
    {
        var d = DetayOku(db, user, id);
        if (d.Talep.Durum != Durumlar.Bekliyor) throw new IslemHatasi("Bu talep zaten " + (d.Talep.Durum == Durumlar.Onaylandi ? "onaylanmış." : "reddedilmiş."));
        user.DepoYetkisi(d.Talep.Depo);
        var degisen = (istek.Satirlar ?? []).ToDictionary(s => s.Id);
        if (degisen.Values.Any(s => s.Miktar < 0)) throw new IslemHatasi("Miktar negatif olamaz.");

        using (var conn = db.App())
        {
            foreach (var s in d.Satirlar.Where(s => s.Sira is null && degisen.ContainsKey(s.Id)))
            {
                var y = degisen[s.Id];
                var tur = string.IsNullOrWhiteSpace(y.KaynakTur) ? s.KaynakTur : y.KaynakTur;
                var kod = string.IsNullOrWhiteSpace(y.KaynakKod) ? s.KaynakKod : y.KaynakKod.Trim();
                var ad = s.KaynakAd;
                if (tur != s.KaynakTur || kod != s.KaynakKod)
                {
                    KaynakKontrol(tur, kod, d.Talep.Depo);
                    ad = KaynakAdi(db, tur, kod);
                }
                conn.Execute("UPDATE SiparisTalepSatirlari SET OnayMiktar = @m, KaynakTur = @tur, KaynakKod = @kod, KaynakAd = @ad WHERE Id = @Id",
                             new { m = y.Miktar, tur, kod, ad = Kes(ad, 127), s.Id });
            }
            if (istek.Ekler is { Count: > 0 })
            {
                using var mikro = db.Mikro();
                foreach (var e in istek.Ekler)
                {
                    if (e.Miktar <= 0) continue;
                    var stok = MikroEvrak.Stok(mikro, null, e.Urun.Trim());
                    if (d.Satirlar.Any(s => s.StokKodu == stok.Kod)) throw new IslemHatasi($"{stok.Ad} talepte zaten var; miktarını değiştirin.");
                    var tur = e.KaynakTur ?? "";
                    var kod = e.KaynakKod?.Trim() ?? "";
                    if (kod.Length == 0)
                    {
                        var o = Siparisler.Hesapla(mikro, d.Talep.Depo, Siparisler.Gecmis(null), Siparisler.Gun(null), stok.Kod).Single();
                        tur = o.KaynakTur; kod = o.KaynakKod;
                    }
                    if (kod.Length > 0) KaynakKontrol(tur, kod, d.Talep.Depo);
                    conn.Execute("""
                        INSERT INTO SiparisTalepSatirlari (TalepId, StokKodu, UrunAdi, Birim, KaynakTur, KaynakKod, KaynakAd, IstenenMiktar, OnayMiktar, BirimFiyat)
                        VALUES (@id, @kod, @ad, @birim, @tur, @kaynak, @kaynakAd, 0, @miktar, @fiyat)
                        """, new { id, kod = stok.Kod, ad = Kes(stok.Ad, 127), birim = stok.Birim ?? "", tur, kaynak = kod,
                                   kaynakAd = Kes(kod.Length == 0 ? "" : KaynakAdi(db, tur, kod), 127), miktar = e.Miktar,
                                   fiyat = tur == Siparisler.Tedarikci ? Math.Round(MikroEvrak.SonAlisFiyati(mikro, null, stok.Kod, DateTime.Today), 4) : (decimal?)null });
                }
            }
        }

        d = DetayOku(db, user, id);
        var kalan = d.Satirlar.Where(s => s.Sira is null && (s.OnayMiktar ?? s.IstenenMiktar) > 0).ToList();
        if (kalan.Count == 0 && d.Satirlar.All(s => s.Sira is null))
            throw new IslemHatasi("Onaylanacak ürün kalmadı. Siparişi istemiyorsanız reddedin.");
        if (kalan.Any(s => s.KaynakKod.Length == 0))
            throw new IslemHatasi("Kaynağı bilinmeyen ürünlere tedarikçi seçin: " + string.Join(", ", kalan.Where(s => s.KaynakKod.Length == 0).Select(s => s.UrunAdi).Take(5)));

        var tarih = DateTime.Today;
        var teslim = (istek.TeslimTarihi ?? tarih.AddDays(1)).Date;
        if (teslim < tarih) throw new IslemHatasi("Teslim tarihi bugünden önce olamaz.");
        var yazilan = new List<Siparisler.Sonuc>();
        string? hata = null;
        foreach (var grup in kalan.GroupBy(s => (s.KaynakTur, s.KaynakKod)))
        {
            var mikroIstek = new Siparisler.Istek(d.Talep.Depo, grup.Key.KaynakTur, grup.Key.KaynakKod, tarih, teslim, $"TALEP-{id}",
                grup.Select(s => new Siparisler.SatirIstek(s.StokKodu, s.OnayMiktar ?? s.IstenenMiktar,
                                                           grup.Key.KaynakTur == Siparisler.Tedarikci && s.BirimFiyat > 0 ? s.BirimFiyat : null)).ToList());
            try
            {
                Siparisler.Dogrula(mikroIstek, user);
                var r = Siparisler.Kaydet(yazici, config, mikroIstek);
                yazilan.Add(r);
                using var conn = db.App();
                conn.Execute("UPDATE SiparisTalepSatirlari SET Seri = @Seri, Sira = @Sira, OnayMiktar = ISNULL(OnayMiktar, IstenenMiktar) WHERE Id IN @ids",
                             new { r.Seri, r.Sira, ids = grup.Select(s => s.Id).ToList() });
            }
            catch (IslemHatasi ex)
            {
                hata = $"{grup.First().KaynakAd} siparişi yazılamadı: {ex.Message}";
                break;
            }
        }

        if (hata is null)
        {
            using var conn = db.App();
            conn.Execute("""
                UPDATE SiparisTalepSatirlari SET OnayMiktar = 0 WHERE TalepId = @id AND Sira IS NULL;
                UPDATE SiparisTalepleri SET Durum = 'onaylandi', OnaylayanId = @kid, OnayZamani = SYSDATETIME() WHERE Id = @id;
                """, new { id, kid = user.Id() });
        }
        return new OnaySonuc(yazilan, hata);
    }

    private static string KaynakAdi(Db db, string tur, string kod)
    {
        using var m = db.Mikro();
        return tur == Siparisler.Depo
            ? m.ExecuteScalar<string?>("SELECT dep_adi FROM DEPOLAR WITH (NOLOCK) WHERE dep_no = @no", new { no = int.Parse(kod) })?.Trim() ?? kod
            : m.ExecuteScalar<string?>("SELECT cari_unvan1 FROM CARI_HESAPLAR WITH (NOLOCK) WHERE cari_kod = @kod", new { kod })?.Trim()
              ?? throw new IslemHatasi($"Cari bulunamadı: {kod}");
    }
}
