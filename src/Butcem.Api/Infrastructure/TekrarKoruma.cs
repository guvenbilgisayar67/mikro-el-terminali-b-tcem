using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Butcem.Api.Infrastructure;

/// <summary>
/// Terminal çevrimdışıyken biriktirdiği işlemi bağlantı gelince yeniden gönderir; cevap yolda kaybolursa bir kez daha
/// gönderebilir. İstek "X-Istemci-Id" başlığıyla gelirse işlem bu kimlikle bir kez yapılır, sonraki gönderimlere
/// ilk sonucun aynısı döner. Başlık yoksa (panel) işlem her zamanki gibi çalışır.
/// </summary>
public static class TekrarKoruma
{
    public const string Baslik = "X-Istemci-Id";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IResult Calistir<T>(HttpRequest req, Db db, string islem, int kullaniciId, Func<T> islemiYap)
    {
        if (!Guid.TryParse(req.Headers[Baslik].ToString(), out var id)) return Results.Ok(islemiYap());

        using var conn = db.App();
        try
        {
            conn.Execute("INSERT INTO IstemciIslemleri (IstemciId, Islem, KullaniciId) VALUES (@id, @islem, @kullaniciId)",
                         new { id, islem, kullaniciId });
        }
        catch (SqlException ex) when (MikroYazici.NumaraCakismasi(ex))
        {
            var onceki = conn.QuerySingle<(string Islem, string? Sonuc)>(
                "SELECT Islem, Sonuc FROM IstemciIslemleri WHERE IstemciId = @id", new { id });
            if (onceki.Islem != islem) throw new IslemHatasi("Bu işlem kimliği başka bir işlemde kullanılmış.");
            if (onceki.Sonuc is null)
                return Results.Json(new { hata = "Bu işlem daha önce gönderildi ve henüz sonuçlanmadı. Birkaç dakika sonra panelden kontrol edin." },
                                    statusCode: StatusCodes.Status409Conflict);
            req.HttpContext.Response.Headers["X-Tekrar"] = "1";
            return Results.Text(onceki.Sonuc, "application/json; charset=utf-8");
        }

        T sonuc;
        try { sonuc = islemiYap(); }
        catch
        {
            // İşlem yapılmadı (doğrulama hatası, kilitli depo...): kayıt silinir, düzeltilip tekrar gönderilebilir.
            conn.Execute("DELETE FROM IstemciIslemleri WHERE IstemciId = @id AND Sonuc IS NULL", new { id });
            throw;
        }
        conn.Execute("UPDATE IstemciIslemleri SET Sonuc = @s, BitisZamani = SYSDATETIME() WHERE IstemciId = @id",
                     new { id, s = JsonSerializer.Serialize(sonuc, Json) });
        return Results.Ok(sonuc);
    }
}
