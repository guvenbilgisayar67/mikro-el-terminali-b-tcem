import { useRef, useState } from "react";
import { api, bugun, Oturum, para, qs, sayiOku, tarih, yetkili } from "../api";
import { Alan, CariOzet, CariSec, DepoSec, Durum, Mesaj, MesajKutusu, useVeri, YazmaDurumu } from "../ortak";

type Satir = { kod: string; ad: string; birim: string; miktar: string; fiyat: string; isk: string[] };
type Bekleyen = { seri: string; sira: number; tarih: string; belgeNo: string; cariKod: string; cariAdi: string; depoNo: number; satirSayisi: number; araToplam: number };
type FaturaSonuc = { evrakTip: number; seri: string; sira: number; araToplam: number; iskonto: number; kdv: number; toplam: number };

const n = (s: string) => sayiOku(s) || 0;
const BOS_ISK = ["", "", "", "", ""];
// Mikro'daki gibi zincirleme: her iskonto bir öncekinden kalan tutara uygulanır.
const satirTutar = (s: Satir) => s.isk.reduce((t, y) => t - (t * n(y)) / 100, n(s.miktar) * n(s.fiyat));

/** Ürün satırları: barkod okut, miktar / fiyat / iskonto düzenle. */
function Satirlar({ satirlar, degisti, fiyatGetir, kdvDahil }: {
  satirlar: Satir[]; degisti: (s: Satir[]) => void; fiyatGetir: (barkod: string) => Promise<Omit<Satir, "miktar" | "isk">>; kdvDahil: boolean;
}) {
  const [barkod, setBarkod] = useState("");
  const [hata, setHata] = useState<string | null>(null);
  const ref = useRef<HTMLInputElement>(null);

  async function ekle(e: React.FormEvent) {
    e.preventDefault();
    const b = barkod.trim();
    setBarkod("");
    ref.current?.focus();
    if (!b) return;
    try {
      const u = await fiyatGetir(b);
      const var_ = satirlar.find((s) => s.kod === u.kod);
      degisti(var_ ? satirlar.map((s) => (s === var_ ? { ...s, miktar: String(n(s.miktar) + 1) } : s)) : [...satirlar, { ...u, miktar: "1", isk: BOS_ISK }]);
      setHata(null);
    } catch (err) {
      setHata(`${b}: ${(err as Error).message}`);
    }
  }
  const degis = (i: number, alan: "miktar" | "fiyat", v: string) => degisti(satirlar.map((s, j) => (j === i ? { ...s, [alan]: v } : s)));
  const iskDegis = (i: number, k: number, v: string) =>
    degisti(satirlar.map((s, j) => (j === i ? { ...s, isk: s.isk.map((x, m) => (m === k ? v : x)) } : s)));

  return (
    <>
      <form className="filtre" onSubmit={ekle}>
        <Alan etiket="Barkod okut ya da stok kodu yaz"><input ref={ref} value={barkod} onChange={(e) => setBarkod(e.target.value)} /></Alan>
        <button type="submit">Ekle</button>
      </form>
      {hata && <div className="uyari hata">{hata}</div>}
      <table>
        <thead><tr><th>Ürün</th><th>Kod</th><th className="s">Miktar</th><th className="s">Birim fiyat {kdvDahil ? "(KDV dahil)" : "(KDV hariç)"}</th>{BOS_ISK.map((_, k) => <th key={k} className="s" title="Zincirleme: bir öncekinden kalan tutara uygulanır">İsk.{k + 1} %</th>)}<th className="s">Tutar</th><th></th></tr></thead>
        <tbody>
          {satirlar.length === 0 && <tr><td colSpan={11} className="bos">Ürün okutun.</td></tr>}
          {satirlar.map((s, i) => (
            <tr key={s.kod}>
              <td>{s.ad}</td><td>{s.kod}</td>
              <td className="s"><input className="adet" inputMode="decimal" value={s.miktar} onChange={(e) => degis(i, "miktar", e.target.value)} /> {s.birim}</td>
              <td className="s"><input className="fiyat" inputMode="decimal" value={s.fiyat} onChange={(e) => degis(i, "fiyat", e.target.value)} /></td>
              {s.isk.map((y, k) => <td key={k} className="s"><input className="isk" inputMode="decimal" value={y} onChange={(e) => iskDegis(i, k, e.target.value)} /></td>)}
              <td className="s">{para(satirTutar(s))}</td>
              <td><button className="sil" title="Çıkar" onClick={() => degisti(satirlar.filter((_, j) => j !== i))}>×</button></td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  );
}

const satirGecerli = (s: Satir[]) => s.length > 0 && s.every((x) => n(x.miktar) > 0 && n(x.fiyat) >= 0 && x.isk.every((y) => n(y) >= 0 && n(y) < 100));
// Sondaki boş iskontolar gönderilmez; aradaki boşluk 0 olur (Mikro'da 1. iskonto boş, 2. dolu olabiliyor).
const iskontolar = (isk: string[]) => { const v = isk.map(n); while (v.length && v[v.length - 1] === 0) v.pop(); return v; };
const satirIstek = (s: Satir[], kdvDahil: boolean) =>
  s.map((x) => ({ urun: x.kod, miktar: n(x.miktar), birimFiyat: n(x.fiyat), iskontolar: iskontolar(x.isk), kdvDahil }));

function AlisIrsaliyesi({ depoNo }: { depoNo: number | null }) {
  const [cari, setCari] = useState<CariOzet | null>(null);
  const [depo, setDepo] = useState<number | "">(depoNo ?? "");
  const [gun, setGun] = useState(bugun());
  const [belgeNo, setBelgeNo] = useState("");
  const [kdvDahil, setKdvDahil] = useState(false);
  const [satirlar, setSatirlar] = useState<Satir[]>([]);
  const [mesaj, setMesaj] = useState<Mesaj>(null);
  const [mesgul, setMesgul] = useState(false);

  // Önerilen fiyat: son alış (KDV hariç, iskontolar düşülmüş).
  const fiyatGetir = async (b: string) => {
    const u = await api.get<{ kod: string; ad: string; birim: string; birimFiyat: number }>(`/api/sevk/urun?${qs({ k: b, tarih: gun })}`);
    return { kod: u.kod, ad: u.ad, birim: u.birim, fiyat: u.birimFiyat ? String(u.birimFiyat) : "" };
  };

  async function kaydet() {
    setMesgul(true);
    try {
      const r = await api.post<{ seri: string; sira: number; satirSayisi: number; araToplam: number; kdv: number; toplam: number }>("/api/fatura/alis-irsaliyesi", {
        cariKod: cari!.kod, depoNo: depo, tarih: gun, belgeNo: belgeNo || null, satirlar: satirIstek(satirlar, kdvDahil),
      });
      setMesaj({ tur: "bilgi", metin: `Alış irsaliyesi ${r.seri} ${r.sira} kaydedildi: ${r.satirSayisi} satır, KDV hariç ${para(r.araToplam)} TL, KDV dahil ${para(r.toplam)} TL. Fatura gelince "Bekleyen irsaliyeler"den faturasını girin.` });
      setSatirlar([]);
      setBelgeNo("");
    } catch (err) {
      setMesaj({ tur: "hata", metin: (err as Error).message });
    }
    setMesgul(false);
  }

  const toplam = satirlar.reduce((s, x) => s + satirTutar(x), 0);
  return (
    <>
      <p className="not">Mal geldiğinde girilir; stoğa girer, cariye henüz yazılmaz. Faturası gelince irsaliyeden fatura oluşturulur (Mikro'daki akışınızla aynı).</p>
      <div className="filtre">
        <CariSec deger={cari} degisti={setCari} etiket="Tedarikçi" />
        <DepoSec etiket="Depo" deger={depo} degisti={setDepo} kilitli={depoNo != null} />
        <Alan etiket="Tarih"><input type="date" value={gun} max={bugun()} onChange={(e) => setGun(e.target.value)} /></Alan>
        <Alan etiket="İrsaliye no (belge)"><input className="kisa" value={belgeNo} onChange={(e) => setBelgeNo(e.target.value)} /></Alan>
        <label className="kutu"><input type="checkbox" checked={kdvDahil} onChange={(e) => setKdvDahil(e.target.checked)} /> Fiyatlar KDV dahil</label>
      </div>
      <Satirlar satirlar={satirlar} degisti={setSatirlar} fiyatGetir={fiyatGetir} kdvDahil={kdvDahil} />
      <MesajKutusu mesaj={mesaj} />
      <div className="kaydet">
        <span>{satirlar.length} ürün, {para(toplam)} TL {kdvDahil ? "(KDV dahil)" : "(KDV hariç)"}</span>
        <button disabled={!cari || !depo || !satirGecerli(satirlar) || mesgul} onClick={kaydet}>{mesgul ? "Kaydediliyor…" : "İrsaliyeyi kaydet"}</button>
      </div>
    </>
  );
}

function BekleyenIrsaliyeler() {
  const [cari, setCari] = useState<CariOzet | null>(null);
  const [secili, setSecili] = useState<Bekleyen | null>(null);
  const [belgeNo, setBelgeNo] = useState("");
  const [gun, setGun] = useState("");
  const [mesaj, setMesaj] = useState<Mesaj>(null);
  const [mesgul, setMesgul] = useState(false);
  const liste = useVeri<Bekleyen[]>(`/api/fatura/bekleyen-irsaliyeler?${qs({ cari: cari?.kod })}`);

  async function faturalastir() {
    if (!secili) return;
    setMesgul(true);
    try {
      const r = await api.post<FaturaSonuc>("/api/fatura/alis/irsaliyeden", { seri: secili.seri, sira: secili.sira, belgeNo: belgeNo || null, tarih: gun || null });
      setMesaj({ tur: "bilgi", metin: `Alış faturası ${r.sira} kaydedildi: KDV hariç ${para(r.araToplam - r.iskonto)} TL, KDV ${para(r.kdv)} TL, toplam ${para(r.toplam)} TL. Cariye alacak yazıldı.` });
      setSecili(null);
      liste.yenile();
    } catch (err) {
      setMesaj({ tur: "hata", metin: (err as Error).message });
    }
    setMesgul(false);
  }

  return (
    <>
      <p className="not">Faturası henüz girilmemiş alış irsaliyeleri (son 6 ay). Birini seçip fatura numarasını yazın; fatura irsaliyenin tutarlarıyla oluşur.</p>
      <div className="filtre"><CariSec deger={cari} degisti={setCari} etiket="Tedarikçiye göre süz (isteğe bağlı)" /></div>
      <Durum hata={liste.hata} yukleniyor={liste.yukleniyor} />
      <table>
        <thead><tr><th>Tedarikçi</th><th>İrsaliye</th><th>Belge no</th><th>Tarih</th><th>Depo</th><th className="s">Satır</th><th className="s">Tutar (KDV hariç)</th></tr></thead>
        <tbody>
          {liste.veri?.length === 0 && <tr><td colSpan={7} className="bos">Bekleyen irsaliye yok.</td></tr>}
          {liste.veri?.map((b) => {
            const s = secili?.seri === b.seri && secili.sira === b.sira;
            return (
              <tr key={`${b.seri}/${b.sira}`} className={"tiklanir" + (s ? " secili" : "")} onClick={() => { setSecili(b); setBelgeNo(b.belgeNo); setGun(b.tarih.slice(0, 10)); setMesaj(null); }}>
                <td>{b.cariAdi}<br /><small>{b.cariKod}</small></td><td>{b.seri} {b.sira}</td><td>{b.belgeNo}</td><td>{tarih(b.tarih)}</td><td>{b.depoNo}</td>
                <td className="s">{b.satirSayisi}</td><td className="s">{para(b.araToplam)}</td>
              </tr>
            );
          })}
        </tbody>
      </table>
      {secili && (
        <div className="filtre" style={{ marginTop: 12 }}>
          <Alan etiket="Fatura no"><input className="kisa" value={belgeNo} onChange={(e) => setBelgeNo(e.target.value)} /></Alan>
          <Alan etiket="Fatura tarihi"><input type="date" value={gun} max={bugun()} onChange={(e) => setGun(e.target.value)} /></Alan>
          <button disabled={mesgul || !belgeNo.trim()} onClick={faturalastir}>{mesgul ? "Kaydediliyor…" : `${secili.seri} ${secili.sira} faturasını gir`}</button>
        </div>
      )}
      <MesajKutusu mesaj={mesaj} />
    </>
  );
}

function SatisFaturasi({ depoNo }: { depoNo: number | null }) {
  const [cari, setCari] = useState<CariOzet | null>(null);
  const [depo, setDepo] = useState<number | "">(depoNo ?? "");
  const [gun, setGun] = useState(bugun());
  const [kdvDahil, setKdvDahil] = useState(true);
  const [satirlar, setSatirlar] = useState<Satir[]>([]);
  const [mesaj, setMesaj] = useState<Mesaj>(null);
  const [mesgul, setMesgul] = useState(false);

  // Önerilen fiyat: satış fiyat listesi (KDV dahil).
  const fiyatGetir = async (b: string) => {
    const r = await api.get<{ urun: { stokKodu: string; urunAdi: string; birim: string | null; fiyat: number } }>(`/api/stok/${encodeURIComponent(b)}?miktar=false`);
    return { kod: r.urun.stokKodu, ad: r.urun.urunAdi, birim: r.urun.birim ?? "", fiyat: r.urun.fiyat ? String(r.urun.fiyat) : "" };
  };

  async function kaydet() {
    setMesgul(true);
    try {
      const r = await api.post<FaturaSonuc>("/api/fatura/satis", { cariKod: cari!.kod, depoNo: depo, tarih: gun, satirlar: satirIstek(satirlar, kdvDahil) });
      setMesaj({ tur: "bilgi", metin: `Satış faturası ${r.sira} kaydedildi: KDV hariç ${para(r.araToplam - r.iskonto)} TL, KDV ${para(r.kdv)} TL, toplam ${para(r.toplam)} TL.` });
      setSatirlar([]);
    } catch (err) {
      setMesaj({ tur: "hata", metin: (err as Error).message });
    }
    setMesgul(false);
  }

  const toplam = satirlar.reduce((s, x) => s + satirTutar(x), 0);
  return (
    <>
      <p className="not">Toptan satış faturası. Stoktan düşer, cariye borç yazılır. Fiyat satış listesinden (KDV dahil) gelir, değiştirebilirsiniz.</p>
      <div className="filtre">
        <CariSec deger={cari} degisti={setCari} etiket="Müşteri" />
        <DepoSec etiket="Depo" deger={depo} degisti={setDepo} kilitli={depoNo != null} />
        <Alan etiket="Tarih"><input type="date" value={gun} max={bugun()} onChange={(e) => setGun(e.target.value)} /></Alan>
        <label className="kutu"><input type="checkbox" checked={kdvDahil} onChange={(e) => setKdvDahil(e.target.checked)} /> Fiyatlar KDV dahil</label>
      </div>
      <Satirlar satirlar={satirlar} degisti={setSatirlar} fiyatGetir={fiyatGetir} kdvDahil={kdvDahil} />
      <MesajKutusu mesaj={mesaj} />
      <div className="kaydet">
        <span>{satirlar.length} ürün, {para(toplam)} TL {kdvDahil ? "(KDV dahil)" : "(KDV hariç)"}</span>
        <button disabled={!cari || !depo || !satirGecerli(satirlar) || mesgul} onClick={kaydet}>{mesgul ? "Kaydediliyor…" : "Faturayı kaydet"}</button>
      </div>
    </>
  );
}

export default function Fatura({ depoNo, oturum }: { depoNo: number | null; oturum: Oturum }) {
  const alis = yetkili(oturum, "alis_fatura");
  const sekmeler = ([
    { id: "irsaliye", ad: "Alış irsaliyesi", var: alis },
    { id: "bekleyen", ad: "Bekleyen irsaliyeler → alış faturası", var: alis },
    { id: "satis", ad: "Satış faturası", var: yetkili(oturum, "satis_fatura") },
  ] as const).filter((s) => s.var);
  const [sekme, setSekme] = useState<"irsaliye" | "bekleyen" | "satis">(sekmeler[0]?.id ?? "irsaliye");
  return (
    <section>
      <header className="sayfa-baslik"><h1>Fatura ve irsaliye</h1></header>
      <YazmaDurumu />
      <div className="sekmeler">
        {sekmeler.map((s) => <button key={s.id} className={sekme === s.id ? "aktif" : ""} onClick={() => setSekme(s.id)}>{s.ad}</button>)}
      </div>
      {sekme === "irsaliye" && <AlisIrsaliyesi depoNo={depoNo} />}
      {sekme === "bekleyen" && <BekleyenIrsaliyeler />}
      {sekme === "satis" && <SatisFaturasi depoNo={depoNo} />}
    </section>
  );
}
