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
