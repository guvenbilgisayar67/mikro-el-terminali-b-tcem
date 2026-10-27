import { useEffect, useState } from "react";
import { api, Oturum, oturumDustugunde, oturumOku, oturumYaz, yetkili } from "./api";
import Cari from "./sayfalar/Cari";
import Cek from "./sayfalar/Cek";
import CekIslem from "./sayfalar/CekIslem";
import Ciro from "./sayfalar/Ciro";
import Etiket from "./sayfalar/Etiket";
import Fatura from "./sayfalar/Fatura";
import Fiyat from "./sayfalar/Fiyat";
import Giris from "./sayfalar/Giris";
import Satis from "./sayfalar/Satis";
import Sevk from "./sayfalar/Sevk";
import Siparis from "./sayfalar/Siparis";
import Yonetim from "./sayfalar/Yonetim";

type Sayfa = "ciro" | "satis" | "cari" | "cek" | "cekislem" | "fatura" | "sevk" | "siparis" | "fiyat" | "etiket" | "yonetim";
// Her sayfa, listelenen yetkilerden birine sahip kullanıcıya görünür (admin hepsini görür).
const SAYFALAR: { id: Sayfa; ad: string; yetki: string[] }[] = [
  { id: "ciro", ad: "Günün cirosu", yetki: ["ciro"] },
  { id: "satis", ad: "Satış raporu", yetki: ["satis_raporu"] },
  { id: "cari", ad: "Cari ekstre", yetki: ["cari_ekstre"] },
  { id: "cek", ad: "Çek raporu", yetki: ["cek_raporu"] },
  { id: "cekislem", ad: "Çek giriş / çıkış", yetki: ["cek_giris", "cek_cikis"] },
  { id: "fatura", ad: "Fatura ve irsaliye", yetki: ["alis_fatura", "satis_fatura"] },
  { id: "sevk", ad: "Depolar arası sevk", yetki: ["sevk"] },
  { id: "siparis", ad: "Sipariş", yetki: ["siparis"] },
  { id: "fiyat", ad: "Fiyat değişikliği", yetki: ["fiyat_degisikligi"] },
  { id: "etiket", ad: "Etiket", yetki: ["etiket"] },
  { id: "yonetim", ad: "Yönetim", yetki: [] },
];

export default function App() {
  const [oturum, setOturum] = useState<Oturum | null>(oturumOku());
  const izinli = SAYFALAR.filter((s) => yetkili(oturum, ...s.yetki));
  const [sayfa, setSayfa] = useState<Sayfa>(() => (location.hash.slice(1) as Sayfa) || "ciro");

  useEffect(() => { oturumDustugunde(() => { oturumYaz(null); setOturum(null); }); }, []);
  // Yönetici yetkileri değiştirdiyse menü yeniden giriş gerekmeden güncellensin.
  useEffect(() => {
    if (!oturum) return;
    api.get<Omit<Oturum, "token">>("/api/ben").then((b) => {
      const o = { ...oturum, ...b };
      oturumYaz(o);
      setOturum(o);
    }).catch(() => {});
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [oturum?.token]);
  useEffect(() => { location.hash = sayfa; }, [sayfa]);

  if (!oturum) return <Giris girdi={(o) => { oturumYaz(o); setOturum(o); }} />;
  const aktif = izinli.find((s) => s.id === sayfa) ?? izinli[0];
  const depoKilidi = oturum.rol === "magaza" ? oturum.depoNo : null;

  return (
    <div className="kabuk">
      <nav>
        <div className="marka">Bütçem</div>
        {izinli.map((s) => (
          <button key={s.id} className={s.id === aktif?.id ? "aktif" : ""} onClick={() => setSayfa(s.id)}>{s.ad}</button>
        ))}
        <div className="kullanici">
          <span>{oturum.adSoyad}</span>
          <button className="ikincil" onClick={() => { oturumYaz(null); setOturum(null); }}>Çıkış</button>
        </div>
      </nav>
      <main>
        {!aktif && <p className="not">Size henüz bir ekran yetkisi verilmemiş. Yöneticinize başvurun.</p>}
        {aktif?.id === "ciro" && <Ciro />}
        {aktif?.id === "satis" && <Satis depoKilitli={depoKilidi != null} />}
        {aktif?.id === "cari" && <Cari />}
        {aktif?.id === "cek" && <Cek />}
        {aktif?.id === "cekislem" && <CekIslem oturum={oturum} />}
        {aktif?.id === "fatura" && <Fatura depoNo={depoKilidi} oturum={oturum} />}
        {aktif?.id === "sevk" && <Sevk depoNo={depoKilidi} />}
        {aktif?.id === "siparis" && <Siparis depoNo={depoKilidi} />}
        {aktif?.id === "fiyat" && <Fiyat />}
        {aktif?.id === "etiket" && <Etiket />}
        {aktif?.id === "yonetim" && <Yonetim />}
      </main>
    </div>
  );
}
