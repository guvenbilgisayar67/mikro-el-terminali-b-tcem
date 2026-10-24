import { useState } from "react";
import { bugun, para, qs, saat } from "../api";
import { Alan, Durum, Kart, useVeri } from "../ortak";

type Kasa = { kaynak: string; magaza: string; kasaKodu: string; kasaAdi: string; fisSayisi: number; zToplam: number; nakit: number; krediKarti: number; iade: number; sonGuncelleme: string | null };
type Magaza = { magaza: string; zToplam: number; nakit: number; krediKarti: number; iade: number; sonGuncelleme: string | null; kasalar: Kasa[] };
type Sonuc = { tarih: string; toplam: number; magazalar: Magaza[]; ulasilamayanKaynaklar: string[] };

export default function Ciro() {
  const [gun, setGun] = useState(bugun());
  const { veri, hata, yukleniyor, yenile } = useVeri<Sonuc>(`/api/ciro?${qs({ tarih: gun })}`);
  const toplam = (f: (m: Magaza) => number) => veri?.magazalar.reduce((s, m) => s + f(m), 0) ?? 0;

  return (
    <section>
      <header className="sayfa-baslik">
        <h1>Günün cirosu</h1>
        <div className="filtre">
          <Alan etiket="Tarih"><input type="date" value={gun} onChange={(e) => setGun(e.target.value)} /></Alan>
          <button onClick={yenile}>Yenile</button>
        </div>
      </header>
      <p className="not">Kaynak: Orion kasa takip (Z raporları). Şubelerin verisi merkeze gecikmeli gelebilir; mağaza bazında son güncelleme saati gösterilir.</p>
      <Durum hata={hata} yukleniyor={yukleniyor} />
      {veri && (
        <>
          <div className="kartlar">
            <Kart baslik="Toplam ciro" deger={`${para(veri.toplam)} ₺`} />
            <Kart baslik="Nakit" deger={`${para(toplam((m) => m.nakit))} ₺`} />
            <Kart baslik="Kredi kartı" deger={`${para(toplam((m) => m.krediKarti))} ₺`} />
            <Kart baslik="İade" deger={`${para(toplam((m) => m.iade))} ₺`} />
          </div>
          {veri.ulasilamayanKaynaklar.map((u) => <div key={u} className="uyari">Orion kaynağına ulaşılamadı: {u}</div>)}
          {veri.magazalar.length === 0 && <div className="bilgi">Bu tarih için kayıt yok.</div>}
          {veri.magazalar.map((m) => (
            <div className="blok" key={m.magaza}>
              <h2>{m.magaza} <span className="sag">{para(m.zToplam)} ₺</span></h2>
              <table>
                <thead><tr><th>Kasa</th><th className="s">Fiş</th><th className="s">Z toplam</th><th className="s">Nakit</th><th className="s">Kredi kartı</th><th className="s">İade</th><th>Son güncelleme</th></tr></thead>
                <tbody>
                  {m.kasalar.map((k) => (
                    <tr key={k.kaynak + k.kasaKodu}>
                      <td>{k.kasaAdi}</td><td className="s">{k.fisSayisi}</td><td className="s">{para(k.zToplam)}</td>
                      <td className="s">{para(k.nakit)}</td><td className="s">{para(k.krediKarti)}</td><td className="s">{para(k.iade)}</td>
                      <td>{saat(k.sonGuncelleme)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ))}
        </>
      )}
    </section>
  );
}
