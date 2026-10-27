using System.Data;
using System.Security.Claims;
using Butcem.Api.Infrastructure;
using Dapper;

namespace Butcem.Api.Features;

/// <summary>
/// Stok sayımı. Mağaza terminalden (ya da bilgisayardan) barkod okutup sayılan adedi girer; sayım Bütçem'in kendi
/// veritabanında birikir (ButcemApp.Sayimlar / SayimSatirlari). Merkezde sayım onay yetkisi olan kişi yanlışları düzeltir
/// ve onaylar; onayda Mikro'ya yazılır. Sayılmayan ürünlere dokunulmaz.
///
/// Mikro'nun kendi sayım akışıyla aynı (2026-05-12 ANADEPO sayımı ile karşılaştırıldı):
///  - SAYIM_SONUCLARI: sayılan miktarlar (depo + tarih için yeni evrak no).
///  - Sayım tarihindeki stoktan fazla sayılanlar: sayım giriş fişi, seri SYMG (STOK_HAREKETLERI, tip 0, cins 10, evrak tipi 12).
///  - Eksik sayılanlar: sayım çıkış fişi, seri SYMC (tip 1, cins 10, evrak tipi 0). Fark sıfırsa fiş satırı yok.
///  Tutar son alış fiyatıyla yazılır (Mikro giriş fişinde aynısını kullanıyor; çıkışta kendi maliyetini öneriyor).
/// </summary>
public static class Sayim
{
    public static class Durumlar { public const byte Acik = 0, Gonderildi = 1, Onaylandi = 2, Iptal = 3, Isleniyor = 4; }

    public sealed record Ozet(int Id, int DepoNo, DateTime Tarih, string Aciklama, byte Durum, int SatirSayisi, decimal Miktar,
                              int DuzeltilenSayisi, string Olusturan, DateTime OlusturmaZamani, DateTime? GonderimZamani,
                              DateTime? OnayZamani, string? GirisFisi, string? CikisFisi);

    public sealed record Satir(string StokKodu, string Barkod, string UrunAdi, string Birim, decimal TerminalMiktar, decimal Miktar,
                               bool Duzeltildi, string SonOkutan, DateTime SonOkutmaZamani)
    {
        // Yalnız onay yetkisi olana gösterilir: sayım tarihindeki Mikro stoğu, fark ve son alış fiyatı.
        public decimal? SistemStok { get; init; }
        public decimal? Fark { get; init; }
        public decimal? BirimMaliyet { get; init; }
    }

    public sealed record Detay(Ozet Sayim, List<Satir> Satirlar);

    public sealed record YeniIstek(int Depo, DateTime? Tarih, string? Aciklama);
    public sealed record OkutIstek(string Urun, decimal Miktar, Guid? IstemciId = null);
    public sealed record OkutSonuc(string Kod, string Ad, string Birim, decimal Miktar);
    public sealed record TopluSonuc(Guid? IstemciId, bool Tamam, string? Hata, OkutSonuc? Sonuc);
    public sealed record DuzeltIstek(string Urun, decimal? Miktar);
    public sealed record OnaySonuc(int MikroEvrakNo, string? GirisFisi, string? CikisFisi, int SatirSayisi, int FarkliSatir, decimal GirisTutar, decimal CikisTutar);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/sayim").RequireYetki(Yetkiler.Sayim, Yetkiler.SayimOnay).WithTags("Sayım");

        g.MapGet("/", (int? depo, bool? hepsi, ClaimsPrincipal user, Db db) =>
        {
            if (user.MagazaKullanicisi()) depo = user.Depo();
            using var conn = db.App();
            return Results.Ok(Liste(conn, depo, null, hepsi == true));
        });

        // Terminal açılışta devam eden sayımı buradan bulur.
        g.MapGet("/acik", (int depo, ClaimsPrincipal user, Db db) =>
        {
            user.DepoYetkisi(depo);
            using var conn = db.App();
            return Results.Ok(Liste(conn, depo, null, false).FirstOrDefault(s => s.Durum == Durumlar.Acik));
        });

        g.MapPost("/", (YeniIstek istek, ClaimsPrincipal user, Db db) =>
        {
            user.DepoYetkisi(istek.Depo);
            var tarih = (istek.Tarih ?? DateTime.Today).Date;
            if (tarih > DateTime.Today) throw new IslemHatasi("İleri tarihli sayım açılamaz.");
            using var mikro = db.Mikro();
            if (mikro.ExecuteScalar<int>("SELECT COUNT(*) FROM DEPOLAR WITH (NOLOCK) WHERE dep_no = @d", new { d = istek.Depo }) == 0)
                throw new IslemHatasi($"Depo bulunamadı: {istek.Depo}");
            using var conn = db.App();
            // Aynı depoda açık sayım varsa yenisi açılmaz; terminaller ve bilgisayar aynı sayıma okutur.
            var acik = Liste(conn, istek.Depo, null, false).FirstOrDefault(s => s.Durum == Durumlar.Acik);
            if (acik is not null) return Results.Ok(acik);
            var id = conn.ExecuteScalar<int>("""
                INSERT INTO Sayimlar (DepoNo, Tarih, Aciklama, Durum, OlusturanId) OUTPUT INSERTED.Id
                VALUES (@Depo, @tarih, @aciklama, 0, @kullanici)
                """, new { istek.Depo, tarih, aciklama = MikroEvrak.Kes(istek.Aciklama?.Trim(), 200), kullanici = user.Id() });
            return Results.Ok(Liste(conn, null, id, true).Single());
        });

        g.MapGet("/{id:int}", (int id, ClaimsPrincipal user, Db db) =>
        {
            using var conn = db.App();
            var s = Bul(conn, id, user);
            var satirlar = Satirlar(conn, id);
            if (user.HasClaim(Yetkiler.YetkiClaim, Yetkiler.SayimOnay))
            {
                if (s.Durum == Durumlar.Onaylandi)
                    satirlar = OnayliFarklar(conn, id, satirlar);
                else
                {
                    using var mikro = db.Mikro();
                    satirlar = Farklar(mikro, null, s.DepoNo, s.Tarih, satirlar);
                }
            }
            return Results.Ok(new Detay(s, satirlar));
        });

        // Okutma: miktar sayılana EKLENİR (aynı ürün iki rafta sayılabilir). Yalnız açık sayımda.
        g.MapPost("/{id:int}/okut", (int id, OkutIstek istek, ClaimsPrincipal user, Db db) =>
        {
            using var conn = db.App();
            var s = Bul(conn, id, user);
            if (s.Durum != Durumlar.Acik) throw new IslemHatasi("Bu sayım kapatılmış; okutma yapılamaz.");
            using var mikro = db.Mikro();
            return Results.Ok(Okut(conn, mikro, id, user.Id(), istek));
        });

        // Terminalin çevrimdışı biriktirdiği okutmalar. Her okutmanın terminalde üretilen kimliği vardır; bağlantı koptuğu için
        // tekrar gönderilen okutma ikinci kez eklenmez. Hatalı satır (bulunamayan barkod) diğerlerini durdurmaz.
        g.MapPost("/{id:int}/okut-toplu", (int id, List<OkutIstek> istekler, ClaimsPrincipal user, Db db) =>
        {
            if (istekler.Count > 2000) throw new IslemHatasi("Tek seferde en fazla 2000 okutma gönderilebilir.");
            using var conn = db.App();
            var s = Bul(conn, id, user);
            if (s.Durum != Durumlar.Acik) throw new IslemHatasi("Bu sayım kapatılmış; okutma yapılamaz.");
            using var mikro = db.Mikro();
            return Results.Ok(istekler.Select(i =>
            {
                try { return new TopluSonuc(i.IstemciId, true, null, Okut(conn, mikro, id, user.Id(), i)); }
                catch (IslemHatasi ex) { return new TopluSonuc(i.IstemciId, false, ex.Message, null); }
            }).ToList());
        });

        // Çevrimdışı çalışma için ürün listesi: barkod (ya da stok kodu), stok kodu, ad, birim.
        g.MapGet("/urunler", (Db db) =>
        {
            using var mikro = db.Mikro();
            var liste = mikro.Query<(string Barkod, string Kod, string Ad, string Birim)>("""
                SELECT b.bar_kodu, s.sto_kod, s.sto_isim, s.sto_birim1_ad
                FROM BARKOD_TANIMLARI b WITH (NOLOCK) JOIN STOKLAR s WITH (NOLOCK) ON s.sto_kod = b.bar_stokkodu
                WHERE ISNULL(s.sto_pasif_fl, 0) = 0
                UNION
                SELECT s.sto_kod, s.sto_kod, s.sto_isim, s.sto_birim1_ad FROM STOKLAR s WITH (NOLOCK) WHERE ISNULL(s.sto_pasif_fl, 0) = 0
                """, commandTimeout: 120);
            return Results.Ok(liste.Select(x => new[] { x.Barkod, x.Kod, x.Ad.Trim(), x.Birim }));
        });

        // Merkez düzeltmesi: ürünün sayılan miktarını doğrudan yazar; miktar boşsa ürünü sayımdan çıkarır.
        g.MapPut("/{id:int}/satir", (int id, DuzeltIstek istek, ClaimsPrincipal user, Db db) =>
        {
            OnayYetkisi(user);
            if (istek.Miktar is < 0 or > 100000) throw new IslemHatasi("Geçerli bir miktar girin.");
            using var conn = db.App();
            var s = Bul(conn, id, user);
            if (s.Durum is not (Durumlar.Acik or Durumlar.Gonderildi)) throw new IslemHatasi("Onaylanmış ya da iptal edilmiş sayım değiştirilemez.");
            using var mikro = db.Mikro();
            var stok = MikroEvrak.Stok(mikro, null, istek.Urun);
            if (istek.Miktar is null)
                conn.Execute("DELETE FROM SayimSatirlari WHERE SayimId = @id AND StokKodu = @kod", new { id, kod = stok.Kod });
            else
                conn.Execute("""
                    MERGE SayimSatirlari WITH (HOLDLOCK) AS t
                    USING (SELECT @id AS SayimId, @kod AS StokKodu) AS k ON t.SayimId = k.SayimId AND t.StokKodu = k.StokKodu
                    WHEN MATCHED THEN UPDATE SET Miktar = @miktar, DuzeltenId = @kullanici, DuzeltmeZamani = SYSDATETIME()
                    WHEN NOT MATCHED THEN INSERT (SayimId, StokKodu, Barkod, UrunAdi, Birim, TerminalMiktar, Miktar, SonOkutanId, DuzeltenId, DuzeltmeZamani)
                         VALUES (@id, @kod, @barkod, @ad, @birim, 0, @miktar, @kullanici, @kullanici, SYSDATETIME());
                    """, new { id, kod = stok.Kod, barkod = Barkod(mikro, stok.Kod), ad = stok.Ad, birim = stok.Birim, miktar = istek.Miktar, kullanici = user.Id() });
            conn.Execute("INSERT INTO SayimGirisleri (SayimId, StokKodu, Miktar, KullaniciId, Islem) VALUES (@id, @kod, @miktar, @kullanici, @islem)",
                new { id, kod = stok.Kod, miktar = istek.Miktar ?? 0, kullanici = user.Id(), islem = istek.Miktar is null ? "cikar" : "duzelt" });
            return Results.NoContent();
        });

        // Mağaza sayımı bitirir; merkeze onaya düşer.
        g.MapPost("/{id:int}/gonder", (int id, ClaimsPrincipal user, Db db, IslemLogu log) =>
        {
            using var conn = db.App();
            Bul(conn, id, user);
            if (conn.ExecuteScalar<int>("SELECT COUNT(*) FROM SayimSatirlari WHERE SayimId = @id", new { id }) == 0)
                throw new IslemHatasi("Sayımda hiç ürün yok.");
            DurumDegistir(conn, id, Durumlar.Acik, Durumlar.Gonderildi, "GonderenId = @k, GonderimZamani = SYSDATETIME()", user.Id());
            log.Yaz(user.Id(), "sayim_gonder", new { id });
            return Results.NoContent();
        });

        // Merkez sayımı mağazaya geri açar (eksik reyon sayılsın vb.).
        g.MapPost("/{id:int}/geri-ac", (int id, ClaimsPrincipal user, Db db, IslemLogu log) =>
        {
            OnayYetkisi(user);
            using var conn = db.App();
            Bul(conn, id, user);
            DurumDegistir(conn, id, Durumlar.Gonderildi, Durumlar.Acik, "GonderenId = NULL, GonderimZamani = NULL", user.Id());
            log.Yaz(user.Id(), "sayim_geri_ac", new { id });
            return Results.NoContent();
        });

        g.MapPost("/{id:int}/iptal", (int id, ClaimsPrincipal user, Db db, IslemLogu log) =>
        {
            OnayYetkisi(user);
            using var conn = db.App();
            var s = Bul(conn, id, user);
            if (s.Durum is not (Durumlar.Acik or Durumlar.Gonderildi)) throw new IslemHatasi("Bu sayım iptal edilemez.");
            DurumDegistir(conn, id, s.Durum, Durumlar.Iptal, "OnaylayanId = @k, OnayZamani = SYSDATETIME()", user.Id());
            log.Yaz(user.Id(), "sayim_iptal", new { id });
            return Results.NoContent();
        });

        // Onay: Mikro'ya sayım sonuçları ve fark fişleri yazılır, ürünlerin stoğu sayılan miktara eşitlenir.
        g.MapPost("/{id:int}/onay", (int id, ClaimsPrincipal user, Db db, MikroYazici yazici, IConfiguration config, IslemLogu log) =>
        {
            OnayYetkisi(user);
            using var conn = db.App();
            var s = Bul(conn, id, user);
            var satirlar = Satirlar(conn, id);
            if (satirlar.Count == 0) throw new IslemHatasi("Sayımda hiç ürün yok.");
            // Aynı sayım iki kez işlenmesin: önce "işleniyor"a alınır.
            DurumDegistir(conn, id, s.Durum is Durumlar.Acik or Durumlar.Gonderildi ? s.Durum : byte.MaxValue, Durumlar.Isleniyor, "", user.Id());
            try
            {
                var (sonuc, farklar) = Kaydet(yazici, config, s, satirlar);
                conn.Execute("""
                    UPDATE Sayimlar SET Durum = 2, OnaylayanId = @k, OnayZamani = SYSDATETIME(), MikroEvrakNo = @MikroEvrakNo,
                           GirisFisi = @GirisFisi, CikisFisi = @CikisFisi WHERE Id = @id;
                    """, new { id, k = user.Id(), sonuc.MikroEvrakNo, sonuc.GirisFisi, sonuc.CikisFisi });
                conn.Execute("UPDATE SayimSatirlari SET SistemStok = @SistemStok, Fark = @Fark, BirimMaliyet = @BirimMaliyet WHERE SayimId = @id AND StokKodu = @StokKodu",
                    farklar.Select(f => new { id, f.StokKodu, f.SistemStok, f.Fark, f.BirimMaliyet }));
                log.Yaz(user.Id(), "sayim_onay", new { id, sonuc });
                return Results.Ok(sonuc);
            }
            catch
            {
                conn.Execute("UPDATE Sayimlar SET Durum = @d WHERE Id = @id AND Durum = 4", new { id, d = s.Durum });
                throw;
            }
        });
    }

    private static OkutSonuc Okut(IDbConnection conn, IDbConnection mikro, int id, int kullanici, OkutIstek istek)
    {
        if (istek.Miktar == 0 || Math.Abs(istek.Miktar) > 100000) throw new IslemHatasi("Geçerli bir miktar girin.");
        var stok = MikroEvrak.Stok(mikro, null, istek.Urun);
        if (istek.IstemciId is { } ist && conn.ExecuteScalar<int>("SELECT COUNT(*) FROM SayimGirisleri WHERE IstemciId = @ist", new { ist }) > 0)
            return new OkutSonuc(stok.Kod, stok.Ad, stok.Birim,
                conn.ExecuteScalar<decimal>("SELECT Miktar FROM SayimSatirlari WHERE SayimId = @id AND StokKodu = @kod", new { id, kod = stok.Kod }));
        if (istek.Miktar < 0 && conn.ExecuteScalar<decimal?>("SELECT Miktar FROM SayimSatirlari WHERE SayimId = @id AND StokKodu = @kod",
                new { id, kod = stok.Kod }) is var mevcut && (mevcut ?? 0) + istek.Miktar < 0)
            throw new IslemHatasi($"{stok.Ad}: sayılan miktar eksiye düşemez.");
        var barkod = istek.Urun.Trim() == stok.Kod ? Barkod(mikro, stok.Kod) : istek.Urun.Trim();
        var yeni = conn.QuerySingle<decimal>("""
            SET XACT_ABORT ON;
            BEGIN TRAN;
            MERGE SayimSatirlari WITH (HOLDLOCK) AS t
            USING (SELECT @id AS SayimId, @kod AS StokKodu) AS k ON t.SayimId = k.SayimId AND t.StokKodu = k.StokKodu
            WHEN MATCHED THEN UPDATE SET TerminalMiktar = t.TerminalMiktar + @miktar,
                                         Miktar = t.Miktar + @miktar,
                                         SonOkutanId = @kullanici, SonOkutmaZamani = SYSDATETIME()
            WHEN NOT MATCHED THEN INSERT (SayimId, StokKodu, Barkod, UrunAdi, Birim, TerminalMiktar, Miktar, SonOkutanId)
                 VALUES (@id, @kod, @barkod, @ad, @birim, @miktar, @miktar, @kullanici)
            OUTPUT INSERTED.Miktar;
            INSERT INTO SayimGirisleri (SayimId, StokKodu, Miktar, KullaniciId, Islem, IstemciId) VALUES (@id, @kod, @miktar, @kullanici, 'okut', @ist);
            COMMIT;
            """, new { id, kod = stok.Kod, barkod, ad = stok.Ad, birim = stok.Birim, miktar = istek.Miktar, kullanici, ist = istek.IstemciId });
        return new OkutSonuc(stok.Kod, stok.Ad, stok.Birim, yeni);
    }

    private static void OnayYetkisi(ClaimsPrincipal user)
    {
        if (!user.HasClaim(Yetkiler.YetkiClaim, Yetkiler.SayimOnay))
            throw new IslemHatasi("Bu işlem için sayım onay yetkisi gerekir.");
    }

    private static void DurumDegistir(IDbConnection conn, int id, byte eski, byte yeni, string ek, int kullanici)
    {
        var n = conn.Execute($"UPDATE Sayimlar SET Durum = @yeni{(ek == "" ? "" : ", " + ek)} WHERE Id = @id AND Durum = @eski",
            new { id, eski, yeni, k = kullanici });
        if (n == 0) throw new IslemHatasi("Sayımın durumu değişmiş; sayfayı yenileyin.");
    }

    private static string Barkod(IDbConnection mikro, string kod) =>
        mikro.ExecuteScalar<string?>("SELECT TOP 1 bar_kodu FROM BARKOD_TANIMLARI WITH (NOLOCK) WHERE bar_stokkodu = @kod", new { kod }) ?? kod;

    private const string OzetSql = """
        SELECT s.Id, s.DepoNo, s.Tarih, ISNULL(s.Aciklama, '') AS Aciklama, s.Durum,
               COUNT(t.StokKodu) AS SatirSayisi, ISNULL(SUM(t.Miktar), 0) AS Miktar,
               SUM(CASE WHEN t.Miktar <> t.TerminalMiktar THEN 1 ELSE 0 END) AS DuzeltilenSayisi,
               ISNULL(MIN(k.AdSoyad), '') AS Olusturan, s.OlusturmaZamani, s.GonderimZamani, s.OnayZamani, s.GirisFisi, s.CikisFisi
        FROM Sayimlar s
        LEFT JOIN SayimSatirlari t ON t.SayimId = s.Id
        LEFT JOIN Kullanicilar k ON k.Id = s.OlusturanId
        """;

    private static List<Ozet> Liste(IDbConnection conn, int? depo, int? id, bool hepsi) =>
        conn.Query<Ozet>($"""
            {OzetSql}
            WHERE (@depo IS NULL OR s.DepoNo = @depo) AND (@id IS NULL OR s.Id = @id)
              AND (@hepsi = 1 OR s.Durum IN (0, 1, 4) OR s.OlusturmaZamani >= DATEADD(day, -60, SYSDATETIME()))
            GROUP BY s.Id, s.DepoNo, s.Tarih, s.Aciklama, s.Durum, s.OlusturmaZamani, s.GonderimZamani, s.OnayZamani, s.GirisFisi, s.CikisFisi
            ORDER BY CASE WHEN s.Durum IN (0, 1) THEN 0 ELSE 1 END, s.Id DESC
            """, new { depo, id, hepsi }).ToList();

    private static Ozet Bul(IDbConnection conn, int id, ClaimsPrincipal user)
    {
        var s = Liste(conn, null, id, true).SingleOrDefault() ?? throw new IslemHatasi($"Sayım bulunamadı: {id}");
        user.DepoYetkisi(s.DepoNo);
        return s;
    }

    private static List<Satir> Satirlar(IDbConnection conn, int id) =>
        conn.Query<Satir>("""
            SELECT t.StokKodu, t.Barkod, t.UrunAdi, t.Birim, t.TerminalMiktar, t.Miktar,
                   CAST(CASE WHEN t.DuzeltenId IS NULL THEN 0 ELSE 1 END AS bit) AS Duzeltildi,
                   ISNULL(k.AdSoyad, '') AS SonOkutan, t.SonOkutmaZamani
            FROM SayimSatirlari t LEFT JOIN Kullanicilar k ON k.Id = t.SonOkutanId
            WHERE t.SayimId = @id ORDER BY t.SonOkutmaZamani DESC
            """, new { id }).ToList();

    private static List<Satir> OnayliFarklar(IDbConnection conn, int id, List<Satir> satirlar)
    {
        var f = conn.Query<(string StokKodu, decimal? SistemStok, decimal? Fark, decimal? BirimMaliyet)>(
            "SELECT StokKodu, SistemStok, Fark, BirimMaliyet FROM SayimSatirlari WHERE SayimId = @id", new { id }).ToDictionary(x => x.StokKodu);
        return satirlar.Select(s => f.TryGetValue(s.StokKodu, out var x) ? s with { SistemStok = x.SistemStok, Fark = x.Fark, BirimMaliyet = x.BirimMaliyet } : s).ToList();
    }

    /// <summary>Sayım tarihi (gün sonu) itibarıyla depodaki Mikro stoğu (fn_DepodakiMiktar ile aynı kural) ve son alış fiyatı.</summary>
    public static List<Satir> Farklar(IDbConnection mikro, IDbTransaction? tx, int depo, DateTime tarih, List<Satir> satirlar)
    {
        if (satirlar.Count == 0) return satirlar;
        var d = mikro.Query<(string Kod, decimal Stok, decimal Maliyet)>("""
            SELECT k.kod,
                   CAST(ISNULL((SELECT SUM(CASE WHEN h.sth_tip = 0 OR (h.sth_tip = 2 AND h.sth_giris_depo_no = @depo) THEN h.sth_miktar ELSE -h.sth_miktar END)
                         FROM STOK_HAREKETLERI h WITH (NOLOCK)
                         WHERE h.sth_stok_kod = k.kod AND h.sth_tarih <= @tarih AND h.sth_cins NOT IN (9, 15)
                           AND ((h.sth_tip = 0 AND h.sth_giris_depo_no = @depo) OR (h.sth_tip = 1 AND h.sth_cikis_depo_no = @depo)
                             OR (h.sth_tip = 2 AND h.sth_giris_depo_no <> h.sth_cikis_depo_no
                                 AND (h.sth_giris_depo_no = @depo OR h.sth_cikis_depo_no = @depo)))), 0) AS decimal(18,3)),
                   CAST(ISNULL((SELECT TOP 1 (h.sth_tutar - h.sth_iskonto1 - h.sth_iskonto2 - h.sth_iskonto3
                                             - h.sth_iskonto4 - h.sth_iskonto5 - h.sth_iskonto6) / h.sth_miktar
                         FROM STOK_HAREKETLERI h WITH (NOLOCK)
                         WHERE h.sth_stok_kod = k.kod AND h.sth_tip = 0 AND h.sth_evraktip IN (3, 13)
                           AND h.sth_normal_iade = 0 AND h.sth_miktar > 0 AND h.sth_tarih <= @tarih
                         ORDER BY h.sth_tarih DESC, h.sth_create_date DESC), 0) AS decimal(18,6))
            FROM (SELECT DISTINCT value AS kod FROM STRING_SPLIT(@kodlar, '|')) k
            """, new { depo, tarih = tarih.Date, kodlar = string.Join('|', satirlar.Select(s => s.StokKodu)) }, tx, commandTimeout: 300)
            .ToDictionary(x => x.Kod);
        return satirlar.Select(s =>
        {
            var x = d.GetValueOrDefault(s.StokKodu);
            return s with { SistemStok = x.Stok, Fark = s.Miktar - x.Stok, BirimMaliyet = Math.Round(x.Maliyet, 6) };
        }).ToList();
    }

    public static (OnaySonuc, List<Satir>) Kaydet(MikroYazici yazici, IConfiguration config, Ozet sayim, List<Satir> satirlar)
    {
        var girisSeri = config["Sayim:GirisSerisi"] ?? "SYMG";
        var cikisSeri = config["Sayim:CikisSerisi"] ?? "SYMC";
        return yazici.Calistir((conn, tx) =>
        {
            var tarih = sayim.Tarih.Date;
            if (tarih > DateTime.Today) throw new IslemHatasi("İleri tarihli sayım onaylanamaz.");
            MikroEvrak.DepoKontrol(conn, tx, sayim.DepoNo, tarih);
            var simdi = MikroEvrak.Simdi();

            // 1. Sayım sonuçları (Mikro: Stok > Sayım sonuçları). Depo + tarih için sıradaki evrak no.
            var evrakNo = conn.ExecuteScalar<int>(
                "SELECT ISNULL(MAX(sym_evrakno), 0) + 1 FROM SAYIM_SONUCLARI WITH (UPDLOCK, HOLDLOCK) WHERE sym_tarihi = @tarih AND sym_depono = @depo",
                new { tarih, depo = sayim.DepoNo }, tx);
            var sirali = satirlar.OrderBy(s => s.SonOkutmaZamani).ToList();
            for (var i = 0; i < sirali.Count; i++)
                yazici.Ekle(conn, tx, "SAYIM_SONUCLARI", new Dictionary<string, object?>
                {
                    ["sym_Guid"] = MikroYazici.YeniGuid(),
                    ["sym_fileid"] = (short)28,
                    ["sym_create_user"] = yazici.KullaniciNo,
                    ["sym_create_date"] = simdi,
                    ["sym_lastup_user"] = yazici.KullaniciNo,
                    ["sym_lastup_date"] = simdi,
                    ["sym_tarihi"] = tarih,
                    ["sym_depono"] = sayim.DepoNo,
                    ["sym_evrakno"] = evrakNo,
                    ["sym_satirno"] = i,
                    ["sym_Stokkodu"] = sirali[i].StokKodu,
                    ["sym_reyonkodu"] = "0",
                    ["sym_koridorkodu"] = "0",
                    ["sym_rafkodu"] = "0",
                    ["sym_miktar1"] = (double)sirali[i].Miktar,
                    ["sym_birim_pntr"] = (byte)1,
                    ["sym_barkod"] = MikroEvrak.Kes(sirali[i].Barkod, 100),
                });

            // 2. Farklar: sayım tarihindeki stok, aynı işlem içinde (kilitli) okunur.
            var farklar = Farklar(conn, tx, sayim.DepoNo, tarih, sirali);
            var altKur = yazici.AlternatifKur(conn, tx);
            var srm = MikroEvrak.DepoSorumlulukMerkezi(conn, tx, sayim.DepoNo);
            string? Fis(bool giris, List<Satir> fs, out decimal toplam)
            {
                toplam = 0;
                if (fs.Count == 0) return null;
                var seri = giris ? girisSeri : cikisSeri;
                var evrakTip = giris ? Mikro.StokEvrakTip.GirisFisi : Mikro.StokEvrakTip.CikisFisi;
                var sira = yazici.SonrakiSira(conn, tx, "STOK_HAREKETLERI", "sth_evraktip", evrakTip, "sth_evrakno_seri", seri, "sth_evrakno_sira");
                for (var i = 0; i < fs.Count; i++)
                {
                    var f = fs[i];
                    var miktar = Math.Abs(f.Fark!.Value);
                    var tutar = Math.Round(f.BirimMaliyet!.Value * miktar, 4, MidpointRounding.AwayFromZero);
                    toplam += tutar;
                    yazici.Ekle(conn, tx, "STOK_HAREKETLERI", new Dictionary<string, object?>
                    {
                        ["sth_Guid"] = MikroYazici.YeniGuid(),
                        ["sth_fileid"] = (short)16,
                        ["sth_create_user"] = yazici.KullaniciNo,
                        ["sth_create_date"] = simdi,
                        ["sth_lastup_user"] = yazici.KullaniciNo,
                        ["sth_lastup_date"] = simdi,
                        ["sth_tarih"] = tarih,
                        ["sth_tip"] = (byte)(giris ? Mikro.StokHarTip.Giris : Mikro.StokHarTip.Cikis),
                        ["sth_cins"] = (byte)Mikro.StokHarCins.Sayim,
                        ["sth_normal_iade"] = (byte)0,
                        ["sth_evraktip"] = (byte)evrakTip,
                        ["sth_evrakno_seri"] = seri,
                        ["sth_evrakno_sira"] = sira,
                        ["sth_satirno"] = i,
                        ["sth_belge_tarih"] = tarih,
                        ["sth_stok_kod"] = f.StokKodu,
                        ["sth_isemri_gider_kodu"] = giris ? "1" : "2",
                        ["sth_har_doviz_kuru"] = 1d,
                        ["sth_alt_doviz_kuru"] = altKur,
                        ["sth_stok_doviz_kuru"] = 1d,
                        ["sth_miktar"] = (double)miktar,
                        ["sth_birim_pntr"] = (byte)1,
                        ["sth_tutar"] = (double)tutar,
                        ["sth_giris_depo_no"] = sayim.DepoNo,
                        ["sth_cikis_depo_no"] = sayim.DepoNo,
                        ["sth_cari_srm_merkezi"] = srm,
                        ["sth_stok_srm_merkezi"] = srm,
                        ["sth_maliyet_ana"] = (double)tutar,
                        ["sth_maliyet_alternatif"] = Math.Round((double)tutar / altKur, 12),
                        ["sth_maliyet_orjinal"] = (double)tutar,
                    });
                }
                return $"{seri} {sira}";
            }

            var girisFisi = Fis(true, farklar.Where(f => f.Fark > 0).ToList(), out var girisTutar);
            var cikisFisi = Fis(false, farklar.Where(f => f.Fark < 0).ToList(), out var cikisTutar);
            return (new OnaySonuc(evrakNo, girisFisi, cikisFisi, farklar.Count, farklar.Count(f => f.Fark != 0),
                                  Math.Round(girisTutar, 2), Math.Round(cikisTutar, 2)), farklar);
        });
    }
}
