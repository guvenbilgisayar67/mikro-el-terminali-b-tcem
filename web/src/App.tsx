import { useEffect, useState } from "react";
import { api, Oturum, oturumDustugunde, oturumOku, oturumYaz, yetkili } from "./api";
import { Ikon, IkonAdi } from "./ikonlar";
import Cari from "./sayfalar/Cari";
import Cek from "./sayfalar/Cek";
import CekIslem from "./sayfalar/CekIslem";
import Ciro from "./sayfalar/Ciro";
import Etiket from "./sayfalar/Etiket";
import EtiketTasarim from "./sayfalar/EtiketTasarim";
import Fatura from "./sayfalar/Fatura";
import Fiyat from "./sayfalar/Fiyat";
import Karlilik from "./sayfalar/Karlilik";
import Giris from "./sayfalar/Giris";
import Satis from "./sayfalar/Satis";
import Sayim from "./sayfalar/Sayim";
import Sevk from "./sayfalar/Sevk";
import Siparis from "./sayfalar/Siparis";
import Yonetim from "./sayfalar/Yonetim";

type Sayfa = "ciro" | "satis" | "karlilik" | "cari" | "cek" | "cekislem" | "fatura" | "sevk" | "siparis" | "sayim" | "fiyat" | "etiket" | "etikettasarim" | "yonetim";
// Her sayfa, listelenen yetkilerden birine sahip kullanıcıya görünür (admin hepsini görür). Menüde gruba göre sıralanır.
const SAYFALAR: { id: Sayfa; ad: string; yetki: string[]; grup: string; ikon: IkonAdi }[] = [
  { id: "ciro", ad: "Günün cirosu", yetki: ["ciro"], grup: "Raporlar", ikon: "ciro" },
  { id: "satis", ad: "Satış raporu", yetki: ["satis_raporu"], grup: "Raporlar", ikon: "rapor" },
  { id: "karlilik", ad: "Karlılık analizi", yetki: ["karlilik"], grup: "Raporlar", ikon: "kar" },
  { id: "cari", ad: "Cari ekstre", yetki: ["cari_ekstre"], grup: "Raporlar", ikon: "cari" },
  { id: "cek", ad: "Çek raporu", yetki: ["cek_raporu"], grup: "Raporlar", ikon: "cek" },
  { id: "cekislem", ad: "Çek giriş / çıkış", yetki: ["cek_giris", "cek_cikis"], grup: "Evrak", ikon: "cek" },
  { id: "fatura", ad: "Fatura ve irsaliye", yetki: ["alis_fatura", "satis_fatura"], grup: "Evrak", ikon: "fatura" },
  { id: "sevk", ad: "Depolar arası sevk", yetki: ["sevk"], grup: "Evrak", ikon: "sevk" },
  { id: "siparis", ad: "Sipariş", yetki: ["siparis"], grup: "Evrak", ikon: "siparis" },
  { id: "sayim", ad: "Sayım", yetki: ["sayim", "sayim_onay"], grup: "Evrak", ikon: "sayim" },
  { id: "fiyat", ad: "Fiyat değişikliği", yetki: ["fiyat_degisikligi"], grup: "Stok ve etiket", ikon: "fiyat" },
  { id: "etiket", ad: "Etiket bas", yetki: ["etiket"], grup: "Stok ve etiket", ikon: "etiket" },
  { id: "etikettasarim", ad: "Etiket tasarımı", yetki: ["etiket_tasarim"], grup: "Stok ve etiket", ikon: "tasarim" },
  { id: "yonetim", ad: "Yönetim", yetki: [], grup: "", ikon: "yonetim" },
];

const ROL_ADI: Record<Oturum["rol"], string> = { admin: "Yönetici", merkez: "Merkez", magaza: "Mağaza" };
const basHarfler = (ad: string) => ad.split(/\s+/).filter(Boolean).slice(0, 2).map((p) => p[0]).join("").toLocaleUpperCase("tr-TR");

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
  const [menuAcik, setMenuAcik] = useState(false);
  useEffect(() => { location.hash = sayfa; setMenuAcik(false); }, [sayfa]);
  // Ana sayfadaki kısayollar (ör. "Karlılık analizi →") başka sayfaya geçmek için adresi değiştirir.
  useEffect(() => {
    const f = () => setSayfa((location.hash.slice(1) as Sayfa) || "ciro");
    window.addEventListener("hashchange", f);
    return () => window.removeEventListener("hashchange", f);
  }, []);

  if (!oturum) return <Giris girdi={(o) => { oturumYaz(o); setOturum(o); }} />;
  const aktif = izinli.find((s) => s.id === sayfa) ?? izinli[0];
  const depoKilidi = oturum.rol === "magaza" ? oturum.depoNo : null;

  return (
    <div className="kabuk">
      <nav className={menuAcik ? "acik" : ""}>
        <div className="marka">
          <span className="logo">B</span>Bütçem
          <button className="menu-dugme" aria-label="Menü" onClick={() => setMenuAcik(!menuAcik)}><Ikon ad="menu" /></button>
        </div>
        <div className="menu">
          {izinli.map((s, i) => [
            s.grup !== izinli[i - 1]?.grup && <div key={"g" + s.grup} className={"grup" + (s.grup ? "" : " bos")}>{s.grup}</div>,
            <button key={s.id} className={s.id === aktif?.id ? "aktif" : ""} onClick={() => setSayfa(s.id)}>
              <Ikon ad={s.ikon} />{s.ad}
            </button>,
          ])}
        </div>
        <div className="kullanici">
          <span className="avatar">{basHarfler(oturum.adSoyad)}</span>
          <span className="kim"><b>{oturum.adSoyad}</b><small>{ROL_ADI[oturum.rol] ?? oturum.rol}</small></span>
          <button className="cikis" title="Çıkış" aria-label="Çıkış" onClick={() => { oturumYaz(null); setOturum(null); }}><Ikon ad="cikis" /></button>
        </div>
      </nav>
      <main style={{ ["--grup" as string]: aktif?.grup ? `"${aktif.grup}"` : `""` }}>
        {!aktif && <p className="not">Size henüz bir ekran yetkisi verilmemiş. Yöneticinize başvurun.</p>}
        {aktif?.id === "ciro" && <Ciro oturum={oturum} />}
        {aktif?.id === "satis" && <Satis depoKilitli={depoKilidi != null} />}
        {aktif?.id === "karlilik" && <Karlilik depoKilitli={depoKilidi != null} />}
        {aktif?.id === "cari" && <Cari />}
        {aktif?.id === "cek" && <Cek />}
        {aktif?.id === "cekislem" && <CekIslem oturum={oturum} />}
        {aktif?.id === "fatura" && <Fatura depoNo={depoKilidi} oturum={oturum} />}
        {aktif?.id === "sevk" && <Sevk depoNo={depoKilidi} />}
        {aktif?.id === "siparis" && <Siparis depoNo={depoKilidi} />}
        {aktif?.id === "sayim" && <Sayim oturum={oturum} depoNo={depoKilidi} />}
        {aktif?.id === "fiyat" && <Fiyat />}
        {aktif?.id === "etiket" && <Etiket />}
        {aktif?.id === "etikettasarim" && <EtiketTasarim />}
        {aktif?.id === "yonetim" && <Yonetim />}
      </main>
    </div>
  );
}
