using System.Text.Json;
using Dapper;

namespace Butcem.Api.Infrastructure;

/// <summary>Kim, ne zaman, ne yaptı. ButcemApp.IslemLog tablosuna yazar; log hatası işlemi durdurmaz.</summary>
public sealed class IslemLogu(Db db, ILogger<IslemLogu> logger)
{
    public void Yaz(int? kullaniciId, string islem, object? detay)
    {
        try
        {
            using var conn = db.App();
            conn.Execute("INSERT INTO IslemLog (KullaniciId, Islem, Detay) VALUES (@kullaniciId, @islem, @detay)",
                new { kullaniciId, islem, detay = detay is null ? null : JsonSerializer.Serialize(detay) });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "İşlem logu yazılamadı: {Islem}", islem);
        }
    }
}
