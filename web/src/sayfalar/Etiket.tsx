import { useEffect, useRef, useState } from "react";
import { api, bugun, para, qs, saat } from "../api";
import { Alan, Durum, useVeri } from "../ortak";

type Urun = { stokKodu: string; urunAdi: string; barkod: string; birim: string | null; fiyat: number; fiyatDegisimTarihi: string | null };
type Yazici = { id: number; ad: string; tur: string; dil: string; depoNo: number; ajanAdi: string; ajanSonGorulme: string | null };
type Is = { id: number; yazici: string; sablon: string; adet: number; durum: number; hata: string | null; olusturmaZamani: string; bitisZamani: string | null; olusturan: string };
type Kalem = Urun & { adet: number };

const DURUM = ["Bekliyor", "Yazıcıya gönderildi", "Basıldı", "Hata"];

function Onizleme({ barkod, sablon }: { barkod: string; sablon: string }) {
  const [url, setUrl] = useState<string | null>(null);
  useEffect(() => {
    let u: string | null = null;
    api.get<Blob>(`/api/etiket/onizleme?${qs({ barkod, sablon })}`).then((b) => setUrl((u = URL.createObjectURL(b)))).catch(() => setUrl(null));
    return () => { if (u) URL.revokeObjectURL(u); };
  }, [barkod, sablon]);
  return url ? <img className="onizleme" src={url} alt="Etiket önizleme" /> : <div className="bilgi">Önizleme hazırlanıyor…</div>;
}

export default function EtiketSayfa() {
  const [kalemler, setKalemler] = useState<Kalem[]>([]);
  const [barkod, setBarkod] = useState("");
  const [sablon, setSablon] = useState("raf");
  const [yaziciId, setYaziciId] = useState<number | "">("");
  const [mesaj, setMesaj] = useState<{ tur: "bilgi" | "hata"; metin: string } | null>(null);
  const [secili, setSecili] = useState<string | null>(null);
  const [gun, setGun] = useState(bugun());
  const giris = useRef<HTMLInputElement>(null);
  const yazicilar = useVeri<Yazici[]>("/api/etiket/yazicilar");
  const isler = useVeri<Is[]>("/api/etiket/isler?ilk=20");

  useEffect(() => {
    if (yaziciId === "" && yazicilar.veri?.length) setYaziciId(yazicilar.veri[0].id);
  }, [yazicilar.veri, yaziciId]);
  useEffect(() => {
    const t = setInterval(isler.yenile, 5000);
    return () => clearInterval(t);
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  const ekle = (yeni: Urun[]) =>
    setKalemler((eski) => {
      const liste = [...eski];
      for (const u of yeni) {
        const var_ = liste.find((k) => k.stokKodu === u.stokKodu);
        if (var_) var_.adet += 1;
        else liste.push({ ...u, adet: 1 });
      }
      return liste;
    });

  async function barkodEkle(e: React.FormEvent) {
    e.preventDefault();
    const b = barkod.trim();
    if (!b) return;
    try {
      const r = await api.get<{ urun: Urun }>(`/api/stok/${encodeURIComponent(b)}?miktar=false`);
      ekle([r.urun]);
      setSecili(r.urun.barkod);
      setMesaj(null);
    } catch (err) {
      setMesaj({ tur: "hata", metin: `${b}: ${(err as Error).message}` });
    }
    setBarkod("");
    giris.current?.focus();
  }

  async function degisenleriEkle() {
    try {
      const r = await api.get<{ adet: number; urunler: Urun[] }>(`/api/etiket/degisenler?${qs({ gun })}`);
      ekle(r.urunler);
      setMesaj({ tur: "bilgi", metin: `${r.adet} ürünün fiyatı ${gun} tarihinde değişmiş; listeye eklendi.` });
    } catch (err) {
      setMesaj({ tur: "hata", metin: (err as Error).message });
    }
  }

  async function bas() {
    if (!yaziciId || kalemler.length === 0) return;
    try {
      const r = await api.post<{ isler: number[]; basilacak: number; bulunamayan: string[] }>("/api/etiket/bas", {
        yaziciId, sablon, kalemler: kalemler.map((k) => ({ barkod: k.barkod, adet: k.adet })),
      });
      setMesaj({ tur: "bilgi", metin: `${r.basilacak} etiket yazıcı kuyruğuna gönderildi.${r.bulunamayan.length ? " Bulunamayan: " + r.bulunamayan.join(", ") : ""}` });
      setKalemler([]);
      isler.yenile();
    } catch (err) {
      setMesaj({ tur: "hata", metin: (err as Error).message });
    }
  }

  const yazici = yazicilar.veri?.find((y) => y.id === yaziciId);
  const ajanCevrimdisi = yazici && (!yazici.ajanSonGorulme || Date.now() - new Date(yazici.ajanSonGorulme).getTime() > 2 * 60_000);
  const toplam = kalemler.reduce((s, k) => s + k.adet, 0);

  return (
    <section>
      <header className="sayfa-baslik"><h1>Etiket bas</h1></header>
      <div className="iki-sutun">
        <div>
          <form className="filtre" onSubmit={barkodEkle}>
            <Alan etiket="Barkod okut ya da yaz"><input ref={giris} autoFocus value={barkod} onChange={(e) => setBarkod(e.target.value)} /></Alan>
            <button type="submit">Ekle</button>
          </form>
          <div className="filtre">
            <Alan etiket="Fiyatı değişenler"><input type="date" value={gun} onChange={(e) => setGun(e.target.value)} /></Alan>
            <button type="button" className="ikincil" onClick={degisenleriEkle}>Listeye ekle</button>
          </div>
          {mesaj && <div className={mesaj.tur === "hata" ? "uyari hata" : "uyari"}>{mesaj.metin}</div>}
          <table>
            <thead><tr><th>Ürün</th><th>Barkod</th><th className="s">Fiyat</th><th className="s">Adet</th><th></th></tr></thead>
            <tbody>
              {kalemler.length === 0 && <tr><td colSpan={5} className="bos">Liste boş. Barkod okutun ya da fiyatı değişenleri ekleyin.</td></tr>}
              {kalemler.map((k) => (
                <tr key={k.stokKodu} className={"tiklanir" + (secili === k.barkod ? " secili" : "")} onClick={() => setSecili(k.barkod)}>
                  <td>{k.urunAdi}</td><td>{k.barkod}</td><td className="s">{para(k.fiyat)}</td>
                  <td className="s">
                    <input className="adet" type="number" min={1} max={500} value={k.adet} onClick={(e) => e.stopPropagation()}
                      onChange={(e) => setKalemler(kalemler.map((x) => x.stokKodu === k.stokKodu ? { ...x, adet: Math.max(1, Math.min(500, +e.target.value || 1)) } : x))} />
                  </td>
                  <td><button className="sil" title="Çıkar" onClick={(e) => { e.stopPropagation(); setKalemler(kalemler.filter((x) => x.stokKodu !== k.stokKodu)); }}>×</button></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <div className="detay">
          <div className="filtre">
            <Alan etiket="Şablon">
              <select value={sablon} onChange={(e) => setSablon(e.target.value)}><option value="raf">Raf etiketi</option><option value="manav">Manav etiketi</option></select>
            </Alan>
            <Alan etiket="Yazıcı">
              <select value={yaziciId} onChange={(e) => setYaziciId(+e.target.value)}>
                {yazicilar.veri?.map((y) => <option key={y.id} value={y.id}>{y.ad} ({y.ajanAdi}{y.tur === "a4" ? ", A4" : ""})</option>)}
              </select>
            </Alan>
            <button disabled={!yaziciId || toplam === 0} onClick={bas}>{toplam} etiket bas</button>
          </div>
          {yazicilar.veri?.length === 0 && <div className="uyari">Tanımlı yazıcı yok. Yönetim sayfasından mağaza bilgisayarını ve yazıcıyı ekleyin.</div>}
          {ajanCevrimdisi && <div className="uyari">{yazici!.ajanAdi} bilgisayarındaki yazdırma programı son 2 dakikadır görünmüyor; iş kuyrukta bekler, program açılınca basılır.</div>}
          {secili && <Onizleme barkod={secili} sablon={sablon} />}
          <h2>Son işler</h2>
          <Durum hata={isler.hata} yukleniyor={false} />
          <table>
            <thead><tr><th>Zaman</th><th>Yazıcı</th><th className="s">Adet</th><th>Durum</th><th>Kim</th></tr></thead>
            <tbody>
              {isler.veri?.map((i) => (
                <tr key={i.id}>
                  <td>{saat(i.olusturmaZamani)}</td><td>{i.yazici}</td><td className="s">{i.adet}</td>
                  <td className={i.durum === 3 ? "eksi" : ""} title={i.hata ?? ""}>{DURUM[i.durum]}{i.hata ? `: ${i.hata}` : ""}</td><td>{i.olusturan}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    </section>
  );
}
