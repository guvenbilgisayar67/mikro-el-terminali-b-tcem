import { useEffect, useRef, useState } from "react";
import { api, bugun, csvIndir, gunEkle, Oturum, para, qs, sayiOku, tarih, yetkili } from "../api";
import { Alan, CariOzet, CariSec, Durum, Mesaj, MesajKutusu, useVeri, YazmaDurumu } from "../ortak";

type Hesap = { kod: string; ad: string };
type Sonuc = { evrakTip: number; sira: number; satirSayisi: number; toplam: number; refNolar?: string[] | null };
type FoyHareketi = { guid: string; tarih: string; evrakTip: number; evrak: string; evrakAdi: string; belgeNo: string; karsiHesap: string; aciklama: string; giris: number; cikis: number; bakiye: number };
type Foy = { kasa: Hesap; devir: number; giris: number; cikis: number; bakiye: number; hareketler: FoyHareketi[] };

const n = (s: string) => sayiOku(s) || 0;
const CINSLER: [number, string][] = [[0, "Cari"], [4, "Kasa"], [5, "Masraf"]];
const cinsAdi = (c: number) => CINSLER.find((x) => x[0] === c)?.[1] ?? "";

function HesapListesi({ yol, etiket, deger, degisti }: { yol: string; etiket: string; deger: string; degisti: (k: string) => void }) {
  const l = useVeri<Hesap[]>(yol);
  return (
    <Alan etiket={etiket}>
      <select value={deger} onChange={(e) => degisti(e.target.value)}>
        <option value="" disabled>Seçin</option>
        {l.veri?.map((h) => <option key={h.kod} value={h.kod}>{h.kod} · {h.ad.trim()}</option>)}
      </select>
    </Alan>
  );
}
const KasaSec = (p: { etiket?: string; deger: string; degisti: (k: string) => void }) => <HesapListesi yol="/api/kasa/kasalar" etiket={p.etiket ?? "Kasa"} deger={p.deger} degisti={p.degisti} />;
const BankaSec = (p: { etiket?: string; deger: string; degisti: (k: string) => void }) => <HesapListesi yol="/api/kasa/bankalar" etiket={p.etiket ?? "Banka"} deger={p.deger} degisti={p.degisti} />;

function Kaydet({ toplam, adet, hazir, mesgul, kaydet, yazi }: { toplam: number; adet: number; hazir: boolean; mesgul: boolean; kaydet: () => void; yazi: string }) {
  return (
    <>
      <div className="toplamlar"><span>{adet} satır</span><span>Toplam <b>{para(toplam)}</b> TL</span></div>
      <div className="kaydet"><button disabled={!hazir || mesgul} onClick={kaydet}>{mesgul ? "Kaydediliyor…" : yazi}</button></div>
    </>
  );
}

const hataMesaji = (err: unknown): Mesaj => ({ tur: "hata", metin: (err as Error).message });

/** Kasa tahsilat (051100) ve kasa ödeme (051200) fişi: üstte kasa, satırlarda karşı hesap (cari / kasa / masraf). */
function KasaFisi({ tur }: { tur: "Tahsilat" | "Odeme" }) {
  type Satir = { cins: number; kod: string; ad: string; tutar: string; aciklama: string };
  const [kasa, setKasa] = useState("");
  const [gun, setGun] = useState(bugun());
  const [belgeNo, setBelgeNo] = useState("");
  const [satirlar, setSatirlar] = useState<Satir[]>([]);
  const [cins, setCins] = useState(tur === "Tahsilat" ? 4 : 5);
  const [ara, setAra] = useState("");
  const [hesap, setHesap] = useState<Hesap | null>(null);
  const [tutar, setTutar] = useState("");
  const [aciklama, setAciklama] = useState("");
  const [acik, setAcik] = useState(false);
  const [mesaj, setMesaj] = useState<Mesaj>(null);
  const [mesgul, setMesgul] = useState(false);
  const tutarRef = useRef<HTMLInputElement>(null);
  const araRef = useRef<HTMLInputElement>(null);
  const liste = useVeri<Hesap[]>(acik && !hesap && (cins !== 0 || ara.trim().length >= 2) ? `/api/kasa/hesap-ara?${qs({ cins, q: ara.trim() })}` : null);

  function sec(h: Hesap) { setHesap(h); setAra(h.kod); setAcik(false); setTimeout(() => tutarRef.current?.focus(), 0); }
  function ekle(e?: React.FormEvent) {
    e?.preventDefault();
    if (!hesap) { setMesaj({ tur: "hata", metin: "Listeden bir hesap seçin." }); return; }
    if (n(tutar) <= 0) { setMesaj({ tur: "hata", metin: "Meblağ sıfırdan büyük olmalı." }); return; }
    setSatirlar([...satirlar, { cins, kod: hesap.kod, ad: hesap.ad, tutar, aciklama }]);
    setHesap(null); setAra(""); setTutar(""); setMesaj(null);
    araRef.current?.focus();
  }
  async function kaydet() {
    setMesgul(true);
    try {
      const r = await api.post<Sonuc>("/api/kasa/fis", {
        tur, kasaKod: kasa, tarih: gun, belgeNo: belgeNo || null,
        satirlar: satirlar.map((s) => ({ cins: s.cins, kod: s.kod, tutar: n(s.tutar), aciklama: s.aciklama || null })),
      });
      setMesaj({ tur: "bilgi", metin: `${tur === "Tahsilat" ? "Kasa tahsilat" : "Kasa ödeme"} fişi ${r.sira} kaydedildi: ${r.satirSayisi} satır, ${para(r.toplam)} TL.` });
      setSatirlar([]); setBelgeNo("");
    } catch (err) { setMesaj(hataMesaji(err)); }
    setMesgul(false);
  }
  const degis = (i: number, alan: "tutar" | "aciklama", v: string) => setSatirlar(satirlar.map((s, j) => (j === i ? { ...s, [alan]: v } : s)));
  const toplam = satirlar.reduce((t, s) => t + n(s.tutar), 0);

  return (
    <>
      <p className="not">{tur === "Tahsilat"
        ? "Mikro'daki Kasa Tahsilat Fişi (051100): seçilen kasaya para girer; satırdaki hesap (cari, başka kasa ya da masraf) alacaklanır."
        : "Mikro'daki Kasa Ödeme Fişi (051200): seçilen kasadan para çıkar; satırdaki hesap (masraf, cari ya da başka kasa) borçlanır."}</p>
      <div className="filtre">
        <KasaSec deger={kasa} degisti={setKasa} />
        <Alan etiket="Tarih"><input type="date" value={gun} max={bugun()} onChange={(e) => setGun(e.target.value)} /></Alan>
        <Alan etiket="Belge no"><input className="kisa" value={belgeNo} onChange={(e) => setBelgeNo(e.target.value)} /></Alan>
      </div>
      <form className="filtre satir-giris" onSubmit={ekle}>
        <Alan etiket="Cinsi">
          <select value={cins} onChange={(e) => { setCins(+e.target.value); setHesap(null); setAra(""); }}>
            {CINSLER.map(([k, a]) => <option key={k} value={k}>{a}</option>)}
          </select>
        </Alan>
        <div className="cari-sec">
          <Alan etiket="Hesap kodu"><input ref={araRef} value={ara} autoComplete="off" placeholder={cins === 0 ? "Kod ya da unvan" : "Kod ya da ad"}
            onFocus={() => setAcik(true)} onChange={(e) => { setAra(e.target.value); setHesap(null); setAcik(true); }} /></Alan>
          {acik && !hesap && liste.veri && liste.veri.length > 0 && (
            <div className="liste acilir">
              {liste.veri.map((h) => (
                <button type="button" key={h.kod} className="liste-satir" onClick={() => sec(h)}><span>{h.ad || "(adı yok)"}<small>{h.kod}</small></span></button>
              ))}
            </div>
          )}
        </div>
        <Alan etiket="Hesap adı"><input className="urun-adi" value={hesap?.ad ?? ""} readOnly tabIndex={-1} /></Alan>
        <Alan etiket="Meblağ"><input ref={tutarRef} className="fiyat" inputMode="decimal" value={tutar} onChange={(e) => setTutar(e.target.value)} /></Alan>
        <Alan etiket="Açıklama"><input value={aciklama} maxLength={40} onChange={(e) => setAciklama(e.target.value)} /></Alan>
        <button type="submit">Ekle</button>
      </form>
      <table>
        <thead><tr><th>#</th><th>Cinsi</th><th>Hesap kodu</th><th>Hesap adı</th><th className="s">Meblağ</th><th>Açıklama</th><th></th></tr></thead>
        <tbody>
          {satirlar.length === 0 && <tr><td colSpan={7} className="bos">Yukarıdan satır ekleyin.</td></tr>}
          {satirlar.map((s, i) => (
            <tr key={i}>
              <td>{i + 1}</td><td>{cinsAdi(s.cins)}</td><td>{s.kod}</td><td>{s.ad}</td>
              <td className="s"><input className="fiyat" inputMode="decimal" value={s.tutar} onChange={(e) => degis(i, "tutar", e.target.value)} /></td>
              <td><input value={s.aciklama} maxLength={40} onChange={(e) => degis(i, "aciklama", e.target.value)} /></td>
              <td><button className="sil" title="Çıkar" onClick={() => setSatirlar(satirlar.filter((_, j) => j !== i))}>×</button></td>
            </tr>
          ))}
        </tbody>
      </table>
      <MesajKutusu mesaj={mesaj} />
      <Kaydet toplam={toplam} adet={satirlar.length} hazir={!!kasa && satirlar.length > 0 && satirlar.every((s) => n(s.tutar) > 0)} mesgul={mesgul} kaydet={kaydet} yazi="Fişi kaydet" />
    </>
  );
}

/** Kasadan bankaya nakit yatırma makbuzu (072520): üstte banka, satırlarda kasa ve meblağ. */
function BankayaYatirma() {
  type Satir = { kasa: string; tutar: string; aciklama: string };
  const [banka, setBanka] = useState("");
  const [gun, setGun] = useState(bugun());
  const [belgeNo, setBelgeNo] = useState("");
  const [satirlar, setSatirlar] = useState<Satir[]>([{ kasa: "", tutar: "", aciklama: "" }]);
  const [mesaj, setMesaj] = useState<Mesaj>(null);
  const [mesgul, setMesgul] = useState(false);
  const kasalar = useVeri<Hesap[]>("/api/kasa/kasalar");

  async function kaydet() {
    setMesgul(true);
    try {
      const r = await api.post<Sonuc>("/api/kasa/bankaya-yatirma", {
        bankaKod: banka, tarih: gun, belgeNo: belgeNo || null,
        satirlar: satirlar.map((s) => ({ kasaKod: s.kasa, tutar: n(s.tutar), aciklama: s.aciklama || null })),
      });
      setMesaj({ tur: "bilgi", metin: `Kasadan bankaya yatırma makbuzu ${r.sira} kaydedildi: ${para(r.toplam)} TL.` });
      setSatirlar([{ kasa: "", tutar: "", aciklama: "" }]); setBelgeNo("");
    } catch (err) { setMesaj(hataMesaji(err)); }
    setMesgul(false);
  }
  const degis = (i: number, alan: keyof Satir, v: string) => setSatirlar(satirlar.map((s, j) => (j === i ? { ...s, [alan]: v } : s)));
  const toplam = satirlar.reduce((t, s) => t + n(s.tutar), 0);
  return (
    <>
      <p className="not">Mikro'daki Kasadan Bankaya Nakit Yatırma Makbuzu (072520): satırdaki kasalardan çıkan nakit seçilen bankaya girer.</p>
      <div className="filtre">
        <BankaSec deger={banka} degisti={setBanka} />
        <Alan etiket="Tarih"><input type="date" value={gun} max={bugun()} onChange={(e) => setGun(e.target.value)} /></Alan>
        <Alan etiket="Belge no"><input className="kisa" value={belgeNo} onChange={(e) => setBelgeNo(e.target.value)} /></Alan>
      </div>
      <table>
        <thead><tr><th>#</th><th>Kasa</th><th className="s">Meblağ</th><th>Açıklama</th><th></th></tr></thead>
        <tbody>
          {satirlar.map((s, i) => (
            <tr key={i}>
              <td>{i + 1}</td>
              <td><select value={s.kasa} onChange={(e) => degis(i, "kasa", e.target.value)}>
                <option value="" disabled>Seçin</option>
                {kasalar.veri?.map((h) => <option key={h.kod} value={h.kod}>{h.kod} · {h.ad.trim()}</option>)}
              </select></td>
              <td className="s"><input className="fiyat" inputMode="decimal" value={s.tutar} onChange={(e) => degis(i, "tutar", e.target.value)} /></td>
              <td><input value={s.aciklama} maxLength={40} onChange={(e) => degis(i, "aciklama", e.target.value)} /></td>
              <td>{satirlar.length > 1 && <button className="sil" title="Çıkar" onClick={() => setSatirlar(satirlar.filter((_, j) => j !== i))}>×</button>}</td>
            </tr>
          ))}
        </tbody>
      </table>
      <button className="ikincil" style={{ marginTop: 8 }} onClick={() => setSatirlar([...satirlar, { kasa: "", tutar: "", aciklama: "" }])}>+ Satır</button>
      <MesajKutusu mesaj={mesaj} />
      <Kaydet toplam={toplam} adet={satirlar.length} hazir={!!banka && satirlar.every((s) => s.kasa && n(s.tutar) > 0)} mesgul={mesgul} kaydet={kaydet} yazi="Makbuzu kaydet" />
    </>
  );
}

/** Tediye makbuzu (042210), havale ile: üstte cari, satırlarda banka, vade, meblağ. */
function TediyeMakbuzu() {
  type Satir = { banka: string; vade: string; tutar: string; aciklama: string };
  const bos = (): Satir => ({ banka: "", vade: bugun(), tutar: "", aciklama: "HAVALE" });
  const [cari, setCari] = useState<CariOzet | null>(null);
  const [gun, setGun] = useState(bugun());
  const [belgeNo, setBelgeNo] = useState("");
  const [satirlar, setSatirlar] = useState<Satir[]>([bos()]);
  const [mesaj, setMesaj] = useState<Mesaj>(null);
  const [mesgul, setMesgul] = useState(false);
  const bankalar = useVeri<Hesap[]>("/api/kasa/bankalar");

  async function kaydet() {
    setMesgul(true);
    try {
      const r = await api.post<Sonuc>("/api/kasa/tediye", {
        cariKod: cari!.kod, tarih: gun, belgeNo: belgeNo || null,
        satirlar: satirlar.map((s) => ({ bankaKod: s.banka, vade: s.vade, tutar: n(s.tutar), aciklama: s.aciklama || null })),
      });
      setMesaj({ tur: "bilgi", metin: `Tediye makbuzu ${r.sira} kaydedildi: ${para(r.toplam)} TL, havale emri ${r.refNolar?.join(", ")}. Cariye borç yazıldı.` });
      setSatirlar([bos()]); setBelgeNo("");
    } catch (err) { setMesaj(hataMesaji(err)); }
    setMesgul(false);
  }
  const degis = (i: number, alan: keyof Satir, v: string) => setSatirlar(satirlar.map((s, j) => (j === i ? { ...s, [alan]: v } : s)));
  const toplam = satirlar.reduce((t, s) => t + n(s.tutar), 0);
  return (
    <>
      <p className="not">Mikro'daki Tediye Makbuzu (042210), firma havale emriyle: tedarikçiye bankadan yapılan ödeme. Cariye borç yazılır, Mikro'da havale emri (FH-…) açılır.</p>
      <div className="filtre">
        <CariSec deger={cari} degisti={setCari} etiket="Cari hesap" />
        <Alan etiket="Tarih"><input type="date" value={gun} max={bugun()} onChange={(e) => setGun(e.target.value)} /></Alan>
        <Alan etiket="Belge no"><input className="kisa" value={belgeNo} onChange={(e) => setBelgeNo(e.target.value)} /></Alan>
      </div>
      <table>
        <thead><tr><th>#</th><th>Cinsi</th><th>Banka</th><th>Vade</th><th className="s">Meblağ</th><th>Açıklama</th><th></th></tr></thead>
        <tbody>
          {satirlar.map((s, i) => (
            <tr key={i}>
              <td>{i + 1}</td><td>Firma havale emri</td>
              <td><select value={s.banka} onChange={(e) => degis(i, "banka", e.target.value)}>
                <option value="" disabled>Seçin</option>
                {bankalar.veri?.map((h) => <option key={h.kod} value={h.kod}>{h.kod} · {h.ad.trim()}</option>)}
              </select></td>
              <td><input type="date" value={s.vade} onChange={(e) => degis(i, "vade", e.target.value)} /></td>
              <td className="s"><input className="fiyat" inputMode="decimal" value={s.tutar} onChange={(e) => degis(i, "tutar", e.target.value)} /></td>
              <td><input value={s.aciklama} maxLength={40} onChange={(e) => degis(i, "aciklama", e.target.value)} /></td>
              <td>{satirlar.length > 1 && <button className="sil" title="Çıkar" onClick={() => setSatirlar(satirlar.filter((_, j) => j !== i))}>×</button>}</td>
            </tr>
          ))}
        </tbody>
      </table>
      <button className="ikincil" style={{ marginTop: 8 }} onClick={() => setSatirlar([...satirlar, bos()])}>+ Satır</button>
      <MesajKutusu mesaj={mesaj} />
      <Kaydet toplam={toplam} adet={satirlar.length} hazir={!!cari && satirlar.every((s) => s.banka && s.vade && n(s.tutar) > 0)} mesgul={mesgul} kaydet={kaydet} yazi="Makbuzu kaydet" />
    </>
  );
}

/** Kasa hareket föyü (051400). */
function KasaFoyu() {
  const [kasa, setKasa] = useState("");
  const [bas, setBas] = useState(gunEkle(bugun(), -30));
  const [bit, setBit] = useState(bugun());
  const f = useVeri<Foy>(kasa ? `/api/kasa/foy?${qs({ kasa, bas, bit })}` : null);
  const v = f.veri;
  return (
    <>
      <p className="not">Mikro'daki Kasa Hareket Föyü (051400): kasanın dönemdeki giriş ve çıkışları, yürüyen bakiyeyle.</p>
      <div className="filtre">
        <KasaSec deger={kasa} degisti={setKasa} />
        <Alan etiket="Başlangıç"><input type="date" value={bas} onChange={(e) => setBas(e.target.value)} /></Alan>
        <Alan etiket="Bitiş"><input type="date" value={bit} onChange={(e) => setBit(e.target.value)} /></Alan>
        <button type="button" className="ikincil" disabled={!v?.hareketler.length} onClick={() => v && csvIndir(`kasa_foyu_${v.kasa.kod}_${bas}_${bit}.csv`,
          ["Tarih", "Evrak", "Evrak türü", "Belge no", "Karşı hesap", "Açıklama", "Giriş", "Çıkış", "Bakiye"],
          v.hareketler.map((h) => [tarih(h.tarih), h.evrak, h.evrakAdi, h.belgeNo, h.karsiHesap, h.aciklama, h.giris, h.cikis, h.bakiye]))}>Excel'e (CSV) indir</button>
      </div>
      {!kasa && <p className="not">Bir kasa seçin.</p>}
      <Durum hata={f.hata} yukleniyor={f.yukleniyor} />
      {v && (
        <table>
          <thead><tr><th>Tarih</th><th>Evrak</th><th>Evrak türü</th><th>Belge no</th><th>Karşı hesap</th><th>Açıklama</th><th className="s">Giriş</th><th className="s">Çıkış</th><th className="s">Bakiye</th></tr></thead>
          <tbody>
            <tr><td colSpan={8}><b>Devir ({tarih(bas)} öncesi)</b></td><td className="s"><b>{para(v.devir)}</b></td></tr>
            {v.hareketler.length === 0 && <tr><td colSpan={9} className="bos">Bu dönemde hareket yok.</td></tr>}
            {v.hareketler.map((h) => (
              <tr key={h.guid}>
                <td>{tarih(h.tarih)}</td><td>{h.evrak}</td><td>{h.evrakAdi}</td><td>{h.belgeNo}</td><td>{h.karsiHesap}</td><td>{h.aciklama}</td>
                <td className="s">{h.giris ? para(h.giris) : ""}</td><td className="s">{h.cikis ? para(h.cikis) : ""}</td><td className="s">{para(h.bakiye)}</td>
              </tr>
            ))}
          </tbody>
          <tfoot><tr><td colSpan={6}>Dönem toplamı</td><td className="s">{para(v.giris)}</td><td className="s">{para(v.cikis)}</td><td className="s">{para(v.bakiye)}</td></tr></tfoot>
        </table>
      )}
    </>
  );
}

type KasaSekme = "tahsilat" | "odeme" | "bankaya" | "tediye" | "foy";

export default function Kasa({ oturum, ilkSekme }: { oturum: Oturum; ilkSekme?: string }) {
  const kasa = yetkili(oturum, "kasa");
  const sekmeler = ([
    { id: "tahsilat", ad: "Kasa tahsilat fişi", var: kasa },
    { id: "odeme", ad: "Kasa ödeme fişi", var: kasa },
    { id: "bankaya", ad: "Kasadan bankaya yatırma", var: kasa },
    { id: "tediye", ad: "Tediye makbuzu (havale)", var: yetkili(oturum, "tediye") },
    { id: "foy", ad: "Kasa hareket föyü", var: kasa },
  ] as const).filter((s) => s.var);
  const [sekme, setSekme] = useState<KasaSekme>(sekmeler.find((s) => s.id === ilkSekme)?.id ?? sekmeler[0]?.id ?? "tahsilat");
  useEffect(() => { const s = sekmeler.find((x) => x.id === ilkSekme); if (s) setSekme(s.id); }, [ilkSekme]); // eslint-disable-line react-hooks/exhaustive-deps
  return (
    <section>
      <header className="sayfa-baslik"><h1>Kasa ve ödemeler</h1></header>
      <YazmaDurumu />
      <div className="sekmeler">
        {sekmeler.map((s) => <button key={s.id} className={sekme === s.id ? "aktif" : ""} onClick={() => setSekme(s.id)}>{s.ad}</button>)}
      </div>
      {sekme === "tahsilat" && <KasaFisi key="t" tur="Tahsilat" />}
      {sekme === "odeme" && <KasaFisi key="o" tur="Odeme" />}
      {sekme === "bankaya" && <BankayaYatirma />}
      {sekme === "tediye" && <TediyeMakbuzu />}
      {sekme === "foy" && <KasaFoyu />}
    </section>
  );
}
