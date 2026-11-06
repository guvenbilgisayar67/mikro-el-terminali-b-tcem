using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Butcem.Api.Infrastructure;
using Dapper;

namespace Butcem.Api.Features;

/// <summary>
/// Evrak fotoğrafları (alış faturası, alış irsaliyesi, satış faturası, depolar arası sevk). Mikro'ya aktarılmaz ve
/// veritabanına yazılmaz: dosya sunucudaki klasöre kaydedilir, evrak bilgisi klasör ve dosya adındadır.
///   {Fotograflar:Klasor}/{tur}/{seri}_{sira}/{yyyyMMdd-HHmmss}_{kullaniciId}_{rastgele}.jpg
/// Klasör ayarlanmamışsa ButcemApi klasörünün yanındaki ButcemFotograflari kullanılır (yükseltmede silinmesin diye).
///
/// Terminal fotoğrafı evrakla birlikte çevrimdışı biriktirebilir; o zaman evrakın seri/sırası henüz bilinmez ve
/// fotoğraf evrakın istemci kimliğiyle gelir. Evrak işlenmişse (IstemciIslemleri.Sonuc) hemen yerine konur, değilse
/// _bekleyen/{istemciId} klasöründe durur ve listeleme sırasında yerine taşınır.
/// </summary>
public static class Fotograflar
{
    public static readonly string[] Turler = ["alis_faturasi", "alis_irsaliyesi", "satis_faturasi", "sevk"];
    private const string Bekleyen = "_bekleyen";
    private const int EnBuyuk = 8 * 1024 * 1024;

    public sealed record YukleIstek(string Tur, string? Seri, int? Sira, Guid? EvrakIstemciId, string Veri);
    public sealed record YukleSonuc(string Tur, string? Seri, int? Sira, bool Bekliyor, string Dosya);
    public sealed record Foto(string Yol, DateTime Zaman, string Kullanici, long Boyut);
    public sealed record EvrakFotolari(string Tur, string Seri, int Sira, DateTime Son, List<Foto> Fotograflar);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/fotograf").RequireYetki(Yetkiler.AlisFatura, Yetkiler.SatisFatura, Yetkiler.Sevk).WithTags("Fotoğraf");

        g.MapPost("/", (YukleIstek istek, HttpRequest req, ClaimsPrincipal user, Db db, IConfiguration config, IWebHostEnvironment env) =>
            TekrarKoruma.Calistir(req, db, "fotograf", user.Id(), () => Yukle(db, Kok(config, env), user, istek)));

        // Panel: dönemdeki fotoğraflı evraklar (en yeni önce).
        g.MapGet("/", (string? tur, DateOnly? bas, DateOnly? bit, string? seri, int? sira, Db db, IConfiguration config, IWebHostEnvironment env) =>
        {
            var kok = Kok(config, env);
            Yerlestir(db, kok);
            var b = (bas ?? DateOnly.FromDateTime(DateTime.Today.AddDays(-30))).ToDateTime(TimeOnly.MinValue);
            var e = (bit ?? DateOnly.FromDateTime(DateTime.Today)).ToDateTime(TimeOnly.MaxValue);
            var adlar = KullaniciAdlari(db);
            var liste = new List<EvrakFotolari>();
            foreach (var t in Turler.Where(x => tur is null or "" || x == tur))
            {
                var tk = Path.Combine(kok, t);
                if (!Directory.Exists(tk)) continue;
                foreach (var ek in Directory.EnumerateDirectories(tk))
                {
                    var ad = Path.GetFileName(ek);
                    var i = ad.LastIndexOf('_');
                    if (i < 0 || !int.TryParse(ad[(i + 1)..], out var s)) continue;
                    var sr = ad[..i];
                    if (seri is not null && !string.Equals(sr, Guvenli(seri), StringComparison.OrdinalIgnoreCase)) continue;
                    if (sira is not null && s != sira) continue;
                    var fotolar = Directory.EnumerateFiles(ek, "*.jpg").Select(f => FotoOku(kok, f, adlar))
                                           .Where(f => f.Zaman >= b && f.Zaman <= e).OrderBy(f => f.Zaman).ToList();
                    if (fotolar.Count > 0) liste.Add(new EvrakFotolari(t, sr, s, fotolar[^1].Zaman, fotolar));
                }
            }
            return Results.Ok(liste.OrderByDescending(x => x.Son).Take(300));
        });

        g.MapGet("/dosya", (string yol, IConfiguration config, IWebHostEnvironment env) =>
        {
            var kok = Path.GetFullPath(Kok(config, env));
            var tam = Path.GetFullPath(Path.Combine(kok, yol));
            if (!tam.StartsWith(kok + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !tam.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(tam))
                return Results.NotFound();
            return Results.File(tam, "image/jpeg", enableRangeProcessing: false);
        });
    }

    public static string Kok(IConfiguration config, IWebHostEnvironment env)
    {
        var k = config["Fotograflar:Klasor"];
        if (string.IsNullOrWhiteSpace(k))
            k = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(env.ContentRootPath)) ?? env.ContentRootPath, "ButcemFotograflari");
        Directory.CreateDirectory(k);
        return k;
    }

    private static string Guvenli(string s)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(['/', '\\', '_', ' ']).ToHashSet();
        var r = new string(s.Trim().Select(c => bad.Contains(c) ? '-' : c).ToArray());
        return r.Length == 0 ? "-" : r;
    }

    private static YukleSonuc Yukle(Db db, string kok, ClaimsPrincipal user, YukleIstek istek)
    {
        if (!Turler.Contains(istek.Tur)) throw new IslemHatasi("Fotoğraf yalnız alış / satış faturası, alış irsaliyesi ve sevke eklenir.");
        byte[] veri;
        try { veri = Convert.FromBase64String(istek.Veri ?? ""); }
        catch (FormatException) { throw new IslemHatasi("Fotoğraf okunamadı."); }
        if (veri.Length < 100 || veri.Length > EnBuyuk) throw new IslemHatasi("Fotoğraf boş ya da 8 MB'tan büyük.");
        if (veri[0] != 0xFF || veri[1] != 0xD8) throw new IslemHatasi("Yalnız JPEG fotoğraf kabul edilir.");

        string klasor;
        string? seri = istek.Seri;
        int? sira = istek.Sira;
        var bekliyor = false;
        if (sira is null)
        {
            if (istek.EvrakIstemciId is not { } eid) throw new IslemHatasi("Fotoğrafın evrakı belirtilmedi.");
            (seri, sira) = EvrakNo(db, eid);
            bekliyor = sira is null;
        }
        klasor = bekliyor
            ? Path.Combine(kok, Bekleyen, istek.EvrakIstemciId!.Value.ToString("N"))
            : Path.Combine(kok, istek.Tur, $"{Guvenli(seri ?? "")}_{sira}");
        Directory.CreateDirectory(klasor);
        if (bekliyor) File.WriteAllText(Path.Combine(klasor, "tur.txt"), istek.Tur);
        var ad = $"{DateTime.Now:yyyyMMdd-HHmmss}_{user.Id()}_{Guid.NewGuid().ToString("N")[..6]}.jpg";
        File.WriteAllBytes(Path.Combine(klasor, ad), veri);
        return new YukleSonuc(istek.Tur, seri, sira, bekliyor, ad);
    }

    /// <summary>Terminalin evrak isteğinin sonucu (seri, sıra); evrak henüz işlenmemişse null.</summary>
    private static (string? Seri, int? Sira) EvrakNo(Db db, Guid istemciId)
    {
        using var conn = db.App();
        var sonuc = conn.ExecuteScalar<string?>("SELECT Sonuc FROM IstemciIslemleri WHERE IstemciId = @istemciId", new { istemciId });
        if (sonuc is null) return (null, null);
        using var j = JsonDocument.Parse(sonuc);
        var r = j.RootElement;
        return (r.TryGetProperty("seri", out var s) ? s.GetString() : "", r.TryGetProperty("sira", out var n) ? n.GetInt32() : null);
    }

    /// <summary>Bekleyen klasörlerden evrakı işlenmiş olanları yerine taşır.</summary>
    private static void Yerlestir(Db db, string kok)
    {
        var bk = Path.Combine(kok, Bekleyen);
        if (!Directory.Exists(bk)) return;
        foreach (var d in Directory.EnumerateDirectories(bk).ToList())
        {
            if (!Guid.TryParseExact(Path.GetFileName(d), "N", out var id)) continue;
            var (seri, sira) = EvrakNo(db, id);
            var turDosya = Path.Combine(d, "tur.txt");
            if (sira is null || !File.Exists(turDosya)) continue;
            var hedef = Path.Combine(kok, File.ReadAllText(turDosya).Trim(), $"{Guvenli(seri ?? "")}_{sira}");
            Directory.CreateDirectory(hedef);
            foreach (var f in Directory.EnumerateFiles(d, "*.jpg")) File.Move(f, Path.Combine(hedef, Path.GetFileName(f)), true);
            Directory.Delete(d, true);
        }
    }

    private static Dictionary<int, string> KullaniciAdlari(Db db)
    {
        using var conn = db.App();
        return conn.Query<(int Id, string Ad)>("SELECT Id, AdSoyad FROM Kullanicilar").ToDictionary(x => x.Id, x => x.Ad);
    }

    private static Foto FotoOku(string kok, string dosya, Dictionary<int, string> adlar)
    {
        var ad = Path.GetFileNameWithoutExtension(dosya).Split('_');
        var zaman = ad.Length > 0 && DateTime.TryParseExact(ad[0], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var z)
            ? z : File.GetLastWriteTime(dosya);
        var kullanici = ad.Length > 1 && int.TryParse(ad[1], out var k) ? adlar.GetValueOrDefault(k, "") : "";
        return new Foto(Path.GetRelativePath(kok, dosya).Replace('\\', '/'), zaman, kullanici, new FileInfo(dosya).Length);
    }
}
