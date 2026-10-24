import { useState } from "react";
import { bugun, csvIndir, para, qs, tarih } from "../api";
import { Alan, Durum, Kart, useVeri } from "../ortak";

type Cari = { kod: string; unvan: string; vergiNo: string | null; telefon: string | null; eposta: string | null; borc: number; alacak: number; bakiye: number };
type Hareket = { tarih: string; evrakNo: string; evrakAdi: string; belgeNo: string | null; aciklama: string | null; borc: number; alacak: number; bakiye: number; vade: string | null; guid: string };
type Ekstre = { cari: Cari; baslangic: string; bitis: string; devirBakiye: number; donemBorc: number; donemAlacak: number; sonBakiye: number; hareketler: Hareket[] };

const bakiyeYazi = (n: number) => `${para(Math.abs(n))} ${n > 0 ? "(B)" : n < 0 ? "(A)" : ""}`;

export default function CariSayfa() {
  const [ara, setAra] = useState("");
  const [arama, setArama] = useState("");
  const [secili, setSecili] = useState<string | null>(null);
  const [donem, setDonem] = useState({ bas: `${new Date().getFullYear()}-01-01`, bit: bugun() });
  const liste = useVeri<Cari[]>(`/api/cari?${qs({ ara: arama, ilk: 100 })}`);
  const ekstre = useVeri<Ekstre>(secili ? `/api/cari/${encodeURIComponent(secili)}/ekstre?${qs(donem)}` : null);

  return (
    <section>
      <header className="sayfa-baslik">
        <h1>Cari hesap ekstresi</h1>
        <form className="filtre" onSubmit={(e) => { e.preventDefault(); setArama(ara); }}>
          <Alan etiket="Cari ara (kod ya da ünvan)"><input value={ara} onChange={(e) => setAra(e.target.value)} placeholder="ör. MUTLU, 320.330" /></Alan>
          <button type="submit">Ara</button>
        </form>
      </header>
      <div className="iki-sutun">
        <div className="liste">
          <Durum hata={liste.hata} yukleniyor={liste.yukleniyor} />
          {liste.veri?.map((c) => (
            <button key={c.kod} className={"liste-satir" + (secili === c.kod ? " secili" : "")} onClick={() => setSecili(c.kod)}>
              <span><b>{c.unvan}</b><small>{c.kod}</small></span>
              <span className={c.bakiye < 0 ? "eksi" : ""}>{bakiyeYazi(c.bakiye)}</span>
            </button>
          ))}
        </div>
        <div className="detay">
          {!secili && <div className="bilgi">Soldan bir cari seçin.</div>}
          {secili && (
            <>
              <div className="filtre">
                <Alan etiket="Başlangıç"><input type="date" value={donem.bas} onChange={(e) => setDonem({ ...donem, bas: e.target.value })} /></Alan>
                <Alan etiket="Bitiş"><input type="date" value={donem.bit} onChange={(e) => setDonem({ ...donem, bit: e.target.value })} /></Alan>
                {ekstre.veri && (
                  <>
                    <button className="ikincil" onClick={() => window.print()}>Yazdır</button>
                    <button className="ikincil" onClick={() => csvIndir(`ekstre_${secili}.csv`,
                      ["Tarih", "Evrak", "Evrak no", "Belge no", "Açıklama", "Vade", "Borç", "Alacak", "Bakiye"],
                      ekstre.veri!.hareketler.map((h) => [tarih(h.tarih), h.evrakAdi, h.evrakNo, h.belgeNo, h.aciklama, tarih(h.vade), h.borc, h.alacak, h.bakiye]))}>
                      Excel'e aktar
                    </button>
                  </>
                )}
              </div>
              <Durum hata={ekstre.hata} yukleniyor={ekstre.yukleniyor} />
              {ekstre.veri && (
                <div className="yazdirilabilir">
                  <h2>{ekstre.veri.cari.unvan} <small>{ekstre.veri.cari.kod}</small></h2>
                  <p className="not">{tarih(ekstre.veri.baslangic)} – {tarih(ekstre.veri.bitis)} · (B) borçlu, (A) alacaklı</p>
                  <div className="kartlar">
                    <Kart baslik="Devir" deger={bakiyeYazi(ekstre.veri.devirBakiye)} />
                    <Kart baslik="Dönem borç" deger={para(ekstre.veri.donemBorc)} />
                    <Kart baslik="Dönem alacak" deger={para(ekstre.veri.donemAlacak)} />
                    <Kart baslik="Son bakiye" deger={bakiyeYazi(ekstre.veri.sonBakiye)} />
                  </div>
                  <table>
                    <thead><tr><th>Tarih</th><th>Evrak</th><th>Evrak no</th><th>Açıklama</th><th>Vade</th><th className="s">Borç</th><th className="s">Alacak</th><th className="s">Bakiye</th></tr></thead>
                    <tbody>
                      <tr className="devir"><td colSpan={7}>Devir</td><td className="s">{bakiyeYazi(ekstre.veri.devirBakiye)}</td></tr>
                      {ekstre.veri.hareketler.map((h) => (
                        <tr key={h.guid}>
                          <td>{tarih(h.tarih)}</td><td>{h.evrakAdi}</td><td title={h.belgeNo ? `Belge no: ${h.belgeNo}` : ""}>{h.evrakNo}</td>
                          <td>{h.aciklama}</td><td>{tarih(h.vade)}</td>
                          <td className="s">{h.borc ? para(h.borc) : ""}</td><td className="s">{h.alacak ? para(h.alacak) : ""}</td>
                          <td className="s b">{bakiyeYazi(h.bakiye)}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </>
          )}
        </div>
      </div>
    </section>
  );
}
