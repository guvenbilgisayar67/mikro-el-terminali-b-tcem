import { useState } from "react";
import { api, saat } from "../api";
import { Alan, Durum, useVeri } from "../ortak";

type Kullanici = { id: number; kullaniciAdi: string; adSoyad: string; rol: string; depoNo: number | null; aktif: boolean; sonGiris: string | null; yetkiler: string[] };
type Yetki = { kod: string; ad: string };
type Ajan = { id: number; ad: string; depoNo: number; aktif: boolean; sonGorulme: string | null };
type Yazici = { id: number; ad: string; tur: string; dil: string; depoNo: number; ajanAdi: string };
type Depo = { no: number; ad: string };

const ROLLER: Record<string, string> = { admin: "Yönetici (her şey)", merkez: "Merkez personeli (tüm depolar)", magaza: "Mağaza personeli (tek depo)" };
const BOS = { id: 0, kullaniciAdi: "", adSoyad: "", rol: "merkez", depoNo: 1 as number | null, aktif: true, sifre: "", yetkiler: [] as string[] };

export default function Yonetim() {
  const kullanicilar = useVeri<Kullanici[]>("/api/kullanicilar");
  const ajanlar = useVeri<Ajan[]>("/api/yonetim/ajanlar");
  const yazicilar = useVeri<Yazici[]>("/api/etiket/yazicilar");
  const depolar = useVeri<Depo[]>("/api/depolar");
  const yetkiler = useVeri<Yetki[]>("/api/kullanicilar/yetkiler");
  const yetkiAdi = (kod: string) => yetkiler.veri?.find((y) => y.kod === kod)?.ad ?? kod;
  const yetkiDegis = (kod: string, var_: boolean) =>
    setK({ ...k, yetkiler: var_ ? [...k.yetkiler, kod] : k.yetkiler.filter((x) => x !== kod) });
  const [k, setK] = useState(BOS);
  const [ajan, setAjan] = useState({ ad: "", depoNo: 1 });
  const [yeniAnahtar, setYeniAnahtar] = useState<{ ad: string; anahtar: string } | null>(null);
  const [y, setY] = useState({ ajanId: 0, ad: "", windowsYaziciAdi: "", tur: "etiket", dil: "TSPL" });
  const [mesaj, setMesaj] = useState<string | null>(null);
  const depoAdi = (n: number | null) => depolar.veri?.find((d) => d.no === n)?.ad ?? (n ?? "");

  async function yap(f: () => Promise<unknown>, sonra: () => void) {
    setMesaj(null);
    try { await f(); sonra(); } catch (e) { setMesaj((e as Error).message); }
  }

  const depoSecim = (deger: number | null, degis: (n: number) => void) => (
    <select value={deger ?? ""} onChange={(e) => degis(+e.target.value)}>
      {depolar.veri?.map((d) => <option key={d.no} value={d.no}>{d.no} · {d.ad}</option>)}
    </select>
  );

  return (
    <section>
      <header className="sayfa-baslik"><h1>Yönetim</h1></header>
      {mesaj && <div className="uyari hata">{mesaj}</div>}

      <div className="blok">
        <h2>Kullanıcılar</h2>
        <p className="not">Bu kullanıcılar Mikro kullanıcısı değildir; panel ve el terminali girişi içindir. Yönetici her şeyi yapar. Diğer kullanıcılar yalnız işaretlenen ekranları görür; mağaza personeli ayrıca yalnız kendi deposuyla çalışır. Değişiklik kullanıcıya en geç 30 saniyede yansır, pasife alınan kullanıcının oturumu hemen kapanır.</p>
        <form className="filtre" onSubmit={(e) => {
          e.preventDefault();
          const govde = { ...k, depoNo: k.rol === "magaza" ? k.depoNo : null, sifre: k.sifre || null,
                          yetkiler: k.rol === "magaza" ? k.yetkiler.filter((y) => y !== "ciro") : k.yetkiler };
          yap(() => (k.id ? api.put(`/api/kullanicilar/${k.id}`, govde) : api.post("/api/kullanicilar", govde)), () => { setK(BOS); kullanicilar.yenile(); });
        }}>
          <Alan etiket="Kullanıcı adı"><input required value={k.kullaniciAdi} onChange={(e) => setK({ ...k, kullaniciAdi: e.target.value })} /></Alan>
          <Alan etiket="Ad soyad"><input required value={k.adSoyad} onChange={(e) => setK({ ...k, adSoyad: e.target.value })} /></Alan>
          <Alan etiket="Kullanıcı tipi">
            <select value={k.rol} onChange={(e) => setK({ ...k, rol: e.target.value })}>{Object.entries(ROLLER).map(([r, a]) => <option key={r} value={r}>{a}</option>)}</select>
          </Alan>
          {k.rol === "magaza" && <Alan etiket="Mağaza">{depoSecim(k.depoNo, (n) => setK({ ...k, depoNo: n }))}</Alan>}
          {k.rol !== "admin" && (
            <fieldset className="yetkiler">
              <legend>Yetkiler</legend>
              {yetkiler.veri?.map((y) => {
                const yasak = k.rol === "magaza" && y.kod === "ciro";
                return (
                  <label key={y.kod} className="kutu" title={yasak ? "Günün cirosu tüm mağazaları gösterir" : undefined}>
                    <input type="checkbox" disabled={yasak} checked={!yasak && k.yetkiler.includes(y.kod)} onChange={(e) => yetkiDegis(y.kod, e.target.checked)} /> {y.ad}
                  </label>
                );
              })}
            </fieldset>
          )}
          <Alan etiket={k.id ? "Yeni şifre (boş: değişmez)" : "Şifre (en az 8)"}><input type="password" value={k.sifre} minLength={k.id ? 0 : 8} onChange={(e) => setK({ ...k, sifre: e.target.value })} /></Alan>
          <label className="kutu"><input type="checkbox" checked={k.aktif} onChange={(e) => setK({ ...k, aktif: e.target.checked })} /> Aktif</label>
          <button type="submit">{k.id ? "Kaydet" : "Ekle"}</button>
          {k.id !== 0 && <button type="button" className="ikincil" onClick={() => setK(BOS)}>Vazgeç</button>}
        </form>
        <Durum hata={kullanicilar.hata} yukleniyor={kullanicilar.yukleniyor} />
        <table>
          <thead><tr><th>Kullanıcı</th><th>Ad soyad</th><th>Tip</th><th>Mağaza</th><th>Yetkiler</th><th>Durum</th><th>Son giriş</th><th></th></tr></thead>
          <tbody>
            {kullanicilar.veri?.map((u) => (
              <tr key={u.id}>
                <td>{u.kullaniciAdi}</td><td>{u.adSoyad}</td><td>{ROLLER[u.rol] ?? u.rol}</td><td>{u.depoNo ? depoAdi(u.depoNo) : ""}</td>
                <td className="yetki-ozet">{u.rol === "admin" ? "Hepsi" : u.yetkiler.map(yetkiAdi).join(", ")}</td>
                <td>{u.aktif ? "Aktif" : "Pasif"}</td><td>{saat(u.sonGiris)}</td>
                <td><button className="ikincil" onClick={() => setK({ ...u, sifre: "" })}>Düzenle</button></td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <div className="blok">
        <h2>Mağaza bilgisayarları (yazdırma programı)</h2>
        <p className="not">Her mağazada yazıcıların USB ile bağlı olduğu bilgisayara ButcemYazdirma kurulur. Buradan eklenince verilen anahtar o bilgisayarın ayar dosyasına yazılır; anahtar bir daha gösterilmez.</p>
        <form className="filtre" onSubmit={(e) => {
          e.preventDefault();
          yap(async () => { const r = await api.post<{ ad: string; anahtar: string }>("/api/yonetim/ajanlar", ajan); setYeniAnahtar(r); },
              () => { setAjan({ ad: "", depoNo: 1 }); ajanlar.yenile(); });
        }}>
          <Alan etiket="Bilgisayar adı (ör. SOLAK-KASA1)"><input required value={ajan.ad} onChange={(e) => setAjan({ ...ajan, ad: e.target.value })} /></Alan>
          <Alan etiket="Mağaza">{depoSecim(ajan.depoNo, (n) => setAjan({ ...ajan, depoNo: n }))}</Alan>
          <button type="submit">Ekle</button>
        </form>
        {yeniAnahtar && <div className="uyari"><b>{yeniAnahtar.ad}</b> anahtarı: <code>{yeniAnahtar.anahtar}</code> (kopyalayın, bir daha gösterilmez)</div>}
        <table>
          <thead><tr><th>Bilgisayar</th><th>Mağaza</th><th>Son görülme</th></tr></thead>
          <tbody>{ajanlar.veri?.map((a) => <tr key={a.id}><td>{a.ad}</td><td>{depoAdi(a.depoNo)}</td><td>{saat(a.sonGorulme) || "Hiç bağlanmadı"}</td></tr>)}</tbody>
        </table>
      </div>

      <div className="blok">
        <h2>Yazıcılar</h2>
        <p className="not">Windows yazıcı adı, mağaza bilgisayarında <code>ButcemYazdirma.exe yazicilar</code> komutunun yazdığı adla birebir aynı olmalı. Xprinter: TSPL, Argox: EPL, Epson/Brother: A4.</p>
        <form className="filtre" onSubmit={(e) => {
          e.preventDefault();
          yap(() => api.post("/api/yonetim/yazicilar", { ...y, ajanId: y.ajanId || ajanlar.veri?.[0]?.id }), () => { setY({ ...y, ad: "", windowsYaziciAdi: "" }); yazicilar.yenile(); });
        }}>
          <Alan etiket="Bilgisayar">
            <select value={y.ajanId || ajanlar.veri?.[0]?.id || ""} onChange={(e) => setY({ ...y, ajanId: +e.target.value })}>
              {ajanlar.veri?.map((a) => <option key={a.id} value={a.id}>{a.ad}</option>)}
            </select>
          </Alan>
          <Alan etiket="Görünen ad"><input required value={y.ad} onChange={(e) => setY({ ...y, ad: e.target.value })} placeholder="Raf yazıcısı" /></Alan>
          <Alan etiket="Windows yazıcı adı"><input required value={y.windowsYaziciAdi} onChange={(e) => setY({ ...y, windowsYaziciAdi: e.target.value })} /></Alan>
          <Alan etiket="Model">
            <select value={y.dil} onChange={(e) => setY({ ...y, dil: e.target.value, tur: e.target.value === "PDF" ? "a4" : "etiket" })}>
              <option value="TSPL">Xprinter XP-470B (TSPL)</option><option value="EPL">Argox OS-214 plus (EPL)</option><option value="PDF">A4 (Epson / Brother)</option>
            </select>
          </Alan>
          <button type="submit" disabled={!ajanlar.veri?.length}>Ekle</button>
        </form>
        <table>
          <thead><tr><th>Ad</th><th>Bilgisayar</th><th>Mağaza</th><th>Tür</th></tr></thead>
          <tbody>{yazicilar.veri?.map((p) => <tr key={p.id}><td>{p.ad}</td><td>{p.ajanAdi}</td><td>{depoAdi(p.depoNo)}</td><td>{p.dil === "PDF" ? "A4" : p.dil}</td></tr>)}</tbody>
        </table>
      </div>
    </section>
  );
}
