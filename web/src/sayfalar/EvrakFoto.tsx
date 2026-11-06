import { useEffect, useState } from "react";
import { api, bugun, gunEkle, qs, saat } from "../api";
import { Alan, Cipler, Durum, useVeri } from "../ortak";

type Foto = { yol: string; zaman: string; kullanici: string; boyut: number };
type Evrak = { tur: string; seri: string; sira: number; son: string; fotograflar: Foto[] };

const TURLER: [string, string][] = [["", "Hepsi"], ["alis_faturasi", "Alış faturası"], ["alis_irsaliyesi", "Alış irsaliyesi"], ["satis_faturasi", "Satış faturası"], ["sevk", "Depolar arası sevk"]];
const turAdi = (t: string) => TURLER.find((x) => x[0] === t)?.[1] ?? t;

/** Terminalden evraklara eklenen fotoğraflar. Mikro'ya gitmez; sunucudaki fotoğraf klasöründen okunur. */
export default function EvrakFoto() {
  const [tur, setTur] = useState("");
  const [bas, setBas] = useState(gunEkle(bugun(), -7));
  const [bit, setBit] = useState(bugun());
  const [sira, setSira] = useState("");
  const [buyuk, setBuyuk] = useState<string | null>(null);
  const liste = useVeri<Evrak[]>(`/api/fotograf?${qs({ tur: tur || null, bas, bit, sira: sira.trim() || null })}`);

  return (
    <section>
      <h1>Evrak fotoğrafları</h1>
      <p className="not">Terminalde alış / satış faturası, alış irsaliyesi ve depolar arası sevk kaydedilirken çekilen fotoğraflar. Fotoğraflar Mikro'ya aktarılmaz ve veritabanında yer tutmaz; sunucudaki fotoğraf klasöründe durur.</p>
      <div className="filtre">
        <Cipler secenekler={TURLER} deger={tur} degisti={setTur} />
      </div>
      <div className="filtre">
        <Alan etiket="Başlangıç"><input type="date" value={bas} max={bit} onChange={(e) => setBas(e.target.value)} /></Alan>
        <Alan etiket="Bitiş"><input type="date" value={bit} max={bugun()} onChange={(e) => setBit(e.target.value)} /></Alan>
        <Alan etiket="Evrak sıra no"><input className="kisa" inputMode="numeric" value={sira} onChange={(e) => setSira(e.target.value)} /></Alan>
      </div>
      <Durum hata={liste.hata} yukleniyor={liste.yukleniyor} />
      {liste.veri?.length === 0 && <p className="bilgi">Bu aralıkta fotoğraflı evrak yok.</p>}
      <div className="foto-liste">
        {liste.veri?.map((e) => (
          <div key={`${e.tur}-${e.seri}-${e.sira}`} className="foto-evrak">
            <div><b>{turAdi(e.tur)}</b> · {e.seri} {e.sira}<small className="not"> · {e.fotograflar.length} fotoğraf · son {saat(e.son)}</small></div>
            <div className="foto-satir">
              {e.fotograflar.map((f) => (
                <button key={f.yol} className="foto-kucuk" title={`${f.kullanici} · ${saat(f.zaman)}`} onClick={() => setBuyuk(f.yol)}>
                  <Resim yol={f.yol} />
                  <small>{f.kullanici} · {saat(f.zaman)}</small>
                </button>
              ))}
            </div>
          </div>
        ))}
      </div>
      {buyuk && (
        <div className="foto-perde" onClick={() => setBuyuk(null)}>
          <Resim yol={buyuk} />
          <span>Kapatmak için dokunun</span>
        </div>
      )}
    </section>
  );
}

/** Resim yetkili istekle (token başlığı) blob olarak alınır. */
function Resim({ yol }: { yol: string }) {
  const [url, setUrl] = useState<string | null>(null);
  useEffect(() => {
    let u: string | null = null;
    let iptal = false;
    api.get<Blob>(`/api/fotograf/dosya?${qs({ yol })}`).then((b) => {
      if (iptal) return;
      u = URL.createObjectURL(b);
      setUrl(u);
    }).catch(() => undefined);
    return () => { iptal = true; if (u) URL.revokeObjectURL(u); };
  }, [yol]);
  return url ? <img src={url} alt="" /> : <span className="foto-yukleniyor">…</span>;
}
