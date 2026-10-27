import { useState } from "react";
import { ayBasi, bugun, csvIndir, miktar, para, qs } from "../api";
import { Alan, Durum, Kart, useVeri } from "../ortak";

type Satir = { anahtar: string; ad: string; miktar: number; netSatis: number; maliyet: number; kar: number; karOrani: number | null; haricSatis: number };
type Kontrol = { stokKodu: string; urunAdi: string; miktar: number; netSatis: number; birimSatis: number | null; birimMaliyet: number | null; sebep: string };
type Sonuc = { netSatis: number; maliyet: number; kar: number; karOrani: number | null; haricSatis: number; satirlar: Satir[]; kontroller: Kontrol[] };
type Depo = { no: number; ad: string };

const KIRILIMLAR: [string, string][] = [["Anagrup", "Ana grup"], ["Reyon", "Reyon"], ["Marka", "Marka"], ["Urun", "Ürün"], ["Depo", "Mağaza"], ["Gun", "Gün"]];
const oran = (n: number | null) => (n == null ? "" : `%${n.toLocaleString("tr-TR", { minimumFractionDigits: 1, maximumFractionDigits: 1 })}`);

/** Kâr oranı çubuğu: düşük kâr kırmızı, iyi kâr yeşil. */
function OranCubugu({ deger }: { deger: number | null }) {
  if (deger == null) return null;
  const g = Math.max(0, Math.min(100, deger * 2));   // %50 kâr = tam çubuk
  return (
    <span className="oran">
      <span className={"oran-dolu" + (deger < 10 ? " dusuk" : deger < 20 ? " orta" : "")} style={{ width: `${g}%` }} />
    </span>
  );
}

export default function Karlilik({ depoKilitli }: { depoKilitli: boolean }) {
  const [f, setF] = useState({ bas: ayBasi(), bit: bugun(), kirilim: "Anagrup", tur: "Hepsi", depo: "" });
  const [sorgu, setSorgu] = useState(f);
  const [kontrolAcik, setKontrolAcik] = useState(false);
  const depolar = useVeri<Depo[]>("/api/depolar");
  const { veri, hata, yukleniyor } = useVeri<Sonuc>(`/api/karlilik?${qs({ ...sorgu, ilk: sorgu.kirilim === "Urun" ? 1000 : undefined })}`);
  const kirilimAdi = KIRILIMLAR.find((k) => k[0] === sorgu.kirilim)?.[1] ?? "";

  return (
    <section>
      <header className="sayfa-baslik">
        <h1>Karlılık analizi</h1>
        <form className="filtre" onSubmit={(e) => { e.preventDefault(); setSorgu(f); }}>
          <Alan etiket="Başlangıç"><input type="date" value={f.bas} onChange={(e) => setF({ ...f, bas: e.target.value })} /></Alan>
          <Alan etiket="Bitiş"><input type="date" value={f.bit} onChange={(e) => setF({ ...f, bit: e.target.value })} /></Alan>
          <Alan etiket="Kırılım">
            <select value={f.kirilim} onChange={(e) => setF({ ...f, kirilim: e.target.value })}>
              {KIRILIMLAR.map(([k, a]) => <option key={k} value={k}>{a}</option>)}
            </select>
          </Alan>
          <Alan etiket="Tür">
            <select value={f.tur} onChange={(e) => setF({ ...f, tur: e.target.value })}>
              <option value="Hepsi">Hepsi</option><option value="Perakende">Perakende (kasa)</option><option value="Toptan">Toptan (fatura)</option>
            </select>
          </Alan>
          {!depoKilitli && (
            <Alan etiket="Mağaza">
              <select value={f.depo} onChange={(e) => setF({ ...f, depo: e.target.value })}>
                <option value="">Tümü</option>
                {depolar.veri?.map((d) => <option key={d.no} value={d.no}>{d.ad}</option>)}
              </select>
            </Alan>
          )}
          <button type="submit">Getir</button>
        </form>
      </header>
      <p className="not">Satış KDV hariç, iskontolar ve iadeler düşülmüş. Maliyet: ürünün satıldığı gün geçerli son alış fiyatı (iskontolu, KDV hariç).</p>
      <Durum hata={hata} yukleniyor={yukleniyor} />
      {veri && (
        <>
          <div className="kartlar">
            <Kart baslik="Net satış" deger={`${para(veri.netSatis)} ₺`} />
            <Kart baslik="Maliyet" deger={`${para(veri.maliyet)} ₺`} />
            <Kart baslik="Brüt kâr" deger={`${para(veri.kar)} ₺`} />
            <Kart baslik="Kâr oranı (satışa göre)" deger={oran(veri.karOrani)} />
          </div>
          {veri.haricSatis !== 0 && (
            <div className="uyari">
              {para(veri.haricSatis)} ₺ satışın maliyeti bilinmediği ya da alış fiyatı tutarsız olduğu için kâra katılmadı.{" "}
              <button className="ikincil" onClick={() => setKontrolAcik(!kontrolAcik)}>{kontrolAcik ? "Gizle" : `Ürünleri göster (${veri.kontroller.length})`}</button>
            </div>
          )}
          {kontrolAcik && (
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
          )}
          <div className="arac">
            <button className="ikincil" onClick={() => csvIndir(`karlilik_${sorgu.kirilim}_${sorgu.bas}_${sorgu.bit}.csv`,
              [kirilimAdi, "Kod", "Miktar", "Net satış", "Maliyet", "Kâr", "Kâr oranı %", "Kâra katılmayan satış"],
              veri.satirlar.map((r) => [r.ad, r.anahtar, r.miktar, r.netSatis, r.maliyet, r.kar, r.karOrani, r.haricSatis]))}>
              Excel'e aktar
            </button>
          </div>
          <table>
            <thead><tr><th>{kirilimAdi}</th><th className="s">Miktar</th><th className="s">Net satış</th><th className="s">Maliyet</th><th className="s">Kâr</th><th className="s">Kâr oranı</th><th></th><th className="s">Kâr payı</th></tr></thead>
            <tbody>
              {veri.satirlar.map((r) => (
                <tr key={r.anahtar}>
                  <td>{r.ad}</td><td className="s">{miktar(r.miktar)}</td><td className="s">{para(r.netSatis)}</td><td className="s">{para(r.maliyet)}</td>
                  <td className={"s b" + (r.kar < 0 ? " eksi" : "")}>{para(r.kar)}</td>
                  <td className={"s" + (r.karOrani != null && r.karOrani < 0 ? " eksi" : "")}>{oran(r.karOrani)}</td>
                  <td style={{ width: 90 }}><OranCubugu deger={r.karOrani} /></td>
                  <td className="s">{veri.kar ? ((r.kar / veri.kar) * 100).toFixed(1) + "%" : ""}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}
    </section>
  );
}
