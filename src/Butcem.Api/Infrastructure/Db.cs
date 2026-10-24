using System.Data;
using Microsoft.Data.SqlClient;

namespace Butcem.Api.Infrastructure;

public sealed record OrionKaynagi(string Ad, string Baglanti);

/// <summary>
/// Bağlantı üretir:
///  - Mikro: firma veritabanı (MikroDB_V16_xxx). Okuma ve kontrollü yazma.
///  - Orion: BarPOS veritabanları (merkez + şubeler). Sadece okuma. Şube kasalarının gün içi verisi
///    önce şubenin kendi Orion veritabanına düşer, merkeze sonra gelir; bu yüzden birden fazla kaynak olabilir.
///  - App:   uygulamanın kendi veritabanı (kullanıcılar, yazıcılar, etiket işleri, log).
/// </summary>
public sealed class Db(IConfiguration config)
{
    public IDbConnection Mikro() => Open(Cs("Mikro"));
    public IDbConnection App() => Open(Cs("App"));

    public IReadOnlyList<OrionKaynagi> OrionKaynaklari =>
        config.GetSection("Orion:Kaynaklar").Get<List<OrionKaynagi>>() ?? [];

    public IDbConnection Orion(OrionKaynagi kaynak) => Open(kaynak.Baglanti);

    private string Cs(string name) => config.GetConnectionString(name)
        ?? throw new InvalidOperationException($"ConnectionStrings:{name} tanımlı değil.");

    private static SqlConnection Open(string cs)
    {
        var conn = new SqlConnection(cs);
        conn.Open();
        return conn;
    }
}
