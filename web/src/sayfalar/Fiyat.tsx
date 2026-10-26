import { Fragment, useRef, useState } from "react";
import { api, bugun, para, qs, sayiOku, tarih } from "../api";
import { Alan, Durum, Mesaj, MesajKutusu, useVeri, YazmaDurumu } from "../ortak";

type Urun = { kod: string; ad: string; birim: string; fiyat: number; kdvOrani: number; sonAlis: number; sonDegisim: string | null };
type Satir = Urun & { yeni: string };
type Ozet = { sira: number; tarih: string; satirSayisi: number; olusturma: string };
type Detay = { satirNo: number; stokKodu: string; urunAdi: string; eskiFiyat: number; yeniFiyat: number };

const n = (s: string) => sayiOku(s) || 0;
const yuzde = (v: number) => `${v >= 0 ? "+" : ""}${v.toLocaleString("tr-TR", { maximumFractionDigits: 1 })}%`;
// Kâr: KDV hariç satış fiyatının son alış fiyatına göre farkı.
const kar = (fiyat: number, u: Urun) => (u.sonAlis > 0 && fiyat > 0 ? ((fiyat / (1 + u.kdvOrani / 100) - u.sonAlis) / u.sonAlis) * 100 : null);

export default function Fiyat() {
  const [satirlar, setSatirlar] = useState<Satir[]>([]);
  const [barkod, setBarkod] = useState("");
  const [toplu, setToplu] = useState("");
  const [gun, setGun] = useState(bugun());
  const [mesaj, setMesaj] = useState<Mesaj>(null);
  const [kaydediliyor, setKaydediliyor] = useState(false);
  const [acik, setAcik] = useState<number | null>(null);
  const ref = useRef<HTMLInputElement>(null);
  const liste = useVeri<Ozet[]>(`/api/fiyat?${qs({ bas: null, bit: null })}`);
  const detay = useVeri<Detay[]>(acik ? `/api/fiyat/detay?sira=${acik}` : null);

  async function ekle(e: React.FormEvent) {
    e.preventDefault();
    const b = barkod.trim();
    setBarkod("");
    ref.current?.focus();
    if (!b) return;
    try {
      const u = await api.get<Urun>(`/api/fiyat/urun?${qs({ k: b })}`);
      if (satirlar.some((s) => s.kod === u.kod)) { setMesaj({ tur: "hata", metin: `${u.ad} listede zaten var.` }); return; }
      setSatirlar((eski) => [...eski, { ...u, yeni: "" }]);
      setMesaj(null);
    } catch (err) {
      setMesaj({ tur: "hata", metin: `${b}: ${(err as Error).message}` });
    }
  }

  const degis = (kod: string, yeni: string) => setSatirlar((eski) => eski.map((s) => (s.kod === kod ? { ...s, yeni } : s)));

  // Seçilen yüzde mevcut fiyatlara uygulanır, kuruşa yuvarlanır.
  function topluUygula() {
    const p = n(toplu);
    if (!p) return;
    setSatirlar((eski) => eski.map((s) => ({ ...s, yeni: (Math.round(s.fiyat * (1 + p / 100) * 100) / 100).toFixed(2).replace(".", ",") })));
  }

  async function kaydet() {
    setKaydediliyor(true);
    try {
      const r = await api.post<{ sira: number; satirSayisi: number }>("/api/fiyat", {
        tarih: gun, satirlar: satirlar.map((s) => ({ urun: s.kod, yeniFiyat: n(s.yeni) })),
      });
      setMesaj({ tur: "bilgi", metin: `Fiyat değişikliği fişi ${r.sira} kaydedildi, ${r.satirSayisi} ürün. Mikro'da satış fiyatı değişti; etiketleri Etiket sayfasındaki "fiyatı değişenler" ile basabilirsiniz.` });
      setSatirlar([]);
      liste.yenile();
    } catch (err) {
      setMesaj({ tur: "hata", metin: (err as Error).message });
    }
    setKaydediliyor(false);
  }

  const gecerli = satirlar.length > 0 && satirlar.every((s) => n(s.yeni) > 0 && n(s.yeni) !== s.fiyat);

  return (
    <section>
      <header className="sayfa-baslik"><h1>Fiyat değişikliği</h1></header>
      <YazmaDurumu />
      <p className="not">Tek fiyat: yeni satış fiyatı (KDV dahil) bütün mağazalarda geçerli olur. Mikro'ya fiyat değişiklik fişi olarak işlenir; kasalar değişen kartı Mikro'dan alır.</p>
      <div className="filtre">
        <form className="filtre" onSubmit={ekle}>
          <Alan etiket="Barkod okut ya da stok kodu yaz"><input ref={ref} value={barkod} onChange={(e) => setBarkod(e.target.value)} /></Alan>
          <button type="submit">Ekle</button>
        </form>
        <Alan etiket="Tarih"><input type="date" value={gun} max={bugun()} onChange={(e) => setGun(e.target.value)} /></Alan>
        <Alan etiket="Hepsine % uygula"><input className="adet" inputMode="decimal" value={toplu} placeholder="ör. 10" onChange={(e) => setToplu(e.target.value)} /></Alan>
        <button type="button" className="ikincil" disabled={!satirlar.length || !n(toplu)} onClick={topluUygula}>Uygula</button>
      </div>
      <MesajKutusu mesaj={mesaj} />
      <table>
        <thead><tr><th>Ürün</th><th>Kod</th><th>Son değişim</th><th className="s">Son alış (KDV hariç)</th><th className="s">Şu anki fiyat</th><th className="s">Kâr</th><th className="s">Yeni fiyat</th><th className="s">Değişim</th><th className="s">Yeni kâr</th><th></th></tr></thead>
        <tbody>
          {satirlar.length === 0 && <tr><td colSpan={10} className="bos">Fiyatı değişecek ürünleri okutun.</td></tr>}
          {satirlar.map((s) => {
            const y = n(s.yeni), k0 = kar(s.fiyat, s), k1 = kar(y, s);
            return (
              <tr key={s.kod}>
                <td>{s.ad}</td><td>{s.kod}</td><td>{tarih(s.sonDegisim)}</td>
                <td className="s">{s.sonAlis ? para(s.sonAlis) : ""}</td>
                <td className="s">{para(s.fiyat)}</td>
                <td className="s">{k0 == null ? "" : yuzde(k0)}</td>
                <td className="s"><input className="fiyat" inputMode="decimal" value={s.yeni} onChange={(e) => degis(s.kod, e.target.value)} /></td>
                <td className="s">{y && s.fiyat ? yuzde(((y - s.fiyat) / s.fiyat) * 100) : ""}</td>
                <td className={"s" + (k1 != null && k1 < 0 ? " eksi" : "")}>{k1 == null || !y ? "" : yuzde(k1) + (k1 < 0 ? " zarar" : "")}</td>
                <td><button className="sil" title="Çıkar" onClick={() => setSatirlar(satirlar.filter((x) => x.kod !== s.kod))}>×</button></td>
              </tr>
            );
          })}
        </tbody>
      </table>
      <div className="kaydet">
        <span>{satirlar.length} ürün</span>
        <button disabled={!gecerli || kaydediliyor} onClick={kaydet}>{kaydediliyor ? "Kaydediliyor…" : "Fiyatları değiştir"}</button>
      </div>

      <h2>Son 30 fiyat değişikliği fişi</h2>
      <Durum hata={liste.hata} yukleniyor={liste.yukleniyor} />
      <table>
        <thead><tr><th>Fiş</th><th>Tarih</th><th className="s">Ürün</th><th>Girilme</th></tr></thead>
        <tbody>
          {liste.veri?.length === 0 && <tr><td colSpan={4} className="bos">Bu aralıkta fiyat değişikliği yok.</td></tr>}
          {liste.veri?.map((o) => (
            <Fragment key={o.sira}>
              <tr className={"tiklanir" + (acik === o.sira ? " secili" : "")} onClick={() => setAcik(acik === o.sira ? null : o.sira)}>
                <td>{o.sira}</td><td>{tarih(o.tarih)}</td><td className="s">{o.satirSayisi}</td><td>{new Date(o.olusturma).toLocaleString("tr-TR")}</td>
              </tr>
              {acik === o.sira && (
                <tr className="alt-satir">
                  <td colSpan={4}>
                    <Durum hata={detay.hata} yukleniyor={detay.yukleniyor} />
                    <table className="ic">
                      <tbody>
                        {detay.veri?.map((d) => (
                          <tr key={d.satirNo}><td>{d.urunAdi}</td><td>{d.stokKodu}</td><td className="s">{para(d.eskiFiyat)}</td><td className="s">→ {para(d.yeniFiyat)}</td></tr>
                        ))}
                      </tbody>
                    </table>
                  </td>
                </tr>
              )}
            </Fragment>
          ))}
        </tbody>
      </table>
    </section>
  );
}
