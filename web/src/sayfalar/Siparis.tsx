import { Fragment, useMemo, useRef, useState } from "react";
import { api, bugun, isoGun, miktar, para, qs, saat, sayiOku, tarih } from "../api";
import { Alan, CariOzet, CariSec, Depo, DepoSec, Durum, Mesaj, MesajKutusu, useVeri, yuzde, YazmaDurumu } from "../ortak";

type Oneri = {
  kod: string; ad: string; birim: string; barkod: string; kaynakTur: "tedarikci" | "depo"; kaynakKod: string; kaynakAd: string;
  satis: number; gunluk: number; stok: number; acik: number; miktar: number; birimFiyat: number; sonGiris: string | null;
};
type Satir = Oneri & { siparis: string };
type Gonderim = { kanal: string; alici: string; zaman: string; kullanici: string };
type Ozet = {
  tur: "tedarikci" | "depo"; seri: string; sira: number; tarih: string; depo: number; kaynakKod: string; kaynakAd: string;
  satirSayisi: number; miktar: number; tutar: number; teslim: number; gonderimler: Gonderim[];
};
type UrunBilgisi = { stokKodu: string; stok: number; sonGelisTarih: string | null; sonGelisMiktar: number | null; sonGelisTur: "alis" | "sevk" | null;
  sonGelisKaynak: string | null; sonGelisEvrak: string | null; sonAlisTarih: string | null; sonAlisCari: string | null; sonAlisFiyat: number | null;
  kdvOrani: number; sonAlisKdvDahil: number | null; satisFiyati: number | null; karOrani: number | null; satis30: number; kacGunluk: number | null };
type Detay = { satirNo: number; stokKodu: string; barkod: string; urunAdi: string; birim: string; miktar: number; teslim: number; tutar: number };
type Evrak = { tur: string; seri: string; sira: number };

const anahtar = (tur: string, kod: string) => `${tur}:${kod}`;
const n = (s: string) => sayiOku(s) || 0;
const yarin = () => { const d = new Date(); d.setDate(d.getDate() + 1); return isoGun(d); };

export default function Siparis({ depoNo }: { depoNo: number | null }) {
  const [sekme, setSekme] = useState<"yeni" | "liste">("yeni");
  return (
    <section>
      <header className="sayfa-baslik"><h1>Sipariş</h1></header>
      <div className="sekmeler">
        <button className={sekme === "yeni" ? "aktif" : ""} onClick={() => setSekme("yeni")}>Yeni sipariş</button>
        <button className={sekme === "liste" ? "aktif" : ""} onClick={() => setSekme("liste")}>Verilen siparişler ve gönderim</button>
      </div>
      {sekme === "yeni" ? <YeniSiparis depoNo={depoNo} gonder={() => setSekme("liste")} /> : <Siparisler depoNo={depoNo} />}
    </section>
  );
}

function YeniSiparis({ depoNo, gonder }: { depoNo: number | null; gonder: () => void }) {
  const [depo, setDepo] = useState<number | "">(depoNo ?? "");
  const [gecmis, setGecmis] = useState("28");
  const [gun, setGun] = useState("7");
  const [satirlar, setSatirlar] = useState<Satir[]>([]);
  const [secili, setSecili] = useState<string | null>(null);
  const [hesaplaniyor, setHesaplaniyor] = useState(false);
  const [mesaj, setMesaj] = useState<Mesaj>(null);
  const [barkod, setBarkod] = useState("");
  const [teslim, setTeslim] = useState(yarin());
  const [belgeNo, setBelgeNo] = useState("");
  const [kaydediliyor, setKaydediliyor] = useState(false);
  const [elleTur, setElleTur] = useState<"tedarikci" | "depo">("tedarikci");
  const [elleCari, setElleCari] = useState<CariOzet | null>(null);
  const [elleDepo, setElleDepo] = useState<number | "">("");
  const [bosKaynak, setBosKaynak] = useState<{ anahtar: string; tur: "tedarikci" | "depo"; kod: string; ad: string } | null>(null);
  const depolar = useVeri<Depo[]>("/api/depolar");
  const ref = useRef<HTMLInputElement>(null);

  const gruplar = useMemo(() => {
    const m = new Map<string, { anahtar: string; tur: "tedarikci" | "depo"; kod: string; ad: string; satirlar: Satir[] }>();
    if (bosKaynak) m.set(bosKaynak.anahtar, { ...bosKaynak, satirlar: [] });
    for (const s of satirlar) {
      const a = anahtar(s.kaynakTur, s.kaynakKod);
      if (!m.has(a)) m.set(a, { anahtar: a, tur: s.kaynakTur, kod: s.kaynakKod, ad: s.kaynakAd, satirlar: [] });
      m.get(a)!.satirlar.push(s);
    }
    return [...m.values()].sort((x, y) => (x.kod === "" ? -1 : y.kod === "" ? 1 : y.satirlar.length - x.satirlar.length));
  }, [satirlar, bosKaynak]);
  const grup = gruplar.find((g) => g.anahtar === secili) ?? null;

  async function hesapla() {
    if (!depo) return;
    setHesaplaniyor(true);
    setMesaj(null);
    try {
      const l = await api.get<Oneri[]>(`/api/siparis/oneri?${qs({ depo, gecmis: n(gecmis), gun: n(gun) })}`);
      setSatirlar(l.map((o) => ({ ...o, siparis: String(o.miktar) })));
      setBosKaynak(null);
      setSecili(null);
      if (l.length === 0) setMesaj({ tur: "bilgi", metin: "Bu ayarlarla sipariş gereken ürün çıkmadı." });
    } catch (err) {
      setMesaj({ tur: "hata", metin: (err as Error).message });
    }
    setHesaplaniyor(false);
  }

  function elleBaslat() {
    const kod = elleTur === "depo" ? String(elleDepo) : elleCari?.kod ?? "";
    if (!kod) return;
    const ad = elleTur === "depo" ? depolar.veri?.find((d) => d.no === elleDepo)?.ad.trim() ?? kod : elleCari!.unvan;
    const a = anahtar(elleTur, kod);
    if (!gruplar.some((g) => g.anahtar === a)) setBosKaynak({ anahtar: a, tur: elleTur, kod, ad });
    setSecili(a);
    setElleCari(null);
    setTimeout(() => ref.current?.focus(), 0);
  }

  async function ekle(e: React.FormEvent) {
    e.preventDefault();
    const b = barkod.trim();
    setBarkod("");
    ref.current?.focus();
    if (!b || !grup || !depo) return;
    try {
      const o = await api.get<Oneri>(`/api/siparis/urun?${qs({ k: b, depo, gecmis: n(gecmis), gun: n(gun) })}`);
      const var_ = satirlar.find((s) => s.kod === o.kod);
      if (var_ && anahtar(var_.kaynakTur, var_.kaynakKod) !== grup.anahtar) {
        setMesaj({ tur: "hata", metin: `${o.ad} başka bir siparişte (${var_.kaynakAd || "kaynağı bilinmeyen"}) zaten var.` });
        return;
      }
      if (var_) { degis(o.kod, String(n(var_.siparis) + 1)); return; }
      // Elle eklenen ürün seçili siparişe gider; önerilen miktar 0 ise 1 yazılır.
      setSatirlar((eski) => [...eski, { ...o, kaynakTur: grup.tur, kaynakKod: grup.kod, kaynakAd: grup.ad, siparis: String(o.miktar || 1) }]);
      setMesaj(null);
    } catch (err) {
      setMesaj({ tur: "hata", metin: `${b}: ${(err as Error).message}` });
    }
  }

  const degis = (kod: string, siparis: string) => setSatirlar((eski) => eski.map((s) => (s.kod === kod ? { ...s, siparis } : s)));
  const cikar = (kod: string) => setSatirlar((eski) => eski.filter((s) => s.kod !== kod));

  // Kaynağı bilinmeyen ürünleri seçilen tedarikçiye taşır.
  function kaynakAta(c: CariOzet | null) {
    if (!c || !grup) return;
    setSatirlar((eski) => eski.map((s) => (anahtar(s.kaynakTur, s.kaynakKod) === grup.anahtar ? { ...s, kaynakTur: "tedarikci", kaynakKod: c.kod, kaynakAd: c.unvan } : s)));
    setSecili(anahtar("tedarikci", c.kod));
  }

  async function kaydet() {
    if (!grup || !depo) return;
    const gidecek = grup.satirlar.filter((s) => n(s.siparis) > 0);
    setKaydediliyor(true);
    try {
      const r = await api.post<Evrak & { satirSayisi: number; tutar: number }>("/api/siparis", {
        depo, kaynakTur: grup.tur, kaynakKod: grup.kod, tarih: bugun(), teslimTarihi: teslim, belgeNo,
        satirlar: gidecek.map((s) => ({ urun: s.kod, miktar: n(s.siparis), birimFiyat: grup.tur === "tedarikci" && s.birimFiyat > 0 ? s.birimFiyat : null })),
      });
      setMesaj({ tur: "bilgi", metin: `Sipariş ${r.seri} ${r.sira} Mikro'ya kaydedildi (${grup.ad}, ${r.satirSayisi} kalem). "Verilen siparişler" sekmesinden e-posta ya da WhatsApp ile gönderebilirsiniz.` });
      setSatirlar((eski) => eski.filter((s) => anahtar(s.kaynakTur, s.kaynakKod) !== grup.anahtar));
      if (bosKaynak?.anahtar === grup.anahtar) setBosKaynak(null);
      setSecili(null);
    } catch (err) {
      setMesaj({ tur: "hata", metin: (err as Error).message });
    }
    setKaydediliyor(false);
  }

  const gidecek = grup?.satirlar.filter((s) => n(s.siparis) > 0) ?? [];
  const tutar = gidecek.reduce((t, s) => t + n(s.siparis) * s.birimFiyat, 0);

  return (
    <>
      <YazmaDurumu />
      <p className="not">
        Öneri: mağazanın son günlerdeki net satışı günlük ortalamaya çevrilir, seçilen gün kadar ihtiyaçtan depodaki stok ve açık siparişler düşülür.
        Ürün mağazaya en son alışla geldiyse o tedarikçiye, depolar arası sevkle geldiyse o depoya sipariş önerilir. Miktarları değiştirebilir, ürün ekleyip çıkarabilirsiniz.
      </p>
      <div className="filtre">
        <DepoSec etiket="Mağaza / depo" deger={depo} degisti={(d) => { setDepo(d); setSatirlar([]); setSecili(null); setBosKaynak(null); }} kilitli={depoNo != null} />
        <Alan etiket="Son kaç günün satışı"><input className="adet" inputMode="numeric" value={gecmis} onChange={(e) => setGecmis(e.target.value)} /></Alan>
        <Alan etiket="Kaç günlük sipariş"><input className="adet" inputMode="numeric" value={gun} onChange={(e) => setGun(e.target.value)} /></Alan>
        <button disabled={!depo || hesaplaniyor} onClick={hesapla}>{hesaplaniyor ? "Hesaplanıyor…" : "Otomatik öneri hesapla"}</button>
      </div>
      <div className="filtre">
        <Alan etiket="Elle sipariş: kime">
          <select value={elleTur} onChange={(e) => setElleTur(e.target.value as "tedarikci" | "depo")}>
            <option value="tedarikci">Tedarikçiye</option>
            <option value="depo">Depoya (depolar arası)</option>
          </select>
        </Alan>
        {elleTur === "tedarikci" ? <CariSec deger={elleCari} degisti={setElleCari} etiket="Tedarikçi" /> : <DepoSec etiket="Depo" deger={elleDepo} degisti={setElleDepo} />}
        <button className="ikincil" disabled={!depo || (elleTur === "tedarikci" ? !elleCari : !elleDepo || elleDepo === depo)} onClick={elleBaslat}>Elle sipariş başlat</button>
      </div>
      <MesajKutusu mesaj={mesaj} />

      {gruplar.length > 0 && (
        <div className="iki-sutun">
          <div className="liste">
            {gruplar.map((g) => {
              const kalem = g.satirlar.filter((s) => n(s.siparis) > 0).length;
              return (
                <button key={g.anahtar} className={"liste-satir" + (g.anahtar === secili ? " secili" : "")} onClick={() => setSecili(g.anahtar)}>
                  <span>{g.kod === "" ? <b>Kaynağı bilinmeyen</b> : g.ad || g.kod}<small>{g.tur === "depo" ? `Depo ${g.kod}` : g.kod || "tedarikçi seçin"}</small></span>
                  <span>{kalem} kalem</span>
                </button>
              );
            })}
          </div>
          <div>
            {!grup && <p className="bilgi">Soldan bir tedarikçi ya da depo seçin.</p>}
            {grup && (
              <>
                <h2>{grup.kod === "" ? "Kaynağı bilinmeyen ürünler" : grup.ad}<small>{grup.tur === "depo" ? "depolar arası sipariş" : "tedarikçi siparişi"}</small></h2>
                {grup.kod === "" && (
                  <div className="filtre"><CariSec deger={null} degisti={kaynakAta} etiket="Bu ürünler için tedarikçi seçin" /></div>
                )}
                <form className="filtre" onSubmit={ekle}>
                  <Alan etiket="Ürün ekle: barkod okut ya da stok kodu yaz"><input ref={ref} value={barkod} onChange={(e) => setBarkod(e.target.value)} /></Alan>
                  <button type="submit" disabled={grup.kod === ""}>Ekle</button>
                </form>
                <table>
                  <thead><tr><th>Ürün</th><th className="s">Satış</th><th className="s">Günlük</th><th className="s">Stok</th><th className="s">Açık sip.</th><th className="s">Öneri</th><th className="s">Sipariş</th><th className="s">Son alış</th><th></th></tr></thead>
                  <tbody>
                    {grup.satirlar.length === 0 && <tr><td colSpan={9} className="bos">Ürün okutun.</td></tr>}
                    {grup.satirlar.map((s) => (
                      <tr key={s.kod}>
                        <td>{s.ad}<br /><small className="not">{s.barkod}{s.sonGiris ? ` · son giriş ${tarih(s.sonGiris)}` : ""}</small></td>
                        <td className="s">{miktar(s.satis)}</td>
                        <td className="s">{miktar(s.gunluk)}</td>
                        <td className={"s" + (s.stok < 0 ? " eksi" : "")} title={s.stok < 0 ? "Mikro'da stok eksi; hesapta sıfır sayıldı" : ""}>{miktar(s.stok)}</td>
                        <td className="s">{miktar(s.acik)}</td>
                        <td className="s">{miktar(s.miktar)}</td>
                        <td className="s"><input className="adet" inputMode="decimal" value={s.siparis} onChange={(e) => degis(s.kod, e.target.value)} /> {s.birim}</td>
                        <td className="s">{s.birimFiyat ? para(s.birimFiyat) : ""}</td>
                        <td><button className="sil" title="Çıkar" onClick={() => cikar(s.kod)}>×</button></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
                <div className="filtre" style={{ marginTop: 10 }}>
                  <Alan etiket="Teslim tarihi"><input type="date" value={teslim} min={bugun()} onChange={(e) => setTeslim(e.target.value)} /></Alan>
                  <Alan etiket="Belge no (isteğe bağlı)"><input className="kisa" value={belgeNo} onChange={(e) => setBelgeNo(e.target.value)} /></Alan>
                </div>
                <div className="kaydet">
                  <span>{gidecek.length} kalem{grup.tur === "tedarikci" && tutar > 0 ? `, yaklaşık ${para(tutar)} TL (son alış, KDV hariç)` : ""}</span>
                  <button disabled={kaydediliyor || grup.kod === "" || gidecek.length === 0} onClick={kaydet}>{kaydediliyor ? "Kaydediliyor…" : "Siparişi Mikro'ya kaydet"}</button>
                  <button className="ikincil" onClick={gonder}>Gönderime git</button>
                </div>
              </>
            )}
          </div>
        </div>
      )}
    </>
  );
}

function Siparisler({ depoNo }: { depoNo: number | null }) {
  const [depo, setDepo] = useState<number | "">(depoNo ?? "");
  const [ara, setAra] = useState("");
  const liste = useVeri<Ozet[]>(`/api/siparis?${qs({ depo })}`);
  const [acik, setAcik] = useState<string | null>(null);
  const secili = liste.veri?.find((o) => `${o.tur}|${o.seri}|${o.sira}` === acik) ?? null;
  const a_ = ara.trim().toLocaleUpperCase("tr-TR");
  const gorunen = liste.veri?.filter((o) => !a_ || `${o.seri} ${o.sira} ${o.kaynakAd} ${o.kaynakKod}`.toLocaleUpperCase("tr-TR").includes(a_));
  return (
    <>
      <div className="filtre">
        <DepoSec etiket="Mağaza / depo" deger={depo} degisti={setDepo} kilitli={depoNo != null} />
        <Alan etiket="Tedarikçi ya da evrak no ara"><input value={ara} onChange={(e) => setAra(e.target.value)} /></Alan>
      </div>
      <h2>Son 30 günün siparişleri<small>Mikro'dan girilenler de görünür</small></h2>
      <Durum hata={liste.hata} yukleniyor={liste.yukleniyor} />
      <table>
        <thead><tr><th>Evrak</th><th>Tarih</th><th>Kime</th><th>Mağaza</th><th className="s">Kalem</th><th className="s">Miktar</th><th className="s">Gelen</th><th>Gönderim</th></tr></thead>
        <tbody>
          {gorunen?.length === 0 && <tr><td colSpan={8} className="bos">Bu aralıkta sipariş yok.</td></tr>}
          {gorunen?.map((o) => {
            const a = `${o.tur}|${o.seri}|${o.sira}`;
            return (
              <Fragment key={a}>
                <tr className={"tiklanir" + (acik === a ? " secili" : "")} onClick={() => setAcik(acik === a ? null : a)}>
                  <td>{o.seri} {o.sira}</td><td>{tarih(o.tarih)}</td>
                  <td>{o.tur === "depo" ? `Depo ${o.kaynakKod} · ${o.kaynakAd.trim()}` : o.kaynakAd}</td><td>{o.depo}</td>
                  <td className="s">{o.satirSayisi}</td><td className="s">{miktar(o.miktar)}</td><td className="s">{miktar(o.teslim)}</td>
                  <td>{o.gonderimler.length === 0 ? <span className="not">gönderilmedi</span> : o.gonderimler.map((g, i) => <div key={i} className="not">{g.kanal === "eposta" ? "E-posta" : "WhatsApp"} · {saat(g.zaman)}</div>)}</td>
                </tr>
                {acik === a && secili && (
                  <tr className="alt-satir"><td colSpan={8}><Gonder evrak={secili} gonderildi={liste.yenile} /></td></tr>
                )}
              </Fragment>
            );
          })}
        </tbody>
      </table>
    </>
  );
}

function Gonder({ evrak, gonderildi }: { evrak: Ozet; gonderildi: () => void }) {
  const k = qs({ tur: evrak.tur, seri: evrak.seri, sira: evrak.sira });
  const detay = useVeri<{ satirlar: Detay[] }>(`/api/siparis/detay?${k}`);
  const bilgi = useVeri<{ eposta: string; telefon: string; metin: string; epostaHazir: boolean }>(`/api/siparis/gonderim?${k}`);
  const urunler = useVeri<UrunBilgisi[]>(`/api/siparis/bilgi?${k}`);
  const [eposta, setEposta] = useState<string | null>(null);
  const [telefon, setTelefon] = useState<string | null>(null);
  const [not, setNot] = useState("");
  const [mesaj, setMesaj] = useState<Mesaj>(null);
  const [bekle, setBekle] = useState(false);
  const ePosta = eposta ?? bilgi.veri?.eposta ?? "";
  const tel = telefon ?? bilgi.veri?.telefon ?? "";
  const govde = { tur: evrak.tur, seri: evrak.seri, sira: evrak.sira, not };

  async function pdf() {
    try {
      const b = await api.get<Blob>(`/api/siparis/pdf?${k}`);
      const url = URL.createObjectURL(b);
      const a = document.createElement("a");
      a.href = url;
      a.download = `Siparis_${evrak.seri}${evrak.sira}.pdf`;
      a.click();
      URL.revokeObjectURL(url);
    } catch (err) {
      setMesaj({ tur: "hata", metin: (err as Error).message });
    }
  }

  async function epostaGonder() {
    setBekle(true);
    try {
      await api.post("/api/siparis/eposta", { ...govde, alici: ePosta });
      setMesaj({ tur: "bilgi", metin: `Sipariş PDF ekiyle ${ePosta} adresine gönderildi.` });
      gonderildi();
    } catch (err) {
      setMesaj({ tur: "hata", metin: (err as Error).message });
    }
    setBekle(false);
  }

  async function whatsapp() {
    // Pencere tıklama anında açılır; yoksa tarayıcı açılır pencereyi engeller.
    const w = window.open("", "_blank");
    try {
      const r = await api.post<{ link: string }>("/api/siparis/whatsapp", { ...govde, alici: tel });
      if (w) w.location.href = r.link; else location.href = r.link;
      setMesaj({ tur: "bilgi", metin: "WhatsApp'ta mesaj hazır açıldı; göndermek için WhatsApp'ta gönder tuşuna basın. PDF'i de isterseniz ayrıca ekleyebilirsiniz." });
      gonderildi();
    } catch (err) {
      w?.close();
      setMesaj({ tur: "hata", metin: (err as Error).message });
    }
  }

  return (
    <div>
      <Durum hata={detay.hata ?? bilgi.hata} yukleniyor={detay.yukleniyor || bilgi.yukleniyor} />
      {detay.veri && <SiparisIcerik satirlar={detay.veri.satirlar} bilgiler={urunler.veri} yukleniyor={urunler.yukleniyor} hata={urunler.hata} />}
      <div className="filtre" style={{ marginTop: 10 }}>
        <Alan etiket="Mesaja eklenecek not (isteğe bağlı)"><input style={{ width: 320 }} value={not} onChange={(e) => setNot(e.target.value)} placeholder="örn. Sabah 10'dan önce teslim" /></Alan>
        <button className="ikincil" onClick={pdf}>PDF indir</button>
      </div>
      <div className="filtre">
        <Alan etiket="E-posta"><input style={{ width: 260 }} type="email" value={ePosta} onChange={(e) => setEposta(e.target.value)} /></Alan>
        <button disabled={bekle || !ePosta || bilgi.veri?.epostaHazir === false} onClick={epostaGonder} title={bilgi.veri?.epostaHazir === false ? "E-posta ayarları yapılmamış" : ""}>E-posta ile gönder</button>
        <Alan etiket="WhatsApp (cep telefonu)"><input className="kisa" inputMode="tel" value={tel} onChange={(e) => setTelefon(e.target.value)} placeholder="0532 123 45 67" /></Alan>
        <button disabled={!tel} onClick={whatsapp}>WhatsApp ile gönder</button>
      </div>
      {bilgi.veri?.epostaHazir === false && <p className="not">E-posta gönderimi için kurulumda e-posta (SMTP) ayarları yapılmalı. O zamana kadar WhatsApp ya da PDF kullanabilirsiniz.</p>}
      <MesajKutusu mesaj={mesaj} />
    </div>
  );
}

/** Sipariş satırları ve her ürün için: depodaki stok, depoya son gelen (alış ya da sevk), son alış fiyatı, raf fiyatı, kâr, satış. */
function SiparisIcerik({ satirlar, bilgiler, yukleniyor, hata }: { satirlar: Detay[]; bilgiler: UrunBilgisi[] | null; yukleniyor: boolean; hata: string | null }) {
  const b = new Map((bilgiler ?? []).map((x) => [x.stokKodu, x]));
  const toplam = (f: (d: Detay, x: UrunBilgisi) => number | null) =>
    satirlar.reduce((t, d) => { const x = b.get(d.stokKodu); const v = x ? f(d, x) : null; return t + (v ?? 0); }, 0);
  return (
    <>
      {hata && <div className="uyari hata">Ürün bilgileri alınamadı: {hata}</div>}
      <table className="ic">
        <thead>
          <tr>
            <th>Ürün</th><th className="s">Sipariş</th><th className="s">Depoda stok</th><th>Depoya son gelen</th>
            <th className="s">Son alış KDV hariç</th><th className="s">Son alış KDV dahil</th><th className="s">Raf fiyatı</th><th className="s">Kâr</th>
            <th className="s">30 gün satış</th><th className="s">Kaç günlük</th>
          </tr>
        </thead>
        <tbody>
          {satirlar.map((d) => {
            const x = b.get(d.stokKodu);
            return (
              <tr key={d.satirNo}>
                <td>{d.urunAdi}<br /><small className="not">{d.barkod}</small></td>
                <td className="s"><b>{miktar(d.miktar)}</b> {d.birim}{d.teslim ? <><br /><small className="not">gelen {miktar(d.teslim)}</small></> : null}</td>
                <td className={"s" + (x && x.stok < 0 ? " eksi" : "")}>{x ? miktar(x.stok) : ""}</td>
                <td style={{ whiteSpace: "normal", minWidth: 180 }}>
                  {x?.sonGelisTarih ? (
                    <>
                      <b>{miktar(x.sonGelisMiktar)}</b> {d.birim} · {tarih(x.sonGelisTarih)}{" "}
                      <span className={"pill " + (x.sonGelisTur === "sevk" ? "mor" : "mavi")}>{x.sonGelisTur === "sevk" ? "Sevk" : "Alış"}</span>
                      <br /><small className="not">{x.sonGelisKaynak}{x.sonGelisEvrak ? ` · ${x.sonGelisEvrak}` : ""}</small>
                    </>
                  ) : x ? <span className="not">son 1 yılda giriş yok</span> : null}
                </td>
                <td className="s" title={x?.sonAlisCari ?? ""}>
                  {x?.sonAlisFiyat != null ? <>{para(x.sonAlisFiyat)}<br /><small className="not">{tarih(x.sonAlisTarih)}</small></> : x ? <span className="not">alış yok</span> : ""}
                </td>
                <td className="s">{x?.sonAlisKdvDahil != null ? <>{para(x.sonAlisKdvDahil)}<br /><small className="not">KDV %{miktar(x.kdvOrani)}</small></> : ""}</td>
                <td className="s">{x?.satisFiyati ? para(x.satisFiyati) : ""}</td>
                <td className="s">{x?.karOrani != null && <span className={"pill " + (x.karOrani < 10 ? "kirmizi" : x.karOrani < 20 ? "turuncu" : "yesil")}>{yuzde(x.karOrani)}</span>}</td>
                <td className="s">{x ? miktar(x.satis30) : ""}</td>
                <td className="s">{x && x.stok <= 0 ? <span className="not">stok yok</span> : x?.kacGunluk != null ? `${miktar(x.kacGunluk)} gün` : ""}</td>
              </tr>
            );
          })}
          {bilgiler && (
            <tr className="toplam">
              <td>{satirlar.length} kalem</td><td className="s">{miktar(satirlar.reduce((t, d) => t + d.miktar, 0))}</td><td></td>
              <td className="not">sipariş tutarı (son alışla)</td>
              <td className="s">{para(toplam((d, x) => x.sonAlisFiyat == null ? null : x.sonAlisFiyat * d.miktar))}</td>
              <td className="s">{para(toplam((d, x) => x.sonAlisKdvDahil == null ? null : x.sonAlisKdvDahil * d.miktar))}</td>
              <td className="s">{para(toplam((d, x) => x.satisFiyati == null ? null : x.satisFiyati * d.miktar))}</td>
              <td colSpan={3}></td>
            </tr>
          )}
        </tbody>
      </table>
      {yukleniyor && !bilgiler && <div className="bilgi">Stok, son giriş ve fiyat bilgileri yükleniyor…</div>}
      {bilgiler && (
        <p className="not">
          Stok ve satış siparişin verildiği depoya göredir. Son gelen: bu depoya en son gelen alış ya da depolar arası sevk. Son alış fiyatı tüm depolardaki
          en son alıştır (iskontolu); üzerine gelince tedarikçisi görünür. Kâr: raf fiyatı ile son alışın KDV dahil hali arasındaki fark.
        </p>
      )}
    </>
  );
}
