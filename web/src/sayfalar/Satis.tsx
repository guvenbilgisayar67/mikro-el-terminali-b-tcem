import { useState } from "react";
import { ayBasi, bugun, csvIndir, miktar, para, qs } from "../api";
import { Alan, Durum, Kart, useVeri } from "../ortak";

type Satir = { anahtar: string; ad: string; faturaSayisi: number; miktar: number; netTutar: number; kdv: number; toplam: number; iadeToplam: number };
type Sonuc = { baslangic: string; bitis: string; kirilim: string; genelToplam: number; genelKdv: number; satirlar: Satir[] };
type Depo = { no: number; ad: string };

const KIRILIMLAR: [string, string][] = [["Gun", "Gün"], ["Depo", "Mağaza"], ["Kasa", "Kasa"], ["Anagrup", "Ana grup"], ["Reyon", "Reyon"], ["Marka", "Marka"], ["Urun", "Ürün"]];

export default function Satis({ depoKilitli }: { depoKilitli: boolean }) {
  const [f, setF] = useState({ bas: ayBasi(), bit: bugun(), kirilim: "Gun", tur: "Hepsi", depo: "" });
  const [sorgu, setSorgu] = useState(f);
  const depolar = useVeri<Depo[]>("/api/depolar");
  const { veri, hata, yukleniyor } = useVeri<Sonuc>(`/api/satis?${qs({ ...sorgu, ilk: sorgu.kirilim === "Urun" ? 500 : undefined })}`);
  const kirilimAdi = KIRILIMLAR.find((k) => k[0] === sorgu.kirilim)?.[1] ?? "";

  return (
    <section>
      <header className="sayfa-baslik">
        <h1>Satış raporu</h1>
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
      <Durum hata={hata} yukleniyor={yukleniyor} />
      {veri && (
        <>
          <div className="kartlar">
            <Kart baslik="KDV dahil toplam" deger={`${para(veri.genelToplam)} ₺`} />
            <Kart baslik="KDV" deger={`${para(veri.genelKdv)} ₺`} />
            <Kart baslik="KDV hariç" deger={`${para(veri.genelToplam - veri.genelKdv)} ₺`} />
            <Kart baslik="İade" deger={`${para(veri.satirlar.reduce((s, r) => s + r.iadeToplam, 0))} ₺`} />
          </div>
          <div className="arac">
            <button className="ikincil" onClick={() => csvIndir(`satis_${sorgu.kirilim}_${sorgu.bas}_${sorgu.bit}.csv`,
              [kirilimAdi, "Kod", "Belge", "Miktar", "Net", "KDV", "Toplam", "İade"],
              veri.satirlar.map((r) => [r.ad, r.anahtar, r.faturaSayisi, r.miktar, r.netTutar, r.kdv, r.toplam, r.iadeToplam]))}>
              Excel'e aktar
            </button>
          </div>
          <table>
            <thead><tr><th>{kirilimAdi}</th><th className="s">Belge</th><th className="s">Miktar</th><th className="s">Net</th><th className="s">KDV</th><th className="s">Toplam</th><th className="s">İade</th><th className="s">Pay</th></tr></thead>
            <tbody>
              {veri.satirlar.map((r) => (
                <tr key={r.anahtar}>
                  <td>{r.ad}</td><td className="s">{r.faturaSayisi}</td><td className="s">{miktar(r.miktar)}</td>
                  <td className="s">{para(r.netTutar)}</td><td className="s">{para(r.kdv)}</td><td className="s b">{para(r.toplam)}</td>
                  <td className="s">{para(r.iadeToplam)}</td>
                  <td className="s">{veri.genelToplam ? ((r.toplam / veri.genelToplam) * 100).toFixed(1) + "%" : ""}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}
    </section>
  );
}
