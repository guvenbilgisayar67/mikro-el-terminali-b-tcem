using System.Net.Http.Json;

namespace Butcem.Yazdirma;

public sealed record AjanAyarlari(string Sunucu, string Ad, string Anahtar, int AralikSaniye = 3);

public sealed record AjanIsi(long Id, string WindowsYaziciAdi, string Dil, string Icerik);

/// <summary>Sunucudaki etiket kuyruğunu dinler, gelen işi mağazadaki USB yazıcıya basar, sonucu bildirir.</summary>
public sealed class Ajan(AjanAyarlari ayar, ILogger<Ajan> log) : BackgroundService
{
    private readonly HttpClient http = YeniIstemci(ayar);

    private static HttpClient YeniIstemci(AjanAyarlari a)
    {
        var c = new HttpClient { BaseAddress = new Uri(a.Sunucu.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.Add("X-Ajan", a.Ad);
        c.DefaultRequestHeaders.Add("X-Anahtar", a.Anahtar);
        return c;
    }

    protected override async Task ExecuteAsync(CancellationToken iptal)
    {
        log.LogInformation("Yazdırma ajanı başladı: {Ad} -> {Sunucu}", ayar.Ad, ayar.Sunucu);
        var bekleme = TimeSpan.FromSeconds(Math.Max(1, ayar.AralikSaniye));
        while (!iptal.IsCancellationRequested)
        {
            try
            {
                var isler = await http.GetFromJsonAsync<List<AjanIsi>>("api/ajan/isler", iptal) ?? [];
                foreach (var isi in isler) await Isle(isi, iptal);
                bekleme = TimeSpan.FromSeconds(Math.Max(1, ayar.AralikSaniye));
            }
            catch (OperationCanceledException) when (iptal.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Sunucuya ulaşılamıyorsa (VPN düştü vb.) bekleme süresi 1 dakikaya kadar uzar.
                log.LogWarning("Sunucuya ulaşılamadı: {Hata}", ex.Message);
                bekleme = TimeSpan.FromSeconds(Math.Min(60, bekleme.TotalSeconds * 2));
            }
            await Task.Delay(bekleme, iptal).ContinueWith(_ => { });
        }
    }

    private async Task Isle(AjanIsi isi, CancellationToken iptal)
    {
        string? hata = null;
        try
        {
            var veri = Convert.FromBase64String(isi.Icerik);
            if (isi.Dil == "PDF") A4Yazici.Bas(isi.WindowsYaziciAdi, veri);
            else HamYazici.Gonder(isi.WindowsYaziciAdi, veri);
            log.LogInformation("İş {Id} basıldı ({Yazici})", isi.Id, isi.WindowsYaziciAdi);
        }
        catch (Exception ex)
        {
            hata = ex.Message;
            log.LogError("İş {Id} basılamadı ({Yazici}): {Hata}", isi.Id, isi.WindowsYaziciAdi, ex.Message);
        }
        using var cevap = await http.PostAsJsonAsync($"api/ajan/isler/{isi.Id}/sonuc",
            new { basarili = hata is null, hata }, iptal);
        cevap.EnsureSuccessStatusCode();
    }
}
