import { useState } from "react";
import { bugun, csvIndir, para, qs, tarih } from "../api";
import { Alan, Durum, useVeri } from "../ortak";

type Personel = { kod: string; ad: string; bakiye: number };
type Hareket = { guid: string; tarih: string; evrak: string; evrakAdi: string; belgeNo: string; karsiHesap: string; aciklama: string; borc: number; alacak: number; bakiye: number };
type Foy = { personel: Personel; devir: number; borc: number; alacak: number; bakiye: number; hareketler: Hareket[] };

const bakiyeYazi = (b: number) => `${para(Math.abs(b))} ${b > 0 ? "(B)" : b < 0 ? "(A)" : ""}`;

/** Cari personel hareket föyü (Mikro 041436): personel hesabının borç / alacak hareketleri, yürüyen bakiyeyle. */
export default function PersonelFoy() {
  const [ara, setAra] = useState("");
  const [secili, setSecili] = useState<Personel | null>(null);
  const [bas, setBas] = useState(`${new Date().getFullYear()}-01-01`);
  const [bit, setBit] = useState(bugun());
  const liste = useVeri<Personel[]>(`/api/personel?${qs({ ara: ara.trim() })}`);
  const f = useVeri<Foy>(secili ? `/api/personel/${encodeURIComponent(secili.kod)}/foy?${qs({ bas, bit })}` : null);
  const v = f.veri;

  return (
    <section>
      <header className="sayfa-baslik"><h1>Cari personel hareket föyü</h1></header>
      <p className="not">Mikro'daki Cari Personel Hareket Föyü (041436): personel hesaplarının (ör. 335.027) maaş ödemeleri ve diğer hareketleri.</p>
      <div className="ekstre-duzen" style={{ display: "grid", gridTemplateColumns: "240px minmax(0, 1fr)", gap: 16, alignItems: "start" }}>
        <div>
          <Alan etiket="Personel ara"><input value={ara} placeholder="Kod ya da ad" onChange={(e) => setAra(e.target.value)} /></Alan>
          <Durum hata={liste.hata} yukleniyor={liste.yukleniyor} />
          <div className="liste" style={{ marginTop: 8 }}>
            {liste.veri?.length === 0 && <div className="bilgi">Bulunamadı.</div>}
            {liste.veri?.map((p) => (
              <button key={p.kod} className={"liste-satir" + (secili?.kod === p.kod ? " secili" : "")} onClick={() => setSecili(p)}>
                <span>{p.ad || p.kod}<small>{p.kod}</small></span><small>{bakiyeYazi(p.bakiye)}</small>
              </button>
            ))}
          </div>
        </div>
        <div style={{ minWidth: 0, overflowX: "auto" }}>
          <div className="filtre">
            <Alan etiket="Başlangıç"><input type="date" value={bas} onChange={(e) => setBas(e.target.value)} /></Alan>
            <Alan etiket="Bitiş"><input type="date" value={bit} onChange={(e) => setBit(e.target.value)} /></Alan>
            <button type="button" className="ikincil" disabled={!v?.hareketler.length} onClick={() => v && csvIndir(`personel_foyu_${v.personel.kod}_${bas}_${bit}.csv`,
              ["Tarih", "Evrak", "Evrak türü", "Belge no", "Karşı hesap", "Açıklama", "Borç", "Alacak", "Bakiye"],
              v.hareketler.map((h) => [tarih(h.tarih), h.evrak, h.evrakAdi, h.belgeNo, h.karsiHesap, h.aciklama, h.borc, h.alacak, h.bakiye]))}>Excel'e (CSV) indir</button>
          </div>
          {!secili && <p className="not">Soldan bir personel seçin.</p>}
          <Durum hata={f.hata} yukleniyor={f.yukleniyor} />
          {v && (
            <>
              <h2>{v.personel.ad || v.personel.kod} <small>{v.personel.kod}</small></h2>
              <table>
                <thead><tr><th>Tarih</th><th>Evrak</th><th>Evrak türü</th><th>Belge no</th><th>Karşı hesap</th><th>Açıklama</th><th className="s">Borç</th><th className="s">Alacak</th><th className="s">Bakiye</th></tr></thead>
                <tbody>
                  <tr><td colSpan={8}><b>Devir ({tarih(bas)} öncesi)</b></td><td className="s"><b>{bakiyeYazi(v.devir)}</b></td></tr>
                  {v.hareketler.length === 0 && <tr><td colSpan={9} className="bos">Bu dönemde hareket yok.</td></tr>}
                  {v.hareketler.map((h) => (
                    <tr key={h.guid}>
                      <td>{tarih(h.tarih)}</td><td>{h.evrak}</td><td>{h.evrakAdi}</td><td>{h.belgeNo}</td><td style={{ whiteSpace: "normal" }}>{h.karsiHesap}</td><td style={{ whiteSpace: "normal" }}>{h.aciklama}</td>
                      <td className="s">{h.borc ? para(h.borc) : ""}</td><td className="s">{h.alacak ? para(h.alacak) : ""}</td><td className="s">{bakiyeYazi(h.bakiye)}</td>
                    </tr>
                  ))}
                </tbody>
                <tfoot><tr><td colSpan={6}>Dönem toplamı</td><td className="s">{para(v.borc)}</td><td className="s">{para(v.alacak)}</td><td className="s">{bakiyeYazi(v.bakiye)}</td></tr></tfoot>
              </table>
            </>
          )}
        </div>
      </div>
    </section>
  );
}
