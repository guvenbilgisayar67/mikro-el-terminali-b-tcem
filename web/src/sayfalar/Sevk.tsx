import { Fragment, useRef, useState } from "react";
import { api, bugun, miktar, para, qs, tarih } from "../api";
import { Alan, DepoSec, Durum, Mesaj, MesajKutusu, useVeri, YazmaDurumu } from "../ortak";

type Satir = { kod: string; ad: string; birim: string; miktar: number; birimFiyat: number; tutar: number };
type Ozet = { seri: string; sira: number; tarih: string; cikisDepo: number; girisDepo: number; satirSayisi: number; miktar: number; tutar: number };
type Detay = { satirNo: number; stokKodu: string; urunAdi: string; miktar: number; tutar: number };

export default function Sevk({ depoNo }: { depoNo: number | null }) {
  const [cikis, setCikis] = useState<number | "">(depoNo ?? "");
  const [giris, setGiris] = useState<number | "">("");
  const [gun, setGun] = useState(bugun());
  const [satirlar, setSatirlar] = useState<Satir[]>([]);
  const [barkod, setBarkod] = useState("");
  const [mesaj, setMesaj] = useState<Mesaj>(null);
  const [kaydediliyor, setKaydediliyor] = useState(false);
  const [acik, setAcik] = useState<string | null>(null);
  const girisRef = useRef<HTMLInputElement>(null);
  const liste = useVeri<Ozet[]>(`/api/sevk?${qs({ bas: null, bit: null })}`);
  const detay = useVeri<Detay[]>(acik ? `/api/sevk/detay?${acik}` : null);

  const istek = (s: Satir[]) => ({ cikisDepo: cikis, girisDepo: giris, tarih: gun, satirlar: s.map((x) => ({ urun: x.kod, miktar: x.miktar })) });

  async function ekle(e: React.FormEvent) {
    e.preventDefault();
    const b = barkod.trim();
    setBarkod("");
    girisRef.current?.focus();
    if (!b) return;
    const var_ = satirlar.find((s) => s.kod === b);
    if (var_) { miktarDegis(var_.kod, var_.miktar + 1); return; }
    try {
      const s = await api.get<Satir>(`/api/sevk/urun?${qs({ k: b, tarih: gun })}`);
      setSatirlar((eski) => eski.some((x) => x.kod === s.kod) ? eski.map((x) => x.kod === s.kod ? { ...x, miktar: x.miktar + 1, tutar: (x.miktar + 1) * x.birimFiyat } : x) : [...eski, s]);
      setMesaj(null);
    } catch (err) {
      setMesaj({ tur: "hata", metin: `${b}: ${(err as Error).message}` });
    }
  }

  const miktarDegis = (kod: string, m: number) =>
    setSatirlar((eski) => eski.map((x) => (x.kod === kod ? { ...x, miktar: m, tutar: m * x.birimFiyat } : x)));

  async function kaydet() {
    if (!cikis || !giris || satirlar.length === 0) return;
    setKaydediliyor(true);
    try {
      const r = await api.post<{ seri: string; sira: number; satirSayisi: number; tutar: number }>("/api/sevk", istek(satirlar));
      setMesaj({ tur: "bilgi", metin: `Sevk fişi kaydedildi: ${r.seri || "(seri yok)"} ${r.sira}, ${r.satirSayisi} satır, ${para(r.tutar)} TL.` });
      setSatirlar([]);
      liste.yenile();
    } catch (err) {
      setMesaj({ tur: "hata", metin: (err as Error).message });
    }
    setKaydediliyor(false);
  }

  const toplam = satirlar.reduce((s, x) => s + x.tutar, 0);
  const anahtar = (o: Ozet) => qs({ seri: o.seri, sira: o.sira });

  return (
    <section>
      <header className="sayfa-baslik"><h1>Depolar arası sevk</h1></header>
      <YazmaDurumu />
      <div className="filtre">
        <DepoSec etiket="Çıkış deposu" deger={cikis} degisti={setCikis} kilitli={depoNo != null} />
        <DepoSec etiket="Giriş deposu" deger={giris} degisti={setGiris} />
        <Alan etiket="Tarih"><input type="date" value={gun} max={bugun()} onChange={(e) => setGun(e.target.value)} /></Alan>
      </div>
      <form className="filtre" onSubmit={ekle}>
        <Alan etiket="Barkod okut ya da stok kodu yaz"><input ref={girisRef} value={barkod} onChange={(e) => setBarkod(e.target.value)} /></Alan>
        <button type="submit">Ekle</button>
      </form>
      <MesajKutusu mesaj={mesaj} />
      <table>
        <thead><tr><th>Ürün</th><th>Kod</th><th className="s">Miktar</th><th className="s">Birim maliyet</th><th className="s">Tutar</th><th></th></tr></thead>
        <tbody>
          {satirlar.length === 0 && <tr><td colSpan={6} className="bos">Liste boş. Gönderilecek ürünleri okutun.</td></tr>}
          {satirlar.map((s) => (
            <tr key={s.kod}>
              <td>{s.ad}</td><td>{s.kod}</td>
              <td className="s"><input className="adet" type="number" min={0.001} step="any" value={s.miktar} onChange={(e) => miktarDegis(s.kod, Math.max(0, +e.target.value))} /> {s.birim}</td>
              <td className="s">{para(s.birimFiyat)}</td><td className="s">{para(s.tutar)}</td>
              <td><button className="sil" title="Çıkar" onClick={() => setSatirlar(satirlar.filter((x) => x.kod !== s.kod))}>×</button></td>
            </tr>
          ))}
        </tbody>
      </table>
      <p className="not">Birim maliyet, ürünün son alış fiyatıdır (iskontolar düşülmüş). Sevk cariyi etkilemez, yalnız depoların stok ve maliyetini değiştirir.</p>
      <div className="kaydet">
        <span>{satirlar.length} ürün, {para(toplam)} TL</span>
        <button disabled={kaydediliyor || !cikis || !giris || cikis === giris || satirlar.length === 0 || satirlar.some((s) => s.miktar <= 0)} onClick={kaydet}>
          {kaydediliyor ? "Kaydediliyor…" : "Sevki kaydet"}
        </button>
      </div>

      <h2>Son 30 günün sevkleri</h2>
      <Durum hata={liste.hata} yukleniyor={liste.yukleniyor} />
      <table>
        <thead><tr><th>Evrak</th><th>Tarih</th><th>Çıkış</th><th>Giriş</th><th className="s">Satır</th><th className="s">Miktar</th><th className="s">Tutar</th></tr></thead>
        <tbody>
          {liste.veri?.length === 0 && <tr><td colSpan={7} className="bos">Bu aralıkta sevk yok.</td></tr>}
          {liste.veri?.map((o) => (
            <Fragment key={anahtar(o)}>
              <tr className={"tiklanir" + (acik === anahtar(o) ? " secili" : "")} onClick={() => setAcik(acik === anahtar(o) ? null : anahtar(o))}>
                <td>{o.seri} {o.sira}</td><td>{tarih(o.tarih)}</td><td>{o.cikisDepo}</td><td>{o.girisDepo}</td>
                <td className="s">{o.satirSayisi}</td><td className="s">{miktar(o.miktar)}</td><td className="s">{para(o.tutar)}</td>
              </tr>
              {acik === anahtar(o) && (
                <tr className="alt-satir">
                  <td colSpan={7}>
                    <Durum hata={detay.hata} yukleniyor={detay.yukleniyor} />
                    <table className="ic">
                      <tbody>
                        {detay.veri?.map((d) => (
                          <tr key={d.satirNo}><td>{d.urunAdi}</td><td>{d.stokKodu}</td><td className="s">{miktar(d.miktar)}</td><td className="s">{para(d.tutar)}</td></tr>
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
