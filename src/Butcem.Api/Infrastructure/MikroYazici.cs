using System.Collections.Concurrent;
using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Butcem.Api.Infrastructure;

/// <summary>
/// Mikro'ya evrak yazar. Okuma bağlantısından (butcem_api, yazma yasak) ayrı bir SQL kullanıcısı
/// kullanır (ConnectionStrings:MikroYazma). "Yazma:Acik" false ise hiçbir şey yazılmaz.
///
/// Mikro tablolarındaki kolonlar NULL kabul eder ama Mikro'nun kendi kayıtlarında hiç NULL yoktur:
/// boş metin '', boş tarih 1899-12-30, boş GUID 000..., sayılar 0. Bu yüzden her INSERT tablonun
/// bütün kolonlarını yazar; verilmeyen kolon bu boş değerlerle doldurulur.
/// </summary>
public sealed class MikroYazici(IConfiguration config)
{
    public static readonly DateTime BosTarih = new(1899, 12, 30);

    private static readonly ConcurrentDictionary<string, IReadOnlyList<(string Ad, string Tip)>> KolonOnbellek = new();

    public bool Acik => config.GetValue("Yazma:Acik", false);

    /// <summary>Kayıtlarda oluşturan/güncelleyen olarak görünecek Mikro kullanıcı numarası.</summary>
    public short KullaniciNo => config.GetValue<short>("Yazma:MikroKullaniciNo", 1);

    /// <summary>Evrak serisi: depoya göre (Yazma:DepoSerileri:{depoNo}); tanımlı değilse boş seri.</summary>
    public string DepoSerisi(int depoNo) => config[$"Yazma:DepoSerileri:{depoNo}"] ?? "";

    public SqlConnection Baglan()
    {
        if (!Acik)
            throw new IslemHatasi("Mikro'ya yazma kapalı. Ayarlarda Yazma:Acik = true yapılmadan evrak kaydedilmez.");
        var cs = config.GetConnectionString("MikroYazma")
                 ?? throw new InvalidOperationException("ConnectionStrings:MikroYazma tanımlı değil.");
        var conn = new SqlConnection(cs);
        conn.Open();
        return conn;
    }

    /// <summary>
    /// İşlemi tek transaction içinde çalıştırır. Aynı anda Mikro'dan aynı numarayla evrak girilirse
    /// benzersiz indeks hatası alınır; numara yeniden alınıp en fazla 3 kez denenir.
    /// </summary>
    public T Calistir<T>(Func<SqlConnection, SqlTransaction, T> islem)
    {
        for (var deneme = 1; ; deneme++)
        {
            using var conn = Baglan();
            using var tx = conn.BeginTransaction(IsolationLevel.ReadCommitted);
            try
            {
                var sonuc = islem(conn, tx);
                tx.Commit();
                return sonuc;
            }
            catch (SqlException ex) when (NumaraCakismasi(ex) && deneme < 3)
            {
                tx.Rollback();
            }
        }
    }

    /// <summary>Tablonun bütün kolonlarını yazan INSERT. Verilmeyen kolonlar Mikro'nun boş değerini alır.</summary>
    public void Ekle(IDbConnection conn, IDbTransaction tx, string tablo, IReadOnlyDictionary<string, object?> degerler)
    {
        var kolonlar = Kolonlar(conn, tx, tablo);
        var bilinmeyen = degerler.Keys.Where(k => !kolonlar.Any(c => c.Ad.Equals(k, StringComparison.OrdinalIgnoreCase))).ToList();
        if (bilinmeyen.Count > 0)
            throw new InvalidOperationException($"{tablo} tablosunda olmayan kolon: {string.Join(", ", bilinmeyen)}");

        var p = new DynamicParameters();
        var adlar = new List<string>();
        var parametreler = new List<string>();
        for (var i = 0; i < kolonlar.Count; i++)
        {
            var (ad, tip) = kolonlar[i];
            var deger = degerler.FirstOrDefault(d => d.Key.Equals(ad, StringComparison.OrdinalIgnoreCase));
            adlar.Add($"[{ad}]");
            parametreler.Add($"@p{i}");
            var v = deger.Key is not null ? deger.Value ?? BosDeger(tip) : BosDeger(tip);
            // DBNull.Value: Mikro'nun kendi kaydında NULL kalan kolon (örn. yeni sürümde eklenmiş sipariş kolonları).
            p.Add($"p{i}", v is DBNull ? null : v);
        }
        conn.Execute($"INSERT INTO [{tablo}] ({string.Join(",", adlar)}) VALUES ({string.Join(",", parametreler)})", p, tx);
    }

    /// <summary>Bir sonraki evrak sıra numarası. Okunan aralık işlem sonuna kadar kilitli kalır.</summary>
    public int SonrakiSira(IDbConnection conn, IDbTransaction tx, string tablo, string tipKolon, int tip,
                           string seriKolon, string seri, string siraKolon) =>
        conn.ExecuteScalar<int>(
            $"SELECT ISNULL(MAX([{siraKolon}]), 0) + 1 FROM [{tablo}] WITH (UPDLOCK, HOLDLOCK) WHERE [{tipKolon}] = @tip AND [{seriKolon}] = @seri",
            new { tip, seri }, tx);

    /// <summary>Mikro'nun alternatif döviz kuru. Mikro ekranından girilen son kayıttaki değer kullanılır.</summary>
    public double AlternatifKur(IDbConnection conn, IDbTransaction tx) =>
        config.GetValue<double?>("Yazma:AlternatifKur")
        ?? conn.ExecuteScalar<double?>(
            "SELECT TOP 1 cha_altd_kur FROM CARI_HESAP_HAREKETLERI WITH (NOLOCK) WHERE cha_altd_kur NOT IN (0, 1) ORDER BY cha_create_date DESC",
            transaction: tx)
        ?? 1;

    /// <summary>Mikro'nun ürettiği GUID'ler sıralı; biz SQL Server'ın sıralı GUID'ine yakın bir değer üretiriz.</summary>
    public static Guid YeniGuid() => Guid.NewGuid();

    /// <summary>Benzersiz indeks çakışması (aynı anda Mikro'dan aynı numarayla evrak girildi).</summary>
    public static bool NumaraCakismasi(SqlException ex) => ex.Number is 2601 or 2627;

    private static IReadOnlyList<(string Ad, string Tip)> Kolonlar(IDbConnection conn, IDbTransaction tx, string tablo) =>
        KolonOnbellek.GetOrAdd(tablo, t => conn.Query<(string, string)>("""
            SELECT c.name, TYPE_NAME(c.system_type_id)
            FROM sys.columns c
            WHERE c.object_id = OBJECT_ID(@t) AND c.is_computed = 0 AND c.is_identity = 0
            ORDER BY c.column_id
            """, new { t }, tx).ToList());

    private static object BosDeger(string tip) => tip switch
    {
        "nvarchar" or "varchar" or "nchar" or "char" or "ntext" or "text" => "",
        "datetime" or "smalldatetime" or "datetime2" or "date" => BosTarih,
        "uniqueidentifier" => Guid.Empty,
        "bit" => false,
        "float" or "real" => 0d,
        "decimal" or "numeric" or "money" or "smallmoney" => 0m,
        "tinyint" => (byte)0,
        "smallint" => (short)0,
        "bigint" => 0L,
        "int" => 0,
        "varbinary" or "binary" or "image" => Array.Empty<byte>(),
        _ => throw new InvalidOperationException($"Desteklenmeyen kolon tipi: {tip}"),
    };
}

/// <summary>Kullanıcıya gösterilecek iş kuralı hatası (400 döner).</summary>
public sealed class IslemHatasi(string mesaj) : Exception(mesaj);
