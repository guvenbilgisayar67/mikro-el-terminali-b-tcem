import { useState } from "react";
import { api, bugun, para, sayiOku as sayi, tarih } from "../api";
import { Alan, CariOzet, CariSec, Durum, Mesaj, MesajKutusu, useVeri, YazmaDurumu } from "../ortak";

type GirisCeki = { vade: string; tutar: string; cekNo: string; banka: string; sube: string; borclu: string };
type FirmaCeki = { bankaKod: string; vade: string; tutar: string; cekNo: string };
type Portfoy = { refNo: string; borclu: string; sahipCari: string; vade: string; tutar: number; cekNo: string | null };
type Banka = { kod: string; ad: string; sube: string | null };
type Sonuc = { evrakTip: number; sira: number; refNolar: string[]; tutar: number };

const bosGiris = (): GirisCeki => ({ vade: "", tutar: "", cekNo: "", banka: "", sube: "", borclu: "" });
const bosFirma = (): FirmaCeki => ({ bankaKod: "", vade: "", tutar: "", cekNo: "" });

function Giris() {
  const [cari, setCari] = useState<CariOzet | null>(null);
  const [gun, setGun] = useState(bugun());
  const [cekler, setCekler] = useState<GirisCeki[]>([bosGiris()]);
  const [mesaj, setMesaj] = useState<Mesaj>(null);
  const [mesgul, setMesgul] = useState(false);

  const degis = (i: number, alan: keyof GirisCeki, v: string) => setCekler(cekler.map((c, j) => (j === i ? { ...c, [alan]: v } : c)));
  const gecerli = cari && cekler.length > 0 && cekler.every((c) => c.vade && sayi(c.tutar) > 0);
  const toplam = cekler.reduce((s, c) => s + (sayi(c.tutar) || 0), 0);

  async function kaydet() {
    if (!gecerli) return;
    setMesgul(true);
    try {
      const r = await api.post<Sonuc>("/api/cek/giris", {
        cariKod: cari!.kod, tarih: gun,
        cekler: cekler.map((c) => ({ vade: c.vade, tutar: sayi(c.tutar), cekNo: c.cekNo || null, banka: c.banka || null, sube: c.sube || null, borclu: c.borclu || null })),
      });
      setMesaj({ tur: "bilgi", metin: `Giriş bordrosu ${r.sira} kaydedildi: ${r.refNolar.join(", ")} (${para(r.tutar)} TL). Çekler ÇEK KASASI'nda, portföyde.` });
      setCekler([bosGiris()]);
    } catch (err) {
      setMesaj({ tur: "hata", metin: (err as Error).message });
    }
    setMesgul(false);
  }

  return (
    <>
      <p className="not">Müşteriden alınan çek. Cariye alacak yazılır, çek ÇEK KASASI'na portföye girer. Borçlu boş bırakılırsa carinin unvanı yazılır.</p>
      <div className="filtre">
        <CariSec deger={cari} degisti={setCari} etiket="Çeki veren cari" />
        <Alan etiket="Bordro tarihi"><input type="date" value={gun} max={bugun()} onChange={(e) => setGun(e.target.value)} /></Alan>
      </div>
      <table>
        <thead><tr><th>Vade</th><th className="s">Tutar</th><th>Çek no</th><th>Banka</th><th>Şube</th><th>Borçlu (keşideci)</th><th></th></tr></thead>
        <tbody>
          {cekler.map((c, i) => (
            <tr key={i}>
              <td><input type="date" value={c.vade} onChange={(e) => degis(i, "vade", e.target.value)} /></td>
              <td className="s"><input className="fiyat" inputMode="decimal" value={c.tutar} onChange={(e) => degis(i, "tutar", e.target.value)} /></td>
              <td><input className="kisa" value={c.cekNo} onChange={(e) => degis(i, "cekNo", e.target.value)} /></td>
              <td><input className="kisa" value={c.banka} onChange={(e) => degis(i, "banka", e.target.value)} /></td>
              <td><input className="kisa" value={c.sube} onChange={(e) => degis(i, "sube", e.target.value)} /></td>
              <td><input value={c.borclu} placeholder={cari?.unvan ?? ""} onChange={(e) => degis(i, "borclu", e.target.value)} /></td>
              <td><button className="sil" title="Çıkar" disabled={cekler.length === 1} onClick={() => setCekler(cekler.filter((_, j) => j !== i))}>×</button></td>
            </tr>
          ))}
        </tbody>
      </table>
      <div className="arac"><button className="ikincil" onClick={() => setCekler([...cekler, bosGiris()])}>Çek ekle</button></div>
      <MesajKutusu mesaj={mesaj} />
      <div className="kaydet">
        <span>{cekler.length} çek, {para(toplam)} TL</span>
        <button disabled={!gecerli || mesgul} onClick={kaydet}>{mesgul ? "Kaydediliyor…" : "Giriş bordrosunu kaydet"}</button>
      </div>
    </>
  );
}

function Cikis() {
  const [cari, setCari] = useState<CariOzet | null>(null);
  const [gun, setGun] = useState(bugun());
  const [secili, setSecili] = useState<string[]>([]);
  const [firma, setFirma] = useState<FirmaCeki[]>([]);
  const [mesaj, setMesaj] = useState<Mesaj>(null);
  const [mesgul, setMesgul] = useState(false);
  const portfoy = useVeri<Portfoy[]>("/api/cek/portfoy");
  const bankalar = useVeri<Banka[]>("/api/cek/bankalar");

  const degis = (i: number, alan: keyof FirmaCeki, v: string) => setFirma(firma.map((c, j) => (j === i ? { ...c, [alan]: v } : c)));
  const portfoyToplam = portfoy.veri?.filter((p) => secili.includes(p.refNo)).reduce((s, p) => s + p.tutar, 0) ?? 0;
  const firmaToplam = firma.reduce((s, c) => s + (sayi(c.tutar) || 0), 0);
  const gecerli = cari && secili.length + firma.length > 0 && firma.every((c) => c.bankaKod && c.vade && sayi(c.tutar) > 0);

  async function kaydet() {
    if (!gecerli) return;
    setMesgul(true);
    try {
      const r = await api.post<Sonuc>("/api/cek/cikis", {
        cariKod: cari!.kod, tarih: gun, musteriCekleri: secili,
        firmaCekleri: firma.map((c) => ({ bankaKod: c.bankaKod, vade: c.vade, tutar: sayi(c.tutar), cekNo: c.cekNo || null })),
      });
      setMesaj({ tur: "bilgi", metin: `Çıkış bordrosu ${r.sira} kaydedildi: ${r.refNolar.join(", ")} (${para(r.tutar)} TL).` });
      setSecili([]);
      setFirma([]);
      portfoy.yenile();
    } catch (err) {
      setMesaj({ tur: "hata", metin: (err as Error).message });
    }
    setMesgul(false);
  }

  return (
    <>
      <p className="not">Tedarikçiye verilen çek. Cariye borç (ödeme) yazılır. Portföydeki müşteri çekleri ciro edilir; kendi çekinizi yazarsanız banka hesabınızdan yeni çek açılır.</p>
      <div className="filtre">
        <CariSec deger={cari} degisti={setCari} etiket="Çekin verildiği cari" />
        <Alan etiket="Bordro tarihi"><input type="date" value={gun} max={bugun()} onChange={(e) => setGun(e.target.value)} /></Alan>
      </div>
      <h2>Portföydeki müşteri çekleri <small>ciro edilecekleri seçin</small></h2>
      <Durum hata={portfoy.hata} yukleniyor={portfoy.yukleniyor} />
      <table>
        <thead><tr><th></th><th>Çek</th><th>Borçlu</th><th>Veren</th><th>Vade</th><th className="s">Tutar</th></tr></thead>
        <tbody>
          {portfoy.veri?.length === 0 && <tr><td colSpan={6} className="bos">Portföyde çek yok.</td></tr>}
          {portfoy.veri?.map((p) => {
            const s = secili.includes(p.refNo);
            return (
              <tr key={p.refNo} className={"tiklanir" + (s ? " secili" : "")} onClick={() => setSecili(s ? secili.filter((x) => x !== p.refNo) : [...secili, p.refNo])}>
                <td><input type="checkbox" readOnly checked={s} /></td>
                <td>{p.refNo}{p.cekNo ? ` · ${p.cekNo}` : ""}</td><td>{p.borclu}</td><td>{p.sahipCari}</td><td>{tarih(p.vade)}</td><td className="s">{para(p.tutar)}</td>
              </tr>
            );
          })}
        </tbody>
      </table>
      <h2>Kendi çekimiz</h2>
      {firma.length > 0 && (
        <table>
          <thead><tr><th>Banka hesabı</th><th>Vade</th><th className="s">Tutar</th><th>Çek no</th><th></th></tr></thead>
          <tbody>
            {firma.map((c, i) => (
              <tr key={i}>
                <td>
                  <select value={c.bankaKod} onChange={(e) => degis(i, "bankaKod", e.target.value)}>
                    <option value="" disabled>Seçin</option>
                    {bankalar.veri?.map((b) => <option key={b.kod} value={b.kod}>{b.kod} · {b.ad}</option>)}
                  </select>
                </td>
                <td><input type="date" value={c.vade} onChange={(e) => degis(i, "vade", e.target.value)} /></td>
                <td className="s"><input className="fiyat" inputMode="decimal" value={c.tutar} onChange={(e) => degis(i, "tutar", e.target.value)} /></td>
                <td><input className="kisa" value={c.cekNo} onChange={(e) => degis(i, "cekNo", e.target.value)} /></td>
                <td><button className="sil" title="Çıkar" onClick={() => setFirma(firma.filter((_, j) => j !== i))}>×</button></td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <div className="arac"><button className="ikincil" onClick={() => setFirma([...firma, bosFirma()])}>Kendi çekimizi ekle</button></div>
      <MesajKutusu mesaj={mesaj} />
      <div className="kaydet">
        <span>{secili.length + firma.length} çek, {para(portfoyToplam + firmaToplam)} TL</span>
        <button disabled={!gecerli || mesgul} onClick={kaydet}>{mesgul ? "Kaydediliyor…" : "Çıkış bordrosunu kaydet"}</button>
      </div>
    </>
  );
}

export default function CekIslem() {
  const [sekme, setSekme] = useState<"giris" | "cikis">("giris");
  return (
    <section>
      <header className="sayfa-baslik"><h1>Çek giriş / çıkış</h1></header>
      <YazmaDurumu />
      <div className="sekmeler">
        <button className={sekme === "giris" ? "aktif" : ""} onClick={() => setSekme("giris")}>Çek girişi (müşteriden)</button>
        <button className={sekme === "cikis" ? "aktif" : ""} onClick={() => setSekme("cikis")}>Çek çıkışı (tedarikçiye)</button>
      </div>
      {sekme === "giris" ? <Giris /> : <Cikis />}
    </section>
  );
}
