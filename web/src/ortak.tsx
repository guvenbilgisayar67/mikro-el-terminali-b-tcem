import { ReactNode, useEffect, useState } from "react";
import { api } from "./api";

export function useVeri<T>(yol: string | null) {
  const [veri, setVeri] = useState<T | null>(null);
  const [hata, setHata] = useState<string | null>(null);
  const [yukleniyor, setYukleniyor] = useState(false);
  const [sayac, setSayac] = useState(0);
  useEffect(() => {
    if (!yol) return;
    let iptal = false;
    setYukleniyor(true);
    setHata(null);
    api
      .get<T>(yol)
      .then((v) => !iptal && setVeri(v))
      .catch((e: Error) => !iptal && setHata(e.message))
      .finally(() => !iptal && setYukleniyor(false));
    return () => {
      iptal = true;
    };
  }, [yol, sayac]);
  return { veri, hata, yukleniyor, yenile: () => setSayac((s) => s + 1) };
}

export function Durum({ hata, yukleniyor }: { hata: string | null; yukleniyor: boolean }) {
  if (hata) return <div className="uyari hata">{hata}</div>;
  if (yukleniyor) return <div className="bilgi">Yükleniyor…</div>;
  return null;
}

export function Kart({ baslik, deger, alt }: { baslik: string; deger: ReactNode; alt?: ReactNode }) {
  return (
    <div className="kart">
      <div className="kart-baslik">{baslik}</div>
      <div className="kart-deger">{deger}</div>
      {alt && <div className="kart-alt">{alt}</div>}
    </div>
  );
}

export function Alan({ etiket, children }: { etiket: string; children: ReactNode }) {
  return (
    <label className="alan">
      <span>{etiket}</span>
      {children}
    </label>
  );
}

export type CariOzet = { kod: string; unvan: string };

/** Cari kod ya da unvanla arar; seçilen carinin kodunu döndürür. */
export function CariSec({ deger, degisti, etiket = "Cari" }: { deger: CariOzet | null; degisti: (c: CariOzet | null) => void; etiket?: string }) {
  const [ara, setAra] = useState("");
  const liste = useVeri<CariOzet[]>(ara.trim().length >= 2 ? `/api/cari?ara=${encodeURIComponent(ara.trim())}&ilk=20` : null);
  if (deger)
    return (
      <Alan etiket={etiket}>
        <span className="secilen">
          <b>{deger.kod}</b> {deger.unvan}
          <button type="button" className="sil" title="Değiştir" onClick={() => degisti(null)}>×</button>
        </span>
      </Alan>
    );
  return (
    <div className="cari-sec">
      <Alan etiket={etiket}>
        <input placeholder="Kod ya da unvan yazın" value={ara} onChange={(e) => setAra(e.target.value)} />
      </Alan>
      {ara.trim().length >= 2 && liste.veri && (
        <div className="liste acilir">
          {liste.veri.length === 0 && <div className="bilgi">Bulunamadı.</div>}
          {liste.veri.map((c) => (
            <button type="button" key={c.kod} className="liste-satir" onClick={() => { degisti(c); setAra(""); }}>
              <span>{c.unvan}<small>{c.kod}</small></span>
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

export type Depo = { no: number; ad: string };

export function DepoSec({ deger, degisti, etiket, kilitli }: { deger: number | ""; degisti: (n: number) => void; etiket: string; kilitli?: boolean }) {
  const depolar = useVeri<Depo[]>("/api/depolar");
  return (
    <Alan etiket={etiket}>
      <select value={deger} disabled={kilitli} onChange={(e) => degisti(+e.target.value)}>
        <option value="" disabled>Seçin</option>
        {depolar.veri?.map((d) => <option key={d.no} value={d.no}>{d.no} · {d.ad.trim()}</option>)}
      </select>
    </Alan>
  );
}

/** Yazma kapalıysa evrak ekranlarının üstünde uyarı. */
export function YazmaDurumu() {
  const d = useVeri<{ acik: boolean }>("/api/yazma/durum");
  if (!d.veri || d.veri.acik) return null;
  return <div className="uyari hata">Mikro'ya kayıt şu an kapalı (ayarlarda Yazma:Acik = false). Ekranı inceleyebilirsiniz ama Kaydet çalışmaz.</div>;
}

export type Mesaj = { tur: "bilgi" | "hata"; metin: string } | null;

export function MesajKutusu({ mesaj }: { mesaj: Mesaj }) {
  if (!mesaj) return null;
  return <div className={mesaj.tur === "hata" ? "uyari hata" : "uyari tamam"}>{mesaj.metin}</div>;
}
