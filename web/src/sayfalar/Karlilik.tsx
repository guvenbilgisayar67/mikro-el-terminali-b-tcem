import { useState } from "react";
import { ayBasi, bugun, csvIndir, gunEkle, isoGun, miktar, para, paraTam, qs } from "../api";
import { Cipler, Durum, Kart, oranSinifi, useVeri, yuzde } from "../ortak";

type Satir = { anahtar: string; ad: string; miktar: number; netSatis: number; maliyet: number; kar: number; karOrani: number | null; haricSatis: number };
type Kontrol = { stokKodu: string; urunAdi: string; miktar: number; netSatis: number; birimSatis: number | null; birimMaliyet: number | null; sebep: string };
type Sonuc = { netSatis: number; maliyet: number; kar: number; karOrani: number | null; haricSatis: number; satirlar: Satir[]; kontroller: Kontrol[] };
type Depo = { no: number; ad: string };

type Kirilim = "Anagrup" | "Reyon" | "Marka" | "Urun" | "Depo" | "Gun";
const KIRILIMLAR: [Kirilim, string][] = [["Anagrup", "Ana grup"], ["Reyon", "Reyon"], ["Marka", "Marka"], ["Urun", "Ürün"], ["Depo", "Mağaza"], ["Gun", "Gün"]];

type Donem = "bugun" | "hafta" | "ay" | "gecenay";
const DONEMLER: [Donem, string][] = [["bugun", "Bugün"], ["hafta", "Bu hafta"], ["ay", "Bu ay"], ["gecenay", "Geçen ay"]];
function donemAraligi(d: Donem): { bas: string; bit: string } {
  const b = bugun();
  const simdi = new Date();
  if (d === "bugun") return { bas: b, bit: b };
  if (d === "hafta") return { bas: gunEkle(b, -((simdi.getDay() + 6) % 7)), bit: b };   // pazartesiden bugüne
  if (d === "ay") return { bas: ayBasi(), bit: b };
  return { bas: isoGun(new Date(simdi.getFullYear(), simdi.getMonth() - 1, 1)), bit: isoGun(new Date(simdi.getFullYear(), simdi.getMonth(), 0)) };
}

/** Kâr oranı çubuğu: düşük kâr kırmızı, iyi kâr yeşil (%50 kâr = tam çubuk). */
function OranCubugu({ deger }: { deger: number | null }) {
  if (deger == null) return null;
  return <span className="cubuk"><i className={oranSinifi(deger)} style={{ width: `${Math.max(0, Math.min(100, deger * 2))}%` }} /></span>;
}

export default function Karlilik({ depoKilitli }: { depoKilitli: boolean }) {
  const [f, setF] = useState({ ...donemAraligi("ay"), kirilim: "Anagrup" as Kirilim, tur: "Hepsi", depo: "" });
  const [donem, setDonem] = useState<Donem | null>("ay");
  const [kontrolAcik, setKontrolAcik] = useState(false);
  const depolar = useVeri<Depo[]>("/api/depolar");
  const { veri, hata, yukleniyor } = useVeri<Sonuc>(`/api/karlilik?${qs({ ...f, ilk: f.kirilim === "Urun" ? 1000 : undefined })}`);
  const kirilimAdi = KIRILIMLAR.find((k) => k[0] === f.kirilim)?.[1] ?? "";
  // Grafikte en çok kâr getiren 10 satır (gün kırılımında tarih sırası).
  const grafik = veri ? (f.kirilim === "Gun" ? veri.satirlar.slice(-14) : veri.satirlar.slice().sort((a, b) => b.kar - a.kar).slice(0, 10)) : [];
  const enCok = Math.max(...grafik.map((r) => Math.abs(r.kar)), 1);

  return (
    <section>
      <header className="sayfa-baslik">
        <h1>Karlılık analizi</h1>
        <div className="filtre">
          <Cipler<Donem> secenekler={DONEMLER} deger={donem} degisti={(d) => { setDonem(d); setF({ ...f, ...donemAraligi(d) }); }} />
          <input type="date" aria-label="Başlangıç" value={f.bas} onChange={(e) => { setDonem(null); setF({ ...f, bas: e.target.value }); }} />
          <input type="date" aria-label="Bitiş" value={f.bit} onChange={(e) => { setDonem(null); setF({ ...f, bit: e.target.value }); }} />
          {!depoKilitli && (
            <select aria-label="Mağaza" value={f.depo} onChange={(e) => setF({ ...f, depo: e.target.value })}>
              <option value="">Mağaza: Tümü</option>
              {depolar.veri?.map((d) => <option key={d.no} value={d.no}>{d.ad}</option>)}
            </select>
          )}
          <select aria-label="Tür" value={f.tur} onChange={(e) => setF({ ...f, tur: e.target.value })}>
            <option value="Hepsi">Tür: Hepsi</option><option value="Perakende">Perakende (kasa)</option><option value="Toptan">Toptan (fatura)</option>
          </select>
          {veri && (
            <button className="ikincil" onClick={() => csvIndir(`karlilik_${f.kirilim}_${f.bas}_${f.bit}.csv`,
              [kirilimAdi, "Kod", "Miktar", "Net satış", "Maliyet", "Kâr", "Kâr oranı %", "Kâra katılmayan satış"],
              veri.satirlar.map((r) => [r.ad, r.anahtar, r.miktar, r.netSatis, r.maliyet, r.kar, r.karOrani, r.haricSatis]))}>
              Excel'e aktar
            </button>
          )}
        </div>
      </header>
      <Durum hata={hata} yukleniyor={yukleniyor} />
      {veri && (
        <>
          <div className="kartlar">
            <Kart baslik="Net satış (KDV hariç)" deger={`${paraTam(veri.netSatis)} ₺`} alt="iskonto ve iadeler düşülmüş" />
            <Kart baslik="Maliyet (son alış)" deger={`${paraTam(veri.maliyet)} ₺`} alt="satış günündeki son alış fiyatı" />
            <Kart baslik="Brüt kâr" deger={`${paraTam(veri.kar)} ₺`} degerSinif={veri.kar < 0 ? "eksi" : "yesil"} />
            <Kart baslik="Kâr oranı (satışa göre)" deger={yuzde(veri.karOrani)} alt={<OranCubugu deger={veri.karOrani} />} />
          </div>
          {veri.haricSatis !== 0 && (
            <div className="uyari dikkat">
              <span className="pill turuncu">Dikkat</span>
              <span>{paraTam(veri.haricSatis)} ₺ satışın maliyeti bilinmiyor ya da alışı koli / kasa ile girilmiş; kâra katılmadı.</span>
              <button className="ikincil" onClick={() => setKontrolAcik(!kontrolAcik)}>{kontrolAcik ? "Gizle" : `${veri.kontroller.length} ürünü göster`}</button>
            </div>
          )}
          {kontrolAcik && (
            <div className="blok">
              <table className="ic">
                <thead><tr><th>Ürün</th><th>Kod</th><th className="s">Miktar</th><th className="s">Net satış</th><th className="s">Birim satış</th><th className="s">Son alış</th><th>Sebep</th></tr></thead>
                <tbody>
                  {veri.kontroller.map((k) => (
                    <tr key={k.stokKodu}>
                      <td>{k.urunAdi}</td><td>{k.stokKodu}</td><td className="s">{miktar(k.miktar)}</td><td className="s">{para(k.netSatis)}</td>
                      <td className="s">{para(k.birimSatis)}</td><td className="s">{para(k.birimMaliyet)}</td><td className="soluk">{k.sebep}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          <div className="izgara esit">
            <div className="blok">
              <div className="blok-baslik">
                <h3>{f.kirilim === "Gun" ? "Günlere göre kâr" : `${kirilimAdi} bazında kâr`} <small>· {f.kirilim === "Gun" ? "son 14 gün" : "ilk 10"}</small></h3>
                <Cipler<Kirilim> secenekler={KIRILIMLAR} deger={f.kirilim} degisti={(k) => setF({ ...f, kirilim: k })} />
              </div>
              {grafik.length === 0 && <div className="bilgi">Bu dönemde satış yok.</div>}
              <div className="yatay-grafik">
                {grafik.map((r) => (
                  <div className="yatay-satir" key={r.anahtar} title={r.ad}>
                    <b>{r.ad}</b>
                    <span className="cizgi">
                      <i className={r.kar < 0 ? "dusuk" : oranSinifi(r.karOrani)} style={{ width: `${(Math.abs(r.kar) / enCok) * 62}%` }} />
                      <span>{kisaTutar(r.kar)} · {yuzde(r.karOrani)}</span>
                    </span>
                  </div>
                ))}
              </div>
            </div>
            <div className="blok">
              <table>
                <thead><tr><th>{kirilimAdi}</th>{f.kirilim === "Urun" && <th className="s">Miktar</th>}<th className="s">Net satış</th><th className="s">Maliyet</th><th className="s">Kâr</th><th className="s">Oran</th><th className="s">Kâr payı</th></tr></thead>
                <tbody>
                  {veri.satirlar.map((r) => (
                    <tr key={r.anahtar} title={`Miktar: ${miktar(r.miktar)}${r.haricSatis ? ` · kâra katılmayan satış ${para(r.haricSatis)} ₺` : ""}`}>
                      <td className="b">{r.ad}</td>{f.kirilim === "Urun" && <td className="s">{miktar(r.miktar)}</td>}<td className="s">{paraTam(r.netSatis)}</td><td className="s">{paraTam(r.maliyet)}</td>
                      <td className={"s b" + (r.kar < 0 ? " eksi" : "")}>{paraTam(r.kar)}</td>
                      <td className="s">{r.karOrani != null && <span className={"pill " + (r.karOrani < 10 ? "kirmizi" : r.karOrani < 20 ? "turuncu" : "yesil")}>{yuzde(r.karOrani)}</span>}</td>
                      <td className="s">{veri.kar ? yuzde((r.kar / veri.kar) * 100) : ""}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
          <p className="not">Satış KDV hariç, iskontolar ve iadeler düşülmüş. Maliyet: ürünün satıldığı gün geçerli son alış fiyatı (iskontolu, KDV hariç). Satırın üzerine gelince miktar görünür.</p>
        </>
      )}
    </section>
  );
}

const kisaTutar = (n: number) => (Math.abs(n) >= 1e6 ? `${(n / 1e6).toLocaleString("tr-TR", { maximumFractionDigits: 2 })} mn ₺` : Math.abs(n) >= 1e4 ? `${paraTam(n / 1000)} bin ₺` : `${paraTam(n)} ₺`);
