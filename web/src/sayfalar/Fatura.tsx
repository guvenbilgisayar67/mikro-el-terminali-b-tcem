import { useEffect, useRef, useState } from "react";
import { api, bugun, csvIndir, gunEkle, miktar, Oturum, para, qs, sayiOku, tarih, yetkili } from "../api";
import { Alan, CariOzet, CariSec, DepoSec, Durum, Mesaj, MesajKutusu, useVeri, YazmaDurumu } from "../ortak";

type Satir = { kod: string; ad: string; birim: string; miktar: string; fiyat: string; isk: string[]; kdv?: number };
type Bekleyen = { seri: string; sira: number; tarih: string; belgeNo: string; cariKod: string; cariAdi: string; depoNo: number; satirSayisi: number; araToplam: number };
type FaturaSonuc = { evrakTip: number; seri: string; sira: number; araToplam: number; iskonto: number; kdv: number; toplam: number };
type UrunBilgi = { kod: string; ad: string; birim: string; sonAlis: number; kdvOrani: number };
type UrunAra = { kod: string; ad: string; birim: string };
type Getirilen = Omit<Satir, "miktar" | "isk">;

const n = (s: string) => sayiOku(s) || 0;
const BOS_ISK = ["", "", "", "", ""];
// Mikro'daki gibi zincirleme: her iskonto bir öncekinden kalan tutara uygulanır.
const satirTutar = (s: Satir) => s.isk.reduce((t, y) => t - (t * n(y)) / 100, n(s.miktar) * n(s.fiyat));
// Fiyat KDV dahilse tutar zaten KDV'li; KDV payı içinden ayrılır.
const satirKdv = (s: Satir, kdvDahil: boolean) => {
  const t = satirTutar(s), o = (s.kdv ?? 0) / 100;
  return kdvDahil ? t - t / (1 + o) : t * o;
};
const sayiYaz = (x: number) => (x ? String(Math.round(x * 10000) / 10000).replace(".", ",") : "");

/** Alış ekranlarının önerdiği fiyat: son alış (KDV hariç). KDV dahil giriliyorsa oranla çevrilir. */
const alisFiyati = (tarih: string, kdvDahil: boolean) => async (k: string): Promise<Getirilen> => {
  const u = await api.get<UrunBilgi>(`/api/fatura/urun?${qs({ k, tarih })}`);
  const f = kdvDahil ? u.sonAlis * (1 + u.kdvOrani / 100) : u.sonAlis;
  return { kod: u.kod, ad: u.ad, birim: u.birim, fiyat: sayiYaz(f), kdv: u.kdvOrani };
};

/**
 * Evrak satırları. Üstteki giriş satırına stok kodu / barkod okutulur ya da ürün adı yazılıp listeden seçilir;
 * ürün adı ve birimi gelir, miktar ve fiyat yazılıp Enter ile (ya da Ekle) aşağıdaki listeye eklenir.
 * Eklenen satırlarda miktar, fiyat ve iskontolar düzenlenebilir.
 */
function Satirlar({ satirlar, degisti, fiyatGetir, kdvDahil, fiyatEtiketi = "Birim fiyat" }: {
  satirlar: Satir[]; degisti: (s: Satir[]) => void; fiyatGetir: (kod: string) => Promise<Getirilen>; kdvDahil: boolean; fiyatEtiketi?: string;
}) {
  const [kod, setKod] = useState("");
  const [urun, setUrun] = useState<Getirilen | null>(null);
  const [miktarG, setMiktarG] = useState("1");
  const [fiyatG, setFiyatG] = useState("");
  const [iskG, setIskG] = useState("");
  const [hata, setHata] = useState<string | null>(null);
  const [aramaAcik, setAramaAcik] = useState(false);
  const kodRef = useRef<HTMLInputElement>(null);
  const miktarRef = useRef<HTMLInputElement>(null);
  const arama = useVeri<UrunAra[]>(aramaAcik && !urun && kod.trim().length >= 2 ? `/api/fatura/urun-ara?${qs({ q: kod.trim() })}` : null);

  function temizle() {
    setKod(""); setUrun(null); setMiktarG("1"); setFiyatG(""); setIskG(""); setAramaAcik(false);
    kodRef.current?.focus();
  }

  async function getir(k: string) {
    setAramaAcik(false);
    if (!k.trim()) return;
    try {
      const u = await fiyatGetir(k.trim());
      setUrun(u); setKod(u.kod); setFiyatG(u.fiyat); setHata(null);
      setTimeout(() => miktarRef.current?.select(), 0);
    } catch (err) {
      setHata(`${k}: ${(err as Error).message}`);
      setUrun(null);
    }
  }

  function ekle(e?: React.FormEvent) {
    e?.preventDefault();
    if (!urun) { getir(kod); return; }
    if (n(miktarG) <= 0) { setHata("Miktar sıfırdan büyük olmalı."); return; }
    const yeni: Satir = { ...urun, miktar: miktarG, fiyat: fiyatG, isk: [iskG, "", "", "", ""] };
    const var_ = satirlar.find((s) => s.kod === urun.kod && s.fiyat === fiyatG && s.isk.join() === yeni.isk.join());
    degisti(var_ ? satirlar.map((s) => (s === var_ ? { ...s, miktar: sayiYaz(n(s.miktar) + n(miktarG)) } : s)) : [...satirlar, yeni]);
    setHata(null);
    temizle();
  }

  const degis = (i: number, alan: "miktar" | "fiyat", v: string) => degisti(satirlar.map((s, j) => (j === i ? { ...s, [alan]: v } : s)));
  const iskDegis = (i: number, k: number, v: string) =>
    degisti(satirlar.map((s, j) => (j === i ? { ...s, isk: s.isk.map((x, m) => (m === k ? v : x)) } : s)));
  const enter = (e: React.KeyboardEvent) => { if (e.key === "Enter") ekle(e as unknown as React.FormEvent); };

  return (
    <>
      <form className="filtre satir-giris" onSubmit={(e) => { e.preventDefault(); urun ? ekle() : getir(kod); }}>
        <div className="cari-sec">
          <Alan etiket="Stok kodu / barkod ya da ürün adı">
            <input ref={kodRef} value={kod} autoComplete="off" placeholder="Okutun ya da yazın"
              onChange={(e) => { setKod(e.target.value); setUrun(null); setAramaAcik(true); }} />
          </Alan>
          {arama.veri && arama.veri.length > 0 && aramaAcik && !urun && (
            <div className="liste acilir">
              {arama.veri.map((u) => (
                <button type="button" key={u.kod} className="liste-satir" onClick={() => getir(u.kod)}>
                  <span>{u.ad}<small>{u.kod}</small></span><small>{u.birim}</small>
                </button>
              ))}
            </div>
          )}
        </div>
        <Alan etiket="Ürün adı"><input className="urun-adi" value={urun?.ad ?? ""} readOnly tabIndex={-1} /></Alan>
        <Alan etiket="Birim"><input className="birim" value={urun?.birim ?? ""} readOnly tabIndex={-1} /></Alan>
        <Alan etiket="Miktar"><input ref={miktarRef} className="adet" inputMode="decimal" value={miktarG} onChange={(e) => setMiktarG(e.target.value)} onKeyDown={enter} /></Alan>
        <Alan etiket={`${fiyatEtiketi} ${kdvDahil ? "(KDV dahil)" : "(KDV hariç)"}`}><input className="fiyat" inputMode="decimal" value={fiyatG} onChange={(e) => setFiyatG(e.target.value)} onKeyDown={enter} /></Alan>
        <Alan etiket="İsk. %"><input className="isk" inputMode="decimal" value={iskG} onChange={(e) => setIskG(e.target.value)} onKeyDown={enter} /></Alan>
        <button type="submit">{urun ? "Ekle" : "Getir"}</button>
        {(urun || kod) && <button type="button" className="ikincil" onClick={() => { temizle(); setHata(null); }}>Temizle</button>}
      </form>
      {hata && <div className="uyari hata">{hata}</div>}
      <table>
        <thead><tr><th>#</th><th>Stok kodu</th><th>Ürün adı</th><th>Birim</th><th className="s">Miktar</th><th className="s">{fiyatEtiketi} {kdvDahil ? "(KDV dahil)" : "(KDV hariç)"}</th>{BOS_ISK.map((_, k) => <th key={k} className="s" title="Zincirleme: bir öncekinden kalan tutara uygulanır">İsk.{k + 1} %</th>)}<th className="s">KDV %</th><th className="s">Tutar</th><th></th></tr></thead>
        <tbody>
          {satirlar.length === 0 && <tr><td colSpan={14} className="bos">Yukarıdan ürün ekleyin.</td></tr>}
          {satirlar.map((s, i) => (
            <tr key={i}>
              <td>{i + 1}</td><td>{s.kod}</td><td>{s.ad}</td><td>{s.birim}</td>
              <td className="s"><input className="adet" inputMode="decimal" value={s.miktar} onChange={(e) => degis(i, "miktar", e.target.value)} /></td>
              <td className="s"><input className="fiyat" inputMode="decimal" value={s.fiyat} onChange={(e) => degis(i, "fiyat", e.target.value)} /></td>
              {s.isk.map((y, k) => <td key={k} className="s"><input className="isk" inputMode="decimal" value={y} onChange={(e) => iskDegis(i, k, e.target.value)} /></td>)}
              <td className="s">{s.kdv != null ? `%${s.kdv}` : ""}</td>
              <td className="s">{para(satirTutar(s))}</td>
              <td><button className="sil" title="Çıkar" onClick={() => degisti(satirlar.filter((_, j) => j !== i))}>×</button></td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  );
}

/** Alt toplamlar: KDV oranı bilinen satırlar için KDV ve genel toplam (kesin tutar kayıttan sonra Mikro'dan döner). */
function Toplamlar({ satirlar, kdvDahil }: { satirlar: Satir[]; kdvDahil: boolean }) {
  const tutar = satirlar.reduce((s, x) => s + satirTutar(x), 0);
  const kdv = satirlar.reduce((s, x) => s + satirKdv(x, kdvDahil), 0);
  const miktarT = satirlar.reduce((s, x) => s + n(x.miktar), 0);
  const haric = kdvDahil ? tutar - kdv : tutar;
  return (
    <div className="toplamlar">
      <span>{satirlar.length} satır, {miktarT.toLocaleString("tr-TR")} adet</span>
      <span>KDV hariç <b>{para(haric)}</b></span>
      <span>KDV <b>{para(kdv)}</b></span>
      <span>Genel toplam <b>{para(haric + kdv)}</b> TL</span>
    </div>
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

  // Önerilen fiyat: son alış (iskontolar düşülmüş).
  const fiyatGetir = alisFiyati(gun, kdvDahil);

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
      <Satirlar satirlar={satirlar} degisti={setSatirlar} fiyatGetir={fiyatGetir} kdvDahil={kdvDahil} fiyatEtiketi="Alış fiyatı" />
      <Toplamlar satirlar={satirlar} kdvDahil={kdvDahil} />
      <MesajKutusu mesaj={mesaj} />
      <div className="kaydet">
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
  const fiyatGetir = async (b: string): Promise<Getirilen> => {
    const [r, u] = await Promise.all([
      api.get<{ urun: { stokKodu: string; urunAdi: string; birim: string | null; fiyat: number } }>(`/api/stok/${encodeURIComponent(b)}?miktar=false`),
      api.get<UrunBilgi>(`/api/fatura/urun?${qs({ k: b })}`),
    ]);
    const f = kdvDahil ? r.urun.fiyat : r.urun.fiyat / (1 + u.kdvOrani / 100);
    return { kod: r.urun.stokKodu, ad: r.urun.urunAdi, birim: r.urun.birim ?? "", fiyat: sayiYaz(f), kdv: u.kdvOrani };
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

  return (
    <>
      <p className="not">Toptan satış faturası. Stoktan düşer, cariye borç yazılır. Fiyat satış listesinden (KDV dahil) gelir, değiştirebilirsiniz.</p>
      <div className="filtre">
        <CariSec deger={cari} degisti={setCari} etiket="Müşteri" />
        <DepoSec etiket="Depo" deger={depo} degisti={setDepo} kilitli={depoNo != null} />
        <Alan etiket="Tarih"><input type="date" value={gun} max={bugun()} onChange={(e) => setGun(e.target.value)} /></Alan>
        <label className="kutu"><input type="checkbox" checked={kdvDahil} onChange={(e) => setKdvDahil(e.target.checked)} /> Fiyatlar KDV dahil</label>
      </div>
      <Satirlar satirlar={satirlar} degisti={setSatirlar} fiyatGetir={fiyatGetir} kdvDahil={kdvDahil} fiyatEtiketi="Satış fiyatı" />
      <Toplamlar satirlar={satirlar} kdvDahil={kdvDahil} />
      <MesajKutusu mesaj={mesaj} />
      <div className="kaydet">
        <button disabled={!cari || !depo || !satirGecerli(satirlar) || mesgul} onClick={kaydet}>{mesgul ? "Kaydediliyor…" : "Faturayı kaydet"}</button>
      </div>
    </>
  );
}

function AlisFaturasi({ depoNo }: { depoNo: number | null }) {
  const [cari, setCari] = useState<CariOzet | null>(null);
  const [depo, setDepo] = useState<number | "">(depoNo ?? "");
  const [gun, setGun] = useState(bugun());
  const [belgeNo, setBelgeNo] = useState("");
  const [belgeGun, setBelgeGun] = useState(bugun());
  const [kdvDahil, setKdvDahil] = useState(false);
  const [satirlar, setSatirlar] = useState<Satir[]>([]);
  const [mesaj, setMesaj] = useState<Mesaj>(null);
  const [mesgul, setMesgul] = useState(false);

  async function kaydet() {
    setMesgul(true);
    try {
      const r = await api.post<FaturaSonuc>("/api/fatura/alis", {
        cariKod: cari!.kod, depoNo: depo, tarih: gun, belgeNo: belgeNo.trim(), belgeTarih: belgeGun, satirlar: satirIstek(satirlar, kdvDahil),
      });
      setMesaj({ tur: "bilgi", metin: `Alış faturası ${r.sira} kaydedildi: KDV hariç ${para(r.araToplam - r.iskonto)} TL, KDV ${para(r.kdv)} TL, toplam ${para(r.toplam)} TL. Stoğa girdi, cariye alacak yazıldı.` });
      setSatirlar([]);
      setBelgeNo("");
    } catch (err) {
      setMesaj({ tur: "hata", metin: (err as Error).message });
    }
    setMesgul(false);
  }

  return (
    <>
      <p className="not">Mikro'daki Alış Faturası (062205): irsaliyesiz gelen faturayı doğrudan girin. Ürünler stoğa girer, tedarikçiye alacak yazılır. İrsaliyesi önceden girilmişse "Bekleyen irsaliyeler"i kullanın.</p>
      <div className="filtre">
        <CariSec deger={cari} degisti={setCari} etiket="Tedarikçi (cari)" />
        <DepoSec etiket="Depo" deger={depo} degisti={setDepo} kilitli={depoNo != null} />
        <Alan etiket="Evrak tarihi"><input type="date" value={gun} max={bugun()} onChange={(e) => setGun(e.target.value)} /></Alan>
        <Alan etiket="Fatura no (belge)"><input className="kisa" value={belgeNo} onChange={(e) => setBelgeNo(e.target.value)} /></Alan>
        <Alan etiket="Fatura tarihi"><input type="date" value={belgeGun} max={bugun()} onChange={(e) => setBelgeGun(e.target.value)} /></Alan>
        <label className="kutu"><input type="checkbox" checked={kdvDahil} onChange={(e) => setKdvDahil(e.target.checked)} /> Fiyatlar KDV dahil</label>
      </div>
      <Satirlar satirlar={satirlar} degisti={setSatirlar} fiyatGetir={alisFiyati(gun, kdvDahil)} kdvDahil={kdvDahil} fiyatEtiketi="Alış fiyatı" />
      <Toplamlar satirlar={satirlar} kdvDahil={kdvDahil} />
      <MesajKutusu mesaj={mesaj} />
      <div className="kaydet">
        {!belgeNo.trim() && satirlar.length > 0 && <span className="not">Fatura numarasını yazın.</span>}
        <button disabled={!cari || !depo || !belgeNo.trim() || !satirGecerli(satirlar) || mesgul} onClick={kaydet}>{mesgul ? "Kaydediliyor…" : "Faturayı kaydet"}</button>
      </div>
    </>
  );
}

type AlisOzet = { guid: string; tarih: string; seri: string; sira: number; belgeNo: string; belgeTarih: string; cariKod: string; cariAdi: string;
  iade: boolean; satirSayisi: number; depoNo: number | null; araToplam: number; iskonto: number; kdv: number; toplam: number };
type AlisSatiri = { satirNo: number; stokKodu: string; urunAdi: string; birim: string; miktar: number; birimFiyat: number; tutar: number; iskonto: number; kdv: number };

function FaturaSatirlari({ guid }: { guid: string }) {
  const d = useVeri<AlisSatiri[]>(`/api/fatura/alis-faturalari/${guid}`);
  return (
    <>
      <Durum hata={d.hata} yukleniyor={d.yukleniyor} />
      {d.veri && (
        <div className="evrak-ici"><table className="ic">
          <thead><tr><th>Stok kodu</th><th>Ürün adı</th><th>Birim</th><th className="s">Miktar</th><th className="s">Birim fiyat</th><th className="s">Tutar</th><th className="s">İskonto</th><th className="s">KDV</th></tr></thead>
          <tbody>
            {d.veri.length === 0 && <tr><td colSpan={8} className="bos">Ürün satırı yok (hizmet / masraf faturası).</td></tr>}
            {d.veri.map((s) => (
              <tr key={s.satirNo}><td>{s.stokKodu}</td><td>{s.urunAdi}</td><td>{s.birim}</td><td className="s">{miktar(s.miktar)}</td>
                <td className="s">{para(s.birimFiyat)}</td><td className="s">{para(s.tutar)}</td><td className="s">{para(s.iskonto)}</td><td className="s">{para(s.kdv)}</td></tr>
            ))}
          </tbody>
        </table></div>
      )}
    </>
  );
}

function AlisFaturalari() {
  const [bas, setBas] = useState(gunEkle(bugun(), -30));
  const [bit, setBit] = useState(bugun());
  const [cari, setCari] = useState<CariOzet | null>(null);
  const [acik, setAcik] = useState<string | null>(null);
  const liste = useVeri<AlisOzet[]>(`/api/fatura/alis-faturalari?${qs({ bas, bit, cari: cari?.kod })}`);
  const v = liste.veri ?? [];
  const isaret = (f: AlisOzet) => (f.iade ? -1 : 1);
  return (
    <>
      <p className="not">Mikro'daki Alış Faturaları Yönetimi (028012): dönemdeki alış ve alış iade faturaları. Satıra tıklayınca ürünleri açılır.</p>
      <div className="filtre">
        <Alan etiket="Başlangıç"><input type="date" value={bas} onChange={(e) => setBas(e.target.value)} /></Alan>
        <Alan etiket="Bitiş"><input type="date" value={bit} onChange={(e) => setBit(e.target.value)} /></Alan>
        <CariSec deger={cari} degisti={setCari} etiket="Tedarikçi (isteğe bağlı)" />
        <button type="button" className="ikincil" disabled={v.length === 0} onClick={() => csvIndir(`alis_faturalari_${bas}_${bit}.csv`,
          ["Tarih", "Evrak", "Fatura no", "Fatura tarihi", "Cari kod", "Cari", "İade", "Depo", "Ara toplam", "İskonto", "KDV", "Toplam"],
          v.map((f) => [tarih(f.tarih), `${f.seri}${f.seri ? "-" : ""}${f.sira}`, f.belgeNo, tarih(f.belgeTarih), f.cariKod, f.cariAdi, f.iade ? "Evet" : "", f.depoNo, f.araToplam, f.iskonto, f.kdv, f.toplam]))}>Excel'e (CSV) indir</button>
      </div>
      <Durum hata={liste.hata} yukleniyor={liste.yukleniyor} />
      <table>
        <thead><tr><th>Tarih</th><th>Evrak</th><th>Fatura no</th><th>Tedarikçi</th><th>Depo</th><th className="s">Satır</th><th className="s">Ara toplam</th><th className="s">İskonto</th><th className="s">KDV</th><th className="s">Toplam</th></tr></thead>
        <tbody>
          {liste.veri?.length === 0 && <tr><td colSpan={10} className="bos">Bu dönemde alış faturası yok.</td></tr>}
          {v.map((f) => [
            <tr key={f.guid} className={"tiklanir" + (acik === f.guid ? " secili" : "")} onClick={() => setAcik(acik === f.guid ? null : f.guid)}>
              <td>{tarih(f.tarih)}</td><td>{f.seri}{f.seri && "-"}{f.sira}{f.iade && <> <span className="pill">İade</span></>}</td>
              <td>{f.belgeNo}{f.belgeTarih.slice(0, 10) !== f.tarih.slice(0, 10) && <><br /><small>{tarih(f.belgeTarih)}</small></>}</td>
              <td>{f.cariAdi}<br /><small>{f.cariKod}</small></td><td>{f.depoNo ?? ""}</td><td className="s">{f.satirSayisi}</td>
              <td className="s">{para(f.araToplam)}</td><td className="s">{para(f.iskonto)}</td><td className="s">{para(f.kdv)}</td><td className="s"><b>{para(f.toplam)}</b></td>
            </tr>,
            acik === f.guid && <tr key={f.guid + "-d"}><td colSpan={10}><FaturaSatirlari guid={f.guid} /></td></tr>,
          ])}
        </tbody>
        {v.length > 0 && (
          <tfoot><tr><td colSpan={6}>{v.length} fatura (iadeler düşülmüş)</td>
            <td className="s">{para(v.reduce((s, f) => s + isaret(f) * f.araToplam, 0))}</td>
            <td className="s">{para(v.reduce((s, f) => s + isaret(f) * f.iskonto, 0))}</td>
            <td className="s">{para(v.reduce((s, f) => s + isaret(f) * f.kdv, 0))}</td>
            <td className="s"><b>{para(v.reduce((s, f) => s + isaret(f) * f.toplam, 0))}</b></td></tr></tfoot>
        )}
      </table>
    </>
  );
}

export type FaturaSekme = "alis" | "irsaliye" | "bekleyen" | "alisfaturalari" | "satis";

export default function Fatura({ depoNo, oturum, ilkSekme }: { depoNo: number | null; oturum: Oturum; ilkSekme?: string }) {
  const alis = yetkili(oturum, "alis_fatura");
  const sekmeler = ([
    { id: "alis", ad: "Alış faturası", var: alis },
    { id: "irsaliye", ad: "Alış irsaliyesi", var: alis },
    { id: "bekleyen", ad: "Bekleyen irsaliyeler → alış faturası", var: alis },
    { id: "alisfaturalari", ad: "Alış faturaları yönetimi", var: alis },
    { id: "satis", ad: "Satış faturası", var: yetkili(oturum, "satis_fatura") },
  ] as const).filter((s) => s.var);
  const [sekme, setSekme] = useState<FaturaSekme>(sekmeler.find((s) => s.id === ilkSekme)?.id ?? sekmeler[0]?.id ?? "alis");
  useEffect(() => { const s = sekmeler.find((x) => x.id === ilkSekme); if (s) setSekme(s.id); }, [ilkSekme]); // eslint-disable-line react-hooks/exhaustive-deps
  return (
    <section>
      <header className="sayfa-baslik"><h1>Fatura ve irsaliye</h1></header>
      <YazmaDurumu />
      <div className="sekmeler">
        {sekmeler.map((s) => <button key={s.id} className={sekme === s.id ? "aktif" : ""} onClick={() => setSekme(s.id)}>{s.ad}</button>)}
      </div>
      {sekme === "alis" && <AlisFaturasi depoNo={depoNo} />}
      {sekme === "irsaliye" && <AlisIrsaliyesi depoNo={depoNo} />}
      {sekme === "bekleyen" && <BekleyenIrsaliyeler />}
      {sekme === "alisfaturalari" && <AlisFaturalari />}
      {sekme === "satis" && <SatisFaturasi depoNo={depoNo} />}
    </section>
  );
}
