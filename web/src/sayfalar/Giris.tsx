import { useState } from "react";
import { api, Oturum } from "../api";

export default function Giris({ girdi }: { girdi: (o: Oturum) => void }) {
  const [kullaniciAdi, setKullaniciAdi] = useState("");
  const [sifre, setSifre] = useState("");
  const [hata, setHata] = useState<string | null>(null);
  const [bekle, setBekle] = useState(false);

  return (
    <main className="giris">
      <form onSubmit={async (e) => {
        e.preventDefault();
        setBekle(true);
        setHata(null);
        try { girdi(await api.post<Oturum>("/api/giris", { kullaniciAdi, sifre })); }
        catch (err) { setHata((err as Error).message); }
        finally { setBekle(false); }
      }}>
        <h1>Bütçem</h1>
        <p className="not">Mağaza ve rapor paneli</p>
        <label className="alan"><span>Kullanıcı adı</span><input autoFocus required value={kullaniciAdi} onChange={(e) => setKullaniciAdi(e.target.value)} /></label>
        <label className="alan"><span>Şifre</span><input type="password" required value={sifre} onChange={(e) => setSifre(e.target.value)} /></label>
        {hata && <div className="uyari hata">{hata}</div>}
        <button type="submit" disabled={bekle}>{bekle ? "Giriş yapılıyor…" : "Giriş"}</button>
      </form>
    </main>
  );
}
