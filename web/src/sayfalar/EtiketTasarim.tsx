import { useEffect, useRef, useState } from "react";
import { api } from "../api";
import { Alan, Durum, useVeri } from "../ortak";

type Oge = {
  tur: "yazi" | "barkod" | "kutu" | "logo";
  x: number; y: number; genislik: number; yukseklik: number;
  metin: string; boyut: number; yaziTipi: "sans" | "serif"; kalin: boolean;
  hiza: "sol" | "orta" | "sag"; dikeyHiza: "ust" | "orta" | "alt"; satirSayisi: number;
  buyukHarf: boolean; ters: boolean; dolu: boolean; kalinlik: number; barkodYazisi: boolean;
};
type Tasarim = { genislikMm: number; yukseklikMm: number; ogeler: Oge[] };
type Sablon = { kod: string; ad: string; aktif: boolean; sira: number; kayitli: boolean; yerlesik: boolean; tasarim: Tasarim };
type Liste = { sablonlar: Sablon[]; alanlar: { kod: string; ad: string }[] };
type Duzenlenen = { kod: string; yeni: boolean; ad: string; aktif: boolean; sira: number; tasarim: Tasarim };

const VARSAYILAN: Oge = {
  tur: "yazi", x: 3, y: 3, genislik: 40, yukseklik: 6, metin: "Yazı", boyut: 4, yaziTipi: "sans", kalin: false,
  hiza: "sol", dikeyHiza: "ust", satirSayisi: 1, buyukHarf: false, ters: false, dolu: false, kalinlik: 0.4, barkodYazisi: true,
};
const TUR_ADI: Record<Oge["tur"], string> = { yazi: "Yazı", barkod: "Barkod", kutu: "Çizgi / çerçeve", logo: "Yerli üretim logosu" };
const yuvarla = (n: number) => Math.round(n * 2) / 2;   // 0,5 mm adım
const kopya = <T,>(x: T): T => JSON.parse(JSON.stringify(x));

/** Etiket tasarımı: öğeleri sürükleyip boyutlandırın, sağdan özelliklerini değiştirin; görüntü sunucunun gerçek çizimidir. */
export default function EtiketTasarimSayfa() {
  const liste = useVeri<Liste>("/api/etiket-tasarim");
  const [d, setD] = useState<Duzenlenen | null>(null);
  const [gecmis, setGecmis] = useState<Duzenlenen[]>([]);
  const [degisti, setDegisti] = useState(false);
  const [secili, setSecili] = useState<number | null>(null);
  const [mesaj, setMesaj] = useState<{ tur: "bilgi" | "hata"; metin: string } | null>(null);
  const [barkod, setBarkod] = useState("");

  const ac = (s: Sablon) => {
    if (degisti && !confirm("Kaydedilmemiş değişiklikler var. Yine de başka tasarıma geçilsin mi?")) return;
    setD({ kod: s.kod, yeni: false, ad: s.ad, aktif: s.aktif, sira: s.sira, tasarim: kopya(s.tasarim) });
    setGecmis([]); setDegisti(false); setSecili(null); setMesaj(null);
  };
  useEffect(() => {
    if (!d && liste.veri?.sablonlar.length) ac(liste.veri.sablonlar[0]);
  }, [liste.veri]); // eslint-disable-line react-hooks/exhaustive-deps

  /** Her değişiklik geri alınabilir (Ctrl+Z). */
  const degistir = (f: (t: Duzenlenen) => void) => {
    if (!d) return;
    setGecmis((g) => [...g.slice(-49), d]);
    const yeni = kopya(d);
    f(yeni);
    setD(yeni);
    setDegisti(true);
  };
  const geriAl = () => {
    if (!gecmis.length) return;
    setD(gecmis[gecmis.length - 1]);
    setGecmis(gecmis.slice(0, -1));
  };
  const ogeDegistir = (i: number, p: Partial<Oge>) => degistir((t) => { t.tasarim.ogeler[i] = { ...t.tasarim.ogeler[i], ...p }; });

  function ekle(p: Partial<Oge>) {
    if (!d) return;
    degistir((t) => { t.tasarim.ogeler.push({ ...VARSAYILAN, ...p }); });
    setSecili(d.tasarim.ogeler.length);
  }
  function sil(i: number) { degistir((t) => { t.tasarim.ogeler.splice(i, 1); }); setSecili(null); }
  function sirala(i: number, yon: number) {
    const j = i + yon;
    if (!d || j < 0 || j >= d.tasarim.ogeler.length) return;
    degistir((t) => { const o = t.tasarim.ogeler; [o[i], o[j]] = [o[j], o[i]]; });
    setSecili(j);
  }

  async function kaydet() {
    if (!d) return;
    try {
      await api.put(`/api/etiket-tasarim/${encodeURIComponent(d.kod)}`, { ad: d.ad, aktif: d.aktif, sira: d.sira, tasarim: d.tasarim });
      setDegisti(false);
      setD({ ...d, yeni: false });
      setMesaj({ tur: "bilgi", metin: "Kaydedildi. Terminal ve etiket basım ekranı bundan sonra bu tasarımı kullanır." });
      liste.yenile();
    } catch (e) {
      setMesaj({ tur: "hata", metin: (e as Error).message });
    }
  }

  async function kaldir(s: Sablon) {
    const soru = s.yerlesik ? `"${s.ad}" programın içindeki ilk haline dönsün mü? Yaptığınız değişiklikler silinir.` : `"${s.ad}" tasarımı silinsin mi?`;
    if (!confirm(soru)) return;
    try {
      await api.del(`/api/etiket-tasarim/${encodeURIComponent(s.kod)}`);
      setDegisti(false);
      setD(null);
      setMesaj({ tur: "bilgi", metin: s.yerlesik ? "İlk haline döndü." : "Silindi." });
      liste.yenile();
    } catch (e) {
      setMesaj({ tur: "hata", metin: (e as Error).message });
    }
  }

  function yeniTasarim(kaynak?: Duzenlenen) {
    if (degisti && !confirm("Kaydedilmemiş değişiklikler var. Yine de yeni tasarıma geçilsin mi?")) return;
    const ad = prompt("Yeni tasarımın adı", kaynak ? `${kaynak.ad} (kopya)` : "Yeni etiket");
    if (!ad) return;
    const mevcut = new Set(liste.veri?.sablonlar.map((s) => s.kod));
    let kod = ad.toLocaleLowerCase("tr").replace(/ı/g, "i").replace(/ğ/g, "g").replace(/ü/g, "u").replace(/ş/g, "s").replace(/ö/g, "o").replace(/ç/g, "c")
      .replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "").slice(0, 16) || "etiket";
    for (let i = 2; mevcut.has(kod); i++) kod = `${kod.slice(0, 16)}-${i}`;
    setD({
      kod, yeni: true, ad, aktif: true, sira: (liste.veri?.sablonlar.length ?? 0) + 1,
      tasarim: kaynak ? kopya(kaynak.tasarim) : { genislikMm: 100, yukseklikMm: 39, ogeler: [] },
    });
    setGecmis([]); setDegisti(true); setSecili(null);
    setMesaj({ tur: "bilgi", metin: "Yeni tasarım. Öğeleri ekleyip Kaydet'e basın." });
  }

  // Klavye: oklar 0,5 mm (Shift ile 5 mm) taşır, Delete siler, Ctrl+Z geri alır.
  useEffect(() => {
    const tus = (e: KeyboardEvent) => {
      const hedef = e.target as HTMLElement;
      if (hedef.tagName === "INPUT" || hedef.tagName === "TEXTAREA" || hedef.tagName === "SELECT") return;
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "z") { e.preventDefault(); geriAl(); return; }
      if (secili == null || !d?.tasarim.ogeler[secili]) return;
      const adim = e.shiftKey ? 5 : 0.5;
      const o = d.tasarim.ogeler[secili];
      const yon: Record<string, [number, number]> = { ArrowLeft: [-adim, 0], ArrowRight: [adim, 0], ArrowUp: [0, -adim], ArrowDown: [0, adim] };
      if (yon[e.key]) { e.preventDefault(); ogeDegistir(secili, { x: yuvarla(o.x + yon[e.key][0]), y: yuvarla(o.y + yon[e.key][1]) }); }
      else if (e.key === "Delete") { e.preventDefault(); sil(secili); }
    };
    window.addEventListener("keydown", tus);
    return () => window.removeEventListener("keydown", tus);
  });

  const sablon = liste.veri?.sablonlar.find((s) => s.kod === d?.kod);
  const oge = secili != null ? d?.tasarim.ogeler[secili] : undefined;

  return (
    <section>
      <header className="sayfa-baslik">
        <h1>Etiket tasarımı</h1>
        <span className="not">Kaydettiğiniz tasarım terminalde ve etiket basım ekranında hemen seçilebilir.</span>
      </header>
      <Durum hata={liste.hata} yukleniyor={liste.yukleniyor && !liste.veri} />
      {mesaj && <div className={mesaj.tur === "hata" ? "uyari hata" : "uyari tamam"}>{mesaj.metin}</div>}
      <div className="tasarim-duzen">
        <div>
          <div className="liste">
            {liste.veri?.sablonlar.map((s) => (
              <button key={s.kod} className={"liste-satir" + (s.kod === d?.kod ? " secili" : "")} onClick={() => ac(s)}>
                <span>{s.ad}<small>{s.tasarim.genislikMm} × {s.tasarim.yukseklikMm} mm{s.aktif ? "" : " · kullanılmıyor"}{s.yerlesik && !s.kayitli ? " · ilk hali" : ""}</small></span>
              </button>
            ))}
          </div>
          <div className="filtre" style={{ marginTop: 10 }}>
            <button className="ikincil" onClick={() => yeniTasarim()}>Boş tasarım</button>
            {d && <button className="ikincil" onClick={() => yeniTasarim(d)}>Bunu kopyala</button>}
          </div>
        </div>

        {d && (
          <div>
            <div className="filtre">
              <Alan etiket="Ad"><input value={d.ad} onChange={(e) => degistir((t) => { t.ad = e.target.value; })} /></Alan>
              <Alan etiket="Genişlik (mm)"><Sayi deger={d.tasarim.genislikMm} degisti={(n) => degistir((t) => { t.tasarim.genislikMm = n; })} /></Alan>
              <Alan etiket="Yükseklik (mm)"><Sayi deger={d.tasarim.yukseklikMm} degisti={(n) => degistir((t) => { t.tasarim.yukseklikMm = n; })} /></Alan>
              <Alan etiket="Sıra"><Sayi deger={d.sira} degisti={(n) => degistir((t) => { t.sira = Math.round(n); })} /></Alan>
              <label className="kutu"><input type="checkbox" checked={d.aktif} onChange={(e) => degistir((t) => { t.aktif = e.target.checked; })} /> Kullanımda</label>
              <button onClick={kaydet} disabled={!degisti}>Kaydet</button>
              <button className="ikincil" onClick={geriAl} disabled={!gecmis.length} title="Ctrl+Z">Geri al</button>
              {sablon?.kayitli && <button className="ikincil" onClick={() => kaldir(sablon)}>{sablon.yerlesik ? "İlk haline döndür" : "Sil"}</button>}
            </div>

            <div className="filtre">
              <button className="ikincil" onClick={() => ekle({ metin: "Yazı" })}>+ Yazı</button>
              <select value="" onChange={(e) => e.target.value && ekle({ metin: `{${e.target.value}}`, genislik: 40, yukseklik: 6 })}>
                <option value="">+ Ürün bilgisi…</option>
                {liste.veri?.alanlar.map((a) => <option key={a.kod} value={a.kod}>{a.ad}</option>)}
              </select>
              <button className="ikincil" onClick={() => ekle({ tur: "barkod", genislik: 32, yukseklik: 14 })}>+ Barkod</button>
              <button className="ikincil" onClick={() => ekle({ tur: "kutu", dolu: true, genislik: 40, yukseklik: 0.5 })}>+ Çizgi</button>
              <button className="ikincil" onClick={() => ekle({ tur: "kutu", x: 0.5, y: 0.5, genislik: d.tasarim.genislikMm - 1, yukseklik: d.tasarim.yukseklikMm - 1 })}>+ Çerçeve</button>
              <button className="ikincil" onClick={() => ekle({ tur: "logo", genislik: 7, yukseklik: 11 })}>+ Yerli üretim logosu</button>
            </div>

            <Tuval tasarim={d.tasarim} barkod={barkod} secili={secili} sec={setSecili}
                   tasi={(i, p) => ogeDegistir(i, p)} />

            <div className="filtre" style={{ marginTop: 8 }}>
              <Alan etiket="Önizlemede gösterilecek ürün (boşsa örnek ürün)">
                <input placeholder="Barkod" value={barkod} onChange={(e) => setBarkod(e.target.value.trim())} />
              </Alan>
              <span className="not">Sürükleyerek taşıyın, sağ alt köşeden boyutlandırın. Oklar 0,5 mm, Shift+ok 5 mm taşır; Delete siler.</span>
            </div>

            <div className="tasarim-alt">
              <div>
                <h2>Öğeler</h2>
                <table className="ic">
                  <tbody>
                    {d.tasarim.ogeler.map((o, i) => (
                      <tr key={i} className={"tiklanir" + (i === secili ? " secili" : "")} onClick={() => setSecili(i)}>
                        <td>{o.tur === "yazi" ? o.metin : TUR_ADI[o.tur]}</td>
                        <td className="s">{o.x}, {o.y}</td>
                      </tr>
                    ))}
                    {d.tasarim.ogeler.length === 0 && <tr><td className="bos">Henüz öğe yok.</td></tr>}
                  </tbody>
                </table>
              </div>
              {oge && secili != null && (
                <OgeAyari oge={oge} alanlar={liste.veri?.alanlar ?? []} degistir={(p) => ogeDegistir(secili, p)}
                          sil={() => sil(secili)} cogalt={() => ekle({ ...oge, x: oge.x + 2, y: oge.y + 2 })}
                          one={() => sirala(secili, 1)} arkaya={() => sirala(secili, -1)} />
              )}
            </div>
          </div>
        )}
      </div>
    </section>
  );
}

function Sayi({ deger, degisti, adim = 0.5 }: { deger: number; degisti: (n: number) => void; adim?: number }) {
  const [metin, setMetin] = useState(String(deger));
  useEffect(() => setMetin(String(deger)), [deger]);
  return (
    <input className="kisa" type="number" step={adim} value={metin}
      onChange={(e) => { setMetin(e.target.value); const n = parseFloat(e.target.value); if (!isNaN(n)) degisti(n); }} />
  );
}

/** Sunucunun çizdiği gerçek etiket görüntüsü ve üstünde sürüklenebilir öğe kutuları. */
function Tuval({ tasarim, barkod, secili, sec, tasi }: {
  tasarim: Tasarim; barkod: string; secili: number | null; sec: (i: number | null) => void; tasi: (i: number, p: Partial<Oge>) => void;
}) {
  const [resim, setResim] = useState<string | null>(null);
  const [hata, setHata] = useState<string | null>(null);
  const [surukle, setSurukle] = useState<{ i: number; boyut: boolean; x0: number; y0: number; o: Oge; dx: number; dy: number } | null>(null);
  const kutu = useRef<HTMLDivElement>(null);
  const [genislikPx, setGenislikPx] = useState(720);

  useEffect(() => {
    const el = kutu.current?.parentElement;
    if (!el) return;
    const g = () => setGenislikPx(Math.min(900, el.clientWidth - 2));
    g();
    const ro = new ResizeObserver(g);
    ro.observe(el);
    return () => ro.disconnect();
  }, []);
  const olcek = genislikPx / tasarim.genislikMm;   // px / mm

  // Gerçek görüntü: değişiklikten kısa süre sonra sunucudan istenir.
  useEffect(() => {
    let iptal = false;
    let url: string | null = null;
    const t = setTimeout(() => {
      api.post<Blob>("/api/etiket-tasarim/onizleme", { tasarim, barkod: barkod || null })
        .then((b) => { if (iptal) return; url = URL.createObjectURL(b); setResim((eski) => { if (eski) URL.revokeObjectURL(eski); return url; }); setHata(null); })
        .catch((e: Error) => !iptal && setHata(e.message));
    }, 200);
    return () => { iptal = true; clearTimeout(t); };
  }, [tasarim, barkod]);

  useEffect(() => {
    if (!surukle) return;
    const hareket = (e: PointerEvent) => {
      const dx = (e.clientX - surukle.x0) / olcek, dy = (e.clientY - surukle.y0) / olcek;
      setSurukle((s) => s && { ...s, dx, dy });
    };
    const birak = () => {
      const { i, boyut, o, dx, dy } = surukle;
      if (Math.abs(dx) > 0.2 || Math.abs(dy) > 0.2)
        tasi(i, boyut
          ? { genislik: Math.max(0.5, yuvarla(o.genislik + dx)), yukseklik: Math.max(0.5, yuvarla(o.yukseklik + dy)) }
          : { x: yuvarla(o.x + dx), y: yuvarla(o.y + dy) });
      setSurukle(null);
    };
    window.addEventListener("pointermove", hareket);
    window.addEventListener("pointerup", birak);
    return () => { window.removeEventListener("pointermove", hareket); window.removeEventListener("pointerup", birak); };
  }, [surukle, olcek, tasi]);

  const basla = (e: React.PointerEvent, i: number, boyut: boolean) => {
    e.preventDefault(); e.stopPropagation();
    sec(i);
    setSurukle({ i, boyut, x0: e.clientX, y0: e.clientY, o: tasarim.ogeler[i], dx: 0, dy: 0 });
  };

  return (
    <div>
      <div ref={kutu} className="tuval" style={{ width: genislikPx, height: tasarim.yukseklikMm * olcek }} onPointerDown={() => sec(null)}>
        {resim && <img src={resim} alt="Etiket görüntüsü" draggable={false} />}
        {tasarim.ogeler.map((o, i) => {
          const s = surukle?.i === i ? surukle : null;
          const x = o.x + (s && !s.boyut ? s.dx : 0), y = o.y + (s && !s.boyut ? s.dy : 0);
          const g = Math.max(0.5, o.genislik + (s?.boyut ? s.dx : 0)), h = Math.max(0.5, o.yukseklik + (s?.boyut ? s.dy : 0));
          return (
            <div key={i} className={"tuval-oge" + (i === secili ? " secili" : "")} title={o.tur === "yazi" ? o.metin : TUR_ADI[o.tur]}
                 style={{ left: x * olcek, top: y * olcek, width: Math.max(6, g * olcek), height: Math.max(6, h * olcek) }}
                 onPointerDown={(e) => basla(e, i, false)}>
              {i === secili && <span className="tutamak" onPointerDown={(e) => basla(e, i, true)} />}
            </div>
          );
        })}
      </div>
      {hata && <div className="uyari hata">{hata}</div>}
    </div>
  );
}

function OgeAyari({ oge, alanlar, degistir, sil, cogalt, one, arkaya }: {
  oge: Oge; alanlar: { kod: string; ad: string }[]; degistir: (p: Partial<Oge>) => void;
  sil: () => void; cogalt: () => void; one: () => void; arkaya: () => void;
}) {
  const metin = useRef<HTMLTextAreaElement>(null);
  const alanEkle = (kod: string) => {
    const el = metin.current;
    const k = el?.selectionStart ?? oge.metin.length;
    degistir({ metin: oge.metin.slice(0, k) + `{${kod}}` + oge.metin.slice(el?.selectionEnd ?? k) });
  };
  return (
    <div className="oge-ayari">
      <h2>{TUR_ADI[oge.tur]}</h2>
      {oge.tur === "yazi" && (
        <>
          <Alan etiket="Yazı ({alan} yerine ürünün bilgisi gelir)">
            <textarea ref={metin} rows={2} value={oge.metin} onChange={(e) => degistir({ metin: e.target.value })} />
          </Alan>
          <Alan etiket="Ürün bilgisi ekle">
            <select value="" onChange={(e) => e.target.value && alanEkle(e.target.value)}>
              <option value="">Seçin…</option>
              {alanlar.map((a) => <option key={a.kod} value={a.kod}>{a.ad}</option>)}
            </select>
          </Alan>
        </>
      )}
      <div className="izgara">
        <Alan etiket="Soldan (mm)"><Sayi deger={oge.x} degisti={(n) => degistir({ x: n })} /></Alan>
        <Alan etiket="Üstten (mm)"><Sayi deger={oge.y} degisti={(n) => degistir({ y: n })} /></Alan>
        <Alan etiket="Genişlik (mm)"><Sayi deger={oge.genislik} degisti={(n) => degistir({ genislik: n })} /></Alan>
        <Alan etiket="Yükseklik (mm)"><Sayi deger={oge.yukseklik} degisti={(n) => degistir({ yukseklik: n })} /></Alan>
        {oge.tur === "yazi" && (
          <>
            <Alan etiket="Yazı boyu (mm)"><Sayi deger={oge.boyut} adim={0.1} degisti={(n) => degistir({ boyut: n })} /></Alan>
            <Alan etiket="En fazla satır"><Sayi deger={oge.satirSayisi} adim={1} degisti={(n) => degistir({ satirSayisi: Math.max(1, Math.round(n)) })} /></Alan>
            <Alan etiket="Yazı tipi">
              <select value={oge.yaziTipi} onChange={(e) => degistir({ yaziTipi: e.target.value as Oge["yaziTipi"] })}>
                <option value="sans">Düz (Sans)</option><option value="serif">Tırnaklı (Serif)</option>
              </select>
            </Alan>
            <Alan etiket="Yatay hiza">
              <select value={oge.hiza} onChange={(e) => degistir({ hiza: e.target.value as Oge["hiza"] })}>
                <option value="sol">Sola</option><option value="orta">Ortaya</option><option value="sag">Sağa</option>
              </select>
            </Alan>
            <Alan etiket="Dikey hiza">
              <select value={oge.dikeyHiza} onChange={(e) => degistir({ dikeyHiza: e.target.value as Oge["dikeyHiza"] })}>
                <option value="ust">Üste</option><option value="orta">Ortaya</option><option value="alt">Alta</option>
              </select>
            </Alan>
          </>
        )}
        {oge.tur === "barkod" && (
          <Alan etiket="Hiza">
            <select value={oge.hiza} onChange={(e) => degistir({ hiza: e.target.value as Oge["hiza"] })}>
              <option value="sol">Sola</option><option value="orta">Ortaya</option><option value="sag">Sağa</option>
            </select>
          </Alan>
        )}
        {oge.tur === "kutu" && !oge.dolu && (
          <Alan etiket="Çizgi kalınlığı (mm)"><Sayi deger={oge.kalinlik} adim={0.1} degisti={(n) => degistir({ kalinlik: n })} /></Alan>
        )}
      </div>
      <div className="filtre">
        {oge.tur === "yazi" && <>
          <label className="kutu"><input type="checkbox" checked={oge.kalin} onChange={(e) => degistir({ kalin: e.target.checked })} /> Kalın</label>
          <label className="kutu"><input type="checkbox" checked={oge.buyukHarf} onChange={(e) => degistir({ buyukHarf: e.target.checked })} /> BÜYÜK HARF</label>
          <label className="kutu"><input type="checkbox" checked={oge.ters} onChange={(e) => degistir({ ters: e.target.checked })} /> Siyah zemin, beyaz yazı</label>
        </>}
        {oge.tur === "barkod" && <label className="kutu"><input type="checkbox" checked={oge.barkodYazisi} onChange={(e) => degistir({ barkodYazisi: e.target.checked })} /> Altında numarası</label>}
        {oge.tur === "kutu" && <label className="kutu"><input type="checkbox" checked={oge.dolu} onChange={(e) => degistir({ dolu: e.target.checked })} /> İçi dolu (çizgi)</label>}
      </div>
      {oge.tur === "yazi" && <p className="not">Yazı kutuya sığmazsa kendiliğinden küçülür. Yazıdaki ürün bilgilerinin hepsi boşsa (örn. menşei girilmemiş) bu öğe basılmaz.</p>}
      {oge.tur === "logo" && <p className="not">Yalnız menşei Türkiye olan ürünlerde basılır.</p>}
      <div className="filtre">
        <button className="ikincil" onClick={cogalt}>Çoğalt</button>
        <button className="ikincil" onClick={one} title="Üstte çizilir">Öne</button>
        <button className="ikincil" onClick={arkaya}>Arkaya</button>
        <button className="ikincil" style={{ color: "var(--hata)" }} onClick={sil}>Sil</button>
      </div>
    </div>
  );
}
