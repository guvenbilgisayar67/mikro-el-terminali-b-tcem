import { useState } from "react";
import { bugun, csvIndir, para, qs, tarih } from "../api";
import { Alan, Durum, useVeri } from "../ortak";

type Cari = { kod: string; unvan: string; vergiNo: string | null; telefon: string | null; eposta: string | null; borc: number; alacak: number; bakiye: number };
type Hareket = { tarih: string; evrakNo: string; evrakAdi: string; belgeNo: string | null; aciklama: string | null; borc: number; alacak: number; bakiye: number; vade: string | null; guid: string };
type Ekstre = { cari: Cari; baslangic: string; bitis: string; devirBakiye: number; donemBorc: number; donemAlacak: number; sonBakiye: number; hareketler: Hareket[] };

type EvrakBasi = { guid: string; tarih: string; evrakNo: string; evrakAdi: string; belgeNo: string | null; aciklama: string | null; cariKod: string; cariAdi: string; borc: number; alacak: number; vade: string | null };
type StokSatiri = { satirNo: number; stokKodu: string; urunAdi: string; birim: string | null; miktar: number; birimFiyat: number; tutar: number; iskonto: number; masraf: number; kdv: number; net: number; depoNo: number; irsaliye: string | null };
type Belge = { refNo: string; seriNo: string | null; borclu: string | null; banka: string | null; vade: string | null; tutar: number; tur: string };
type EvrakDetayi = { baslik: EvrakBasi; satirlar: StokSatiri[]; belgeler: Belge[]; digerSatirlar: EvrakBasi[] };

const miktar = (n: number) => n.toLocaleString("tr-TR", { maximumFractionDigits: 3 });

/** Ekstre satırının içi: faturada ürünler, çek / kart tahsilatında belgeler, diğer evraklarda evrağın öbür satırları. */
function EvrakIcerik({ guid }: { guid: string }) {
  const d = useVeri<EvrakDetayi>(`/api/cari/evrak/${guid}`);
  if (!d.veri) return <Durum hata={d.hata} yukleniyor={true} />;
  const { satirlar, belgeler, digerSatirlar, baslik } = d.veri;
  const t = (f: (s: StokSatiri) => number) => satirlar.reduce((a, s) => a + f(s), 0);
  const irsaliyeler = [...new Set(satirlar.map((s) => s.irsaliye).filter(Boolean))];
  return (
    <div className="evrak-ici">
      <div className="evrak-ust">
        <b>{baslik.evrakAdi} {baslik.evrakNo}</b>
        {baslik.belgeNo && <span className="pill">Belge {baslik.belgeNo}</span>}
        {irsaliyeler.map((i) => <span key={i} className="pill">İrsaliye {i}</span>)}
        {[...new Set(satirlar.map((x) => x.depoNo))].map((d) => <span key={d} className="pill">Depo {d}</span>)}
        {baslik.vade && <span className="pill">Vade {tarih(baslik.vade)}</span>}
      </div>
      {satirlar.length > 0 && (
        <table className="ic">
          <thead><tr><th>Ürün</th><th>Kod</th><th className="s">Miktar</th><th className="s">Birim fiyat</th><th className="s">Tutar</th><th className="s">İskonto</th><th className="s">KDV</th><th className="s">Net</th><th className="s">Depo</th></tr></thead>
          <tbody>
            {satirlar.map((s) => (
              <tr key={`${s.irsaliye}-${s.satirNo}`}>
                <td>{s.urunAdi}</td><td>{s.stokKodu}</td><td className="s">{miktar(s.miktar)} {s.birim}</td>
                <td className="s">{para(s.birimFiyat)}</td><td className="s">{para(s.tutar)}</td>
                <td className="s">{s.iskonto ? para(s.iskonto) : ""}</td><td className="s">{para(s.kdv)}</td><td className="s b">{para(s.net)}</td><td className="s">{s.depoNo}</td>
              </tr>
            ))}
            <tr className="toplam"><td colSpan={4}>{satirlar.length} satır</td><td className="s">{para(t((s) => s.tutar))}</td><td className="s">{para(t((s) => s.iskonto))}</td><td className="s">{para(t((s) => s.kdv))}</td><td className="s b">{para(t((s) => s.net))}</td><td></td></tr>
          </tbody>
        </table>
      )}
      {belgeler.length > 0 && (
        <table className="ic">
          <thead><tr><th>Belge</th><th>Ref. no</th><th>Seri no</th><th>Borçlu</th><th>Vade</th><th className="s">Tutar</th></tr></thead>
          <tbody>{belgeler.map((b) => <tr key={b.refNo}><td>{b.tur}</td><td>{b.refNo}</td><td>{b.seriNo}</td><td>{b.borclu}</td><td>{tarih(b.vade)}</td><td className="s">{para(b.tutar)}</td></tr>)}</tbody>
        </table>
      )}
      {digerSatirlar.length > 0 && (
        <>
          <p className="not">Aynı evraktaki diğer satırlar</p>
          <table className="ic">
            <thead><tr><th>Evrak</th><th>Cari</th><th>Açıklama</th><th>Vade</th><th className="s">Borç</th><th className="s">Alacak</th></tr></thead>
            <tbody>{digerSatirlar.map((h) => <tr key={h.guid}><td>{h.evrakAdi}</td><td>{h.cariAdi}</td><td className="soluk">{h.aciklama}</td><td>{tarih(h.vade)}</td><td className="s">{h.borc ? para(h.borc) : ""}</td><td className="s">{h.alacak ? para(h.alacak) : ""}</td></tr>)}</tbody>
          </table>
        </>
      )}
      {!satirlar.length && !belgeler.length && !digerSatirlar.length && <p className="not">Bu evrakın ayrıca satırı yok{baslik.aciklama ? `: ${baslik.aciklama}` : "."}</p>}
    </div>
  );
}

/** Evrak türüne göre etiket rengi. */
function evrakRengi(ad: string) {
  const a = ad.toLocaleLowerCase("tr-TR");
  if (a.includes("iade")) return "turuncu";
  if (a.includes("alış") || a.includes("alis")) return "mavi";
  if (a.includes("satış") || a.includes("satis")) return "mor";
  if (a.includes("çek") || a.includes("senet") || a.includes("tahsil") || a.includes("ödeme") || a.includes("kasa") || a.includes("banka") || a.includes("kredi")) return "yesil";
  return "";
}
const basHarf = (s: string) => s.replace(/[^\p{L}\p{N} ]/gu, " ").split(/\s+/).filter(Boolean).slice(0, 2).map((p) => p[0]).join("").toLocaleUpperCase("tr-TR");

const bakiyeYazi = (n: number) => `${para(Math.abs(n))} ${n > 0 ? "(B)" : n < 0 ? "(A)" : ""}`;

export default function CariSayfa() {
  const [ara, setAra] = useState("");
  const [arama, setArama] = useState("");
  const [secili, setSecili] = useState<string | null>(null);
  const [acik, setAcik] = useState<string | null>(null);
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
                  <div className="cari-kart">
                    <span className="harf">{basHarf(ekstre.veri.cari.unvan)}</span>
                    <div className="kimlik">
                      <b>{ekstre.veri.cari.unvan}</b>
                      <span className="not">{[ekstre.veri.cari.kod, ekstre.veri.cari.vergiNo && `Vergi no ${ekstre.veri.cari.vergiNo}`, ekstre.veri.cari.telefon, ekstre.veri.cari.eposta].filter(Boolean).join(" · ")}</span>
                      <div className="not">{tarih(ekstre.veri.baslangic)} – {tarih(ekstre.veri.bitis)} · (B) borçlu, (A) alacaklı · içeriğini görmek için satıra tıklayın</div>
                    </div>
                    <div className="rakamlar">
                      <div><span className="kart-baslik">Devir</span><b>{bakiyeYazi(ekstre.veri.devirBakiye)}</b></div>
                      <div><span className="kart-baslik">Dönem borç</span><b>{para(ekstre.veri.donemBorc)}</b></div>
                      <div><span className="kart-baslik">Dönem alacak</span><b>{para(ekstre.veri.donemAlacak)}</b></div>
                      <div className="bakiye"><span className="kart-baslik">Son bakiye</span>
                        <b className={ekstre.veri.sonBakiye < 0 ? "eksi" : ""}>{para(Math.abs(ekstre.veri.sonBakiye))}{" "}
                          {ekstre.veri.sonBakiye !== 0 && <span className={"pill " + (ekstre.veri.sonBakiye < 0 ? "kirmizi" : "mavi")}>{ekstre.veri.sonBakiye < 0 ? "Alacaklı" : "Borçlu"}</span>}
                        </b>
                      </div>
                    </div>
                  </div>
                  <table>
                    <thead><tr><th>Tarih</th><th>Evrak</th><th>Evrak no</th><th>Açıklama</th><th>Vade</th><th className="s">Borç</th><th className="s">Alacak</th><th className="s">Bakiye</th><th></th></tr></thead>
                    <tbody>
                      <tr className="devir"><td colSpan={7}>Devir</td><td className="s">{bakiyeYazi(ekstre.veri.devirBakiye)}</td><td></td></tr>
                      {ekstre.veri.hareketler.map((h) => [
                        <tr key={h.guid} className={"tiklanir" + (acik === h.guid ? " secili" : "")} title="İçeriğini görmek için tıklayın"
                            onClick={() => setAcik(acik === h.guid ? null : h.guid)}>
                          <td>{tarih(h.tarih)}</td><td><span className={"pill " + evrakRengi(h.evrakAdi)}>{h.evrakAdi}</span></td><td title={h.belgeNo ? `Belge no: ${h.belgeNo}` : ""}>{h.evrakNo}</td>
                          <td className="soluk">{h.aciklama}</td><td>{tarih(h.vade)}</td>
                          <td className="s">{h.borc ? para(h.borc) : ""}</td><td className="s">{h.alacak ? para(h.alacak) : ""}</td>
                          <td className="s b">{bakiyeYazi(h.bakiye)}</td><td className="ok">{acik === h.guid ? "⌄" : "›"}</td>
                        </tr>,
                        acik === h.guid && <tr key={h.guid + "-ic"} className="alt-satir"><td colSpan={9}><EvrakIcerik guid={h.guid} /></td></tr>,
                      ])}
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
