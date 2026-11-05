import { useEffect, useRef, useState } from "react";
import { api, gunEkle, bugun, miktar, para, qs, saat, sayiOku, tarih } from "../api";
import { Alan, CariOzet, CariSec, Cipler, Durum, Mesaj, MesajKutusu, useVeri } from "../ortak";

type Ozet = {
  id: number; tur: "planli" | "plansiz"; depo: number; depoAdi: string; durum: "bekliyor" | "onaylandi" | "reddedildi"; not: string;
  olusturan: string; zaman: string; satirSayisi: number; miktar: number; onaylayan: string | null; onayZamani: string | null;
  redSebebi: string | null; evraklar: string | null;
};
type Satir = {
  id: number; stokKodu: string; urunAdi: string; birim: string; kaynakTur: "tedarikci" | "depo"; kaynakKod: string; kaynakAd: string;
  oneri: number | null; istenenMiktar: number; onayMiktar: number | null; birimFiyat: number | null; seri: string | null; sira: number | null;
};
type Detay = { talep: Ozet; satirlar: Satir[] };
type Duzen = { miktar: string; kaynakTur?: string; kaynakKod?: string; kaynakAd?: string };
type Ek = { urun: string; ad: string; birim: string; miktar: string; kaynakTur: string; kaynakKod: string; kaynakAd: string; birimFiyat: number };
type OnaySonuc = { evraklar: { tur: string; seri: string; sira: number; satirSayisi: number; tutar: number }[]; hata: string | null };

const DURUM: Record<Ozet["durum"], string> = { bekliyor: "Onay bekliyor", onaylandi: "Onaylandı, Mikro'da", reddedildi: "Reddedildi" };
const TUR: Record<Ozet["tur"], string> = { planli: "Planlı", plansiz: "Plansız" };
const n = (s: string) => sayiOku(s) || 0;

/** Mağazaların (terminalden) verdiği sipariş talepleri: patron miktarları düzeltir, onaylar, Mikro'ya aktarılır. */
export default function SiparisOnay({ onayYetkisi }: { onayYetkisi: boolean }) {
  const [durum, setDurum] = useState<string>("bekliyor");
  const [secili, setSecili] = useState<number | null>(null);
  const [yenile, setYenile] = useState(0);
  const liste = useVeri<Ozet[]>(`/api/siparis-talep?${qs({ durum: durum === "hepsi" ? null : durum, y: yenile })}`);

  return (
    <section>
      <h1>Sipariş onayı</h1>
      <p className="not">
        Mağazaların terminalden verdiği planlı ve plansız siparişler burada onay bekler. Miktarı artırıp azaltabilir, 0 yazarak ürünü çıkarabilir,
        ürün ekleyebilirsiniz. <b>Onayla</b> denince her tedarikçi ve depo için ayrı sipariş Mikro'ya yazılır; e-posta / WhatsApp gönderimi Sipariş sayfasından yapılır.
      </p>
      <div className="filtre">
        <Cipler secenekler={[["bekliyor", "Onay bekleyen"], ["onaylandi", "Onaylanan"], ["reddedildi", "Reddedilen"], ["hepsi", "Hepsi"]]} deger={durum} degisti={(d) => { setDurum(d); setSecili(null); }} />
      </div>
      <Durum hata={liste.hata} yukleniyor={liste.yukleniyor} />
      <div className="iki-sutun">
        <div className="liste">
          {liste.veri?.length === 0 && <p className="bilgi">Talep yok.</p>}
          {liste.veri?.map((t) => (
            <button key={t.id} className={"liste-satir" + (t.id === secili ? " secili" : "")} onClick={() => setSecili(t.id)}>
              <span>{t.depo} · {t.depoAdi}<small>{TUR[t.tur]} · {t.olusturan} · {tarih(t.zaman)} {saat(t.zaman)}</small></span>
              <span>{t.satirSayisi} kalem<small>{DURUM[t.durum]}</small></span>
            </button>
          ))}
        </div>
        <div>
          {secili == null ? <p className="bilgi">Soldan bir talep seçin.</p>
            : <TalepDetay key={secili} id={secili} onayYetkisi={onayYetkisi} bitti={() => setYenile((x) => x + 1)} />}
        </div>
      </div>
    </section>
  );
}

function TalepDetay({ id, onayYetkisi, bitti }: { id: number; onayYetkisi: boolean; bitti: () => void }) {
  const d = useVeri<Detay>(`/api/siparis-talep/${id}`);
  const [duzen, setDuzen] = useState<Record<number, Duzen>>({});
  const [ekler, setEkler] = useState<Ek[]>([]);
  const [barkod, setBarkod] = useState("");
  const [teslim, setTeslim] = useState(gunEkle(bugun(), 1));
  const [mesaj, setMesaj] = useState<Mesaj>(null);
  const [mesgul, setMesgul] = useState(false);
  const ref = useRef<HTMLInputElement>(null);

  useEffect(() => {
    if (!d.veri) return;
    const m: Record<number, Duzen> = {};
    for (const s of d.veri.satirlar) m[s.id] = { miktar: String(s.onayMiktar ?? s.istenenMiktar) };
    setDuzen(m);
  }, [d.veri]);

  if (!d.veri) return <Durum hata={d.hata} yukleniyor={d.yukleniyor} />;
  const { talep, satirlar } = d.veri;
  const acik = talep.durum === "bekliyor" && onayYetkisi;

  const deg = (sid: number, p: Partial<Duzen>) => setDuzen((e) => ({ ...e, [sid]: { ...e[sid], ...p } }));
  const kaynakAd = (s: Satir) => duzen[s.id]?.kaynakAd ?? s.kaynakAd;
  const kaynakKod = (s: Satir) => duzen[s.id]?.kaynakKod ?? s.kaynakKod;

  let tutar = 0;
  for (const s of satirlar) if (s.birimFiyat && kaynakKod(s) === s.kaynakKod) tutar += n(duzen[s.id]?.miktar ?? "0") * s.birimFiyat;
  for (const e of ekler) tutar += n(e.miktar) * e.birimFiyat;

  async function ekle(ev: React.FormEvent) {
    ev.preventDefault();
    const b = barkod.trim();
    setBarkod("");
    ref.current?.focus();
    if (!b) return;
    try {
      const o = await api.get<{ kod: string; ad: string; birim: string; kaynakTur: string; kaynakKod: string; kaynakAd: string; birimFiyat: number; miktar: number }>(
        `/api/siparis-talep/urun?${qs({ k: b, depo: talep.depo })}`);
      if (satirlar.some((s) => s.stokKodu === o.kod)) {
        setMesaj({ tur: "hata", metin: `${o.ad} talepte zaten var; miktarını değiştirin.` });
        return;
      }
      const var_ = ekler.find((e) => e.urun === o.kod);
      if (var_) { setEkler((x) => x.map((e) => (e.urun === o.kod ? { ...e, miktar: String(n(e.miktar) + 1) } : e))); return; }
      setEkler((x) => [...x, { urun: o.kod, ad: o.ad, birim: o.birim, miktar: String(o.miktar || 1), kaynakTur: o.kaynakTur, kaynakKod: o.kaynakKod, kaynakAd: o.kaynakAd, birimFiyat: o.birimFiyat }]);
      setMesaj(null);
    } catch (err) {
      setMesaj({ tur: "hata", metin: `${b}: ${(err as Error).message}` });
    }
  }

  async function onayla() {
    setMesgul(true);
    setMesaj(null);
    try {
      const r = await api.post<OnaySonuc>(`/api/siparis-talep/${id}/onayla`, {
        satirlar: satirlar.filter((s) => s.sira == null).map((s) => ({
          id: s.id, miktar: n(duzen[s.id]?.miktar ?? "0"), kaynakTur: duzen[s.id]?.kaynakTur ?? null, kaynakKod: duzen[s.id]?.kaynakKod ?? null,
        })),
        ekler: ekler.map((e) => ({ urun: e.urun, miktar: n(e.miktar), kaynakTur: e.kaynakTur || null, kaynakKod: e.kaynakKod || null })),
        teslimTarihi: teslim,
      });
      const yazilan = r.evraklar.map((e) => `${e.seri} ${e.sira} (${e.satirSayisi} kalem)`).join(", ");
      if (r.hata) setMesaj({ tur: "hata", metin: (yazilan ? `Mikro'ya yazılan: ${yazilan}. ` : "") + r.hata + " Düzeltip tekrar onaylayın; yazılanlar tekrar yazılmaz." });
      else { setMesaj({ tur: "bilgi", metin: `Onaylandı. Mikro'ya yazılan siparişler: ${yazilan}.` }); }
      setEkler([]);
      d.yenile();
      bitti();
    } catch (err) {
      setMesaj({ tur: "hata", metin: (err as Error).message });
    }
    setMesgul(false);
  }

  async function reddet() {
    const sebep = window.prompt("Reddetme sebebi (mağaza görür):", "");
    if (sebep == null) return;
    setMesgul(true);
    try {
      await api.post(`/api/siparis-talep/${id}/reddet`, { sebep });
      d.yenile();
      bitti();
    } catch (err) {
      setMesaj({ tur: "hata", metin: (err as Error).message });
    }
    setMesgul(false);
  }

  return (
    <>
      <h2>Talep {talep.id} · {talep.depo} {talep.depoAdi}<small>{TUR[talep.tur]} sipariş · {talep.olusturan} · {tarih(talep.zaman)} {saat(talep.zaman)} · {DURUM[talep.durum]}</small></h2>
      {talep.not && <p className="not">Not: {talep.not}</p>}
      {talep.redSebebi && <p className="not">Red sebebi: {talep.redSebebi}</p>}
      {talep.evraklar && <p className="not">Mikro siparişleri: {talep.evraklar}{talep.onaylayan ? ` · onaylayan ${talep.onaylayan}` : ""}</p>}
      <MesajKutusu mesaj={mesaj} />
      <table>
        <thead><tr><th>Ürün</th><th>Kime</th><th className="s">Öneri</th><th className="s">İstenen</th><th className="s">Onay</th><th className="s">Son alış</th></tr></thead>
        <tbody>
          {satirlar.map((s) => {
            const yazildi = s.sira != null;
            return (
              <tr key={s.id}>
                <td>{s.urunAdi}<br /><small className="not">{s.stokKodu}</small></td>
                <td>
                  {kaynakAd(s) || <b>Kaynağı bilinmiyor</b>}<br /><small className="not">{s.kaynakTur === "depo" ? `Depo ${kaynakKod(s)}` : kaynakKod(s)}{yazildi ? ` · ${s.seri} ${s.sira}` : ""}</small>
                  {acik && !yazildi && !kaynakKod(s) && (
                    <CariSec deger={null} etiket="Tedarikçi seç" degisti={(c: CariOzet | null) => c && deg(s.id, { kaynakTur: "tedarikci", kaynakKod: c.kod, kaynakAd: c.unvan })} />
                  )}
                </td>
                <td className="s">{miktar(s.oneri)}</td>
                <td className="s">{miktar(s.istenenMiktar)}</td>
                <td className="s">
                  {acik && !yazildi
                    ? <><input className="adet" inputMode="decimal" value={duzen[s.id]?.miktar ?? ""} onChange={(e) => deg(s.id, { miktar: e.target.value })} /> {s.birim}</>
                    : <>{miktar(s.onayMiktar)} {s.birim}</>}
                </td>
                <td className="s">{s.birimFiyat ? para(s.birimFiyat) : ""}</td>
              </tr>
            );
          })}
          {ekler.map((e) => (
            <tr key={e.urun}>
              <td>{e.ad} <small className="not">(eklendi)</small><br /><small className="not">{e.urun}</small></td>
              <td>{e.kaynakAd || <b>Kaynağı bilinmiyor</b>}
                {!e.kaynakKod && <CariSec deger={null} etiket="Tedarikçi seç" degisti={(c: CariOzet | null) => c && setEkler((x) => x.map((y) => (y.urun === e.urun ? { ...y, kaynakTur: "tedarikci", kaynakKod: c.kod, kaynakAd: c.unvan } : y)))} />}
              </td>
              <td className="s"></td><td className="s"></td>
              <td className="s"><input className="adet" inputMode="decimal" value={e.miktar} onChange={(ev) => setEkler((x) => x.map((y) => (y.urun === e.urun ? { ...y, miktar: ev.target.value } : y)))} /> {e.birim}
                <button className="sil" title="Çıkar" onClick={() => setEkler((x) => x.filter((y) => y.urun !== e.urun))}>×</button></td>
              <td className="s">{e.birimFiyat ? para(e.birimFiyat) : ""}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {acik && (
        <>
          <form className="filtre" onSubmit={ekle} style={{ marginTop: 10 }}>
            <Alan etiket="Ürün ekle: barkod ya da stok kodu"><input ref={ref} value={barkod} onChange={(e) => setBarkod(e.target.value)} /></Alan>
            <button type="submit" className="ikincil">Ekle</button>
            <Alan etiket="Teslim tarihi"><input type="date" value={teslim} min={bugun()} onChange={(e) => setTeslim(e.target.value)} /></Alan>
          </form>
          <div className="kaydet">
            <span>{tutar > 0 ? `Yaklaşık ${para(tutar)} TL (tedarikçi kalemleri, son alış, KDV hariç)` : ""}</span>
            <button className="ikincil" disabled={mesgul} onClick={reddet}>Reddet</button>
            <button disabled={mesgul} onClick={onayla}>{mesgul ? "Mikro'ya yazılıyor…" : "Onayla ve Mikro'ya aktar"}</button>
          </div>
        </>
      )}
      {talep.durum === "bekliyor" && !onayYetkisi && <p className="bilgi">Onay için "Sipariş talebi onayı" yetkisi gerekir.</p>}
    </>
  );
}
