import { useEffect, useState } from "react";
import { Oturum, oturumDustugunde, oturumOku, oturumYaz } from "./api";
import Cari from "./sayfalar/Cari";
import Cek from "./sayfalar/Cek";
import CekIslem from "./sayfalar/CekIslem";
import Ciro from "./sayfalar/Ciro";
import Etiket from "./sayfalar/Etiket";
import Fatura from "./sayfalar/Fatura";
import Giris from "./sayfalar/Giris";
import Satis from "./sayfalar/Satis";
import Sevk from "./sayfalar/Sevk";
import Yonetim from "./sayfalar/Yonetim";

type Sayfa = "ciro" | "satis" | "cari" | "cek" | "cekislem" | "fatura" | "sevk" | "etiket" | "yonetim";
const SAYFALAR: { id: Sayfa; ad: string; roller: string[] }[] = [
  { id: "ciro", ad: "Günün cirosu", roller: ["admin", "merkez"] },
  { id: "satis", ad: "Satış raporu", roller: ["admin", "merkez"] },
  { id: "cari", ad: "Cari ekstre", roller: ["admin", "merkez"] },
  { id: "cek", ad: "Çek raporu", roller: ["admin", "merkez"] },
  { id: "cekislem", ad: "Çek giriş / çıkış", roller: ["admin", "merkez"] },
  { id: "fatura", ad: "Fatura ve irsaliye", roller: ["admin", "merkez"] },
  { id: "sevk", ad: "Depolar arası sevk", roller: ["admin", "merkez", "magaza"] },
  { id: "etiket", ad: "Etiket", roller: ["admin", "merkez", "magaza"] },
  { id: "yonetim", ad: "Yönetim", roller: ["admin"] },
];

export default function App() {
  const [oturum, setOturum] = useState<Oturum | null>(oturumOku());
  const izinli = SAYFALAR.filter((s) => oturum && s.roller.includes(oturum.rol));
  const [sayfa, setSayfa] = useState<Sayfa>(() => (location.hash.slice(1) as Sayfa) || "ciro");

  useEffect(() => { oturumDustugunde(() => { oturumYaz(null); setOturum(null); }); }, []);
  useEffect(() => { location.hash = sayfa; }, [sayfa]);

  if (!oturum) return <Giris girdi={(o) => { oturumYaz(o); setOturum(o); }} />;
  const aktif = izinli.find((s) => s.id === sayfa) ?? izinli[0];

  return (
    <div className="kabuk">
      <nav>
        <div className="marka">Bütçem</div>
        {izinli.map((s) => (
          <button key={s.id} className={s.id === aktif.id ? "aktif" : ""} onClick={() => setSayfa(s.id)}>{s.ad}</button>
        ))}
        <div className="kullanici">
          <span>{oturum.adSoyad}</span>
          <button className="ikincil" onClick={() => { oturumYaz(null); setOturum(null); }}>Çıkış</button>
        </div>
      </nav>
      <main>
        {aktif.id === "ciro" && <Ciro />}
        {aktif.id === "satis" && <Satis depoKilitli={oturum.rol === "magaza"} />}
        {aktif.id === "cari" && <Cari />}
        {aktif.id === "cek" && <Cek />}
        {aktif.id === "cekislem" && <CekIslem />}
        {aktif.id === "fatura" && <Fatura depoNo={null} />}
        {aktif.id === "sevk" && <Sevk depoNo={oturum.rol === "magaza" ? oturum.depoNo : null} />}
        {aktif.id === "etiket" && <Etiket />}
        {aktif.id === "yonetim" && <Yonetim />}
      </main>
    </div>
  );
}
