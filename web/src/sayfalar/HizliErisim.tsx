import { Oturum, yetkili } from "../api";

/**
 * Mikro'daki "Hızlı Erişim" listesinin paneldeki karşılığı: her Mikro programı için panelde açılacak ekran.
 * hedef boşsa ekran henüz panelde yok (hazırlanıyor).
 */
type Program = { ad: string; kod: string; tip: "Evrak" | "Föy"; hedef?: string; yetki: string[]; not?: string };

const GRUPLAR: { ad: string; programlar: Program[] }[] = [
  {
    ad: "",
    programlar: [
      { ad: "Alış Faturası", kod: "062205", tip: "Evrak", hedef: "fatura/alis", yetki: ["alis_fatura"] },
      { ad: "Tediye Makbuzu", kod: "042210", tip: "Evrak", yetki: [], not: "Hazırlanıyor" },
      { ad: "Cari Hesap Hareket Föyü", kod: "041410", tip: "Föy", hedef: "cari", yetki: ["cari_ekstre"], not: "Cari ekstre ekranı" },
      { ad: "Alış Faturaları Yönetimi", kod: "028012", tip: "Evrak", hedef: "fatura/alisfaturalari", yetki: ["alis_fatura"] },
      { ad: "Çek Çıkış Bordrosu", kod: "072410", tip: "Evrak", hedef: "cekislem", yetki: ["cek_cikis"], not: "Çek giriş / çıkış ekranı" },
    ],
  },
  {
    ad: "Kasa",
    programlar: [
      { ad: "Kasa Tahsilat Fişi", kod: "051100", tip: "Evrak", yetki: [], not: "Hazırlanıyor" },
      { ad: "Kasadan Bankaya Nakit Yatırma Makbuzu", kod: "072520", tip: "Evrak", yetki: [], not: "Hazırlanıyor" },
      { ad: "Kasa Ödeme Fişi", kod: "051200", tip: "Evrak", yetki: [], not: "Hazırlanıyor" },
      { ad: "Kasa Hareket Föyü", kod: "051400", tip: "Föy", yetki: [], not: "Hazırlanıyor" },
    ],
  },
  {
    ad: "Personel",
    programlar: [
      { ad: "Fiyat Değişikliği Fişi", kod: "031200", tip: "Evrak", hedef: "fiyat", yetki: ["fiyat_degisikligi"] },
      { ad: "Cari Personel Hareket Föyü", kod: "041436", tip: "Föy", yetki: [], not: "Hazırlanıyor" },
    ],
  },
];

export default function HizliErisim({ oturum }: { oturum: Oturum }) {
  return (
    <section>
      <header className="sayfa-baslik"><h1>Mikro hızlı erişim</h1></header>
      <p className="not">Mikro'daki Hızlı Erişim listeniz. Paneldeki karşılığı olan programlar "Aç" ile açılır.</p>
      <table>
        <thead><tr><th>Program adı</th><th>Program ID</th><th>Tipi</th><th>Paneldeki ekran</th><th></th></tr></thead>
        <tbody>
          {GRUPLAR.map((g) => [
            g.ad && <tr key={"g" + g.ad}><td colSpan={5}><b>{g.ad.toLocaleUpperCase("tr-TR")}</b></td></tr>,
            ...g.programlar.map((p) => {
              const izin = p.hedef && yetkili(oturum, ...p.yetki);
              return (
                <tr key={p.kod}>
                  <td>{p.ad} ({p.kod})</td><td>{+p.kod}</td><td>{p.tip}</td>
                  <td className="not">{p.not ?? ""}{p.hedef && !izin ? " (yetkiniz yok)" : ""}</td>
                  <td>{izin && <button onClick={() => { location.hash = p.hedef!; }}>Aç</button>}</td>
                </tr>
              );
            }),
          ])}
        </tbody>
      </table>
    </section>
  );
}
