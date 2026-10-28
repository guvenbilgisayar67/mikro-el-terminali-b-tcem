import { useEffect, useState } from "react";
import { ayBasi, bugun, gunEkle, Oturum, para, paraTam, qs, saat, yetkili } from "../api";
import { Cipler, Degisim, Durum, Kart, oranSinifi, useVeri, yuzde } from "../ortak";

type Kasa = { kaynak: string; magaza: string; kasaKodu: string; kasaAdi: string; fisSayisi: number; zToplam: number; nakit: number; krediKarti: number; iade: number; sonGuncelleme: string | null };
type Magaza = { magaza: string; zToplam: number; nakit: number; krediKarti: number; iade: number; sonGuncelleme: string | null; kasalar: Kasa[] };
type Sonuc = { tarih: string; toplam: number; magazalar: Magaza[]; ulasilamayanKaynaklar: string[] };
type Gunler = { gunler: { tarih: string; toplam: number }[]; ulasilamayanKaynaklar: string[] };
type KarSatiri = { anahtar: string; ad: string; netSatis: number; kar: number; karOrani: number | null };
type Kar = { netSatis: number; kar: number; karOrani: number | null; satirlar: KarSatiri[] };
type Bekleyen = { tur: string; baslik: string; metin: string; ek: string; sayfa: string };

type Aralik = "14" | "30" | "12ay";
const AYLAR = ["Oca", "Şub", "Mar", "Nis", "May", "Haz", "Tem", "Ağu", "Eyl", "Eki", "Kas", "Ara"];
const PILL: Record<string, string> = { sayim: "turuncu", irsaliye: "mavi", cek: "kirmizi", etiket: "yesil" };
const yuvarla = (n: number) => {
  // Eksen üst sınırı: 1, 2, 2,5 ya da 5'in katı.
  if (n <= 0) return 1;
  const us = 10 ** Math.floor(Math.log10(n));
  return ([1, 2, 2.5, 5, 10].find((k) => k * us >= n) ?? 10) * us;
};
const kisa = (n: number) => (n >= 1e6 ? `${(n / 1e6).toLocaleString("tr-TR", { maximumFractionDigits: 2 })} mn` : `${paraTam(n / 1000)} bin`);

/** Gün ya da ay ciro çubuk grafiği; en yüksek ve son çubuğun üstünde değer yazar. */
function CiroGrafigi({ veri }: { veri: { etiket: string; ipucu: string; toplam: number }[] }) {
  const W = 760, H = 250, sol = 56, alt = 28, ust = 22;
  const ustSinir = yuvarla(Math.max(...veri.map((v) => v.toplam), 0));
  const adim = (W - sol) / Math.max(veri.length, 1);
  const bw = Math.min(36, adim * 0.68);
  const enYuksek = veri.reduce((m, v, i) => (v.toplam > veri[m].toplam ? i : m), 0);
  const etiketAraligi = veri.length > 16 ? Math.ceil(veri.length / 12) : 1;
  return (
    <svg className="grafik" viewBox={`0 0 ${W} ${H}`} role="img" aria-label="Ciro grafiği">
      {[0, 1, 2, 3, 4].map((i) => {
        const y = ust + (i * (H - ust - alt)) / 4;
        return (
          <g key={i}>
            <line className="izgara-cizgi" x1={sol} x2={W} y1={y} y2={y} />
            <text x={sol - 8} y={y + 4} textAnchor="end">{kisa(ustSinir - (i * ustSinir) / 4)}</text>
          </g>
        );
      })}
      {veri.map((v, i) => {
        const h = (v.toplam / ustSinir) * (H - ust - alt);
        const x = sol + i * adim + (adim - bw) / 2;
        const y = H - alt - h;
        const son = i === veri.length - 1;
        return (
          <g key={v.etiket + i}>
            <rect className={"gun" + (son ? " son" : "")} x={x} y={y} width={bw} height={Math.max(h, 0)} rx={Math.min(5, bw / 4)}>
              <title>{`${v.ipucu}: ${paraTam(v.toplam)} ₺`}</title>
            </rect>
            {(i % etiketAraligi === 0 || son) && <text x={x + bw / 2} y={H - 10} textAnchor="middle">{v.etiket}</text>}
            {(son || i === enYuksek) && v.toplam > 0 && <text className="deger" x={x + bw / 2} y={y - 6} textAnchor="middle">{kisa(v.toplam)}</text>}
          </g>
        );
      })}
    </svg>
  );
}

export default function Ciro({ oturum }: { oturum: Oturum }) {
  const [gun, setGun] = useState(bugun());
  const [aralik, setAralik] = useState<Aralik>("14");
  const { veri, hata, yukleniyor, yenile } = useVeri<Sonuc>(`/api/ciro?${qs({ tarih: gun })}`);
  const [yenilenme, setYenilenme] = useState(new Date());
  const karYetkisi = yetkili(oturum, "karlilik");

  // Bugüne bakılıyorsa 5 dakikada bir kendiliğinden yenilenir.
  useEffect(() => {
    if (gun !== bugun()) return;
    const t = setInterval(() => { yenile(); setYenilenme(new Date()); }, 5 * 60 * 1000);
    return () => clearInterval(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [gun]);

  const gecmisBas = aralik === "12ay" ? gunEkle(gun.slice(0, 8) + "01", -334) : gunEkle(gun, aralik === "14" ? -13 : -29);
  const gecmis = useVeri<Gunler>(`/api/ciro/gunler?${qs({ bas: gecmisBas, bit: gun })}`);
  const haftaOnce = useVeri<Gunler>(`/api/ciro/gunler?${qs({ bas: gunEkle(gun, -7), bit: gunEkle(gun, -7) })}`);
  const ayBas = gun.slice(0, 8) + "01";
  const kar = useVeri<Kar>(karYetkisi ? `/api/karlilik?${qs({ bas: gun === bugun() ? ayBasi() : ayBas, bit: gun, kirilim: "Anagrup" })}` : null);
  const bekleyenler = useVeri<Bekleyen[]>("/api/ana-sayfa/bekleyenler");

  const toplam = (f: (m: Magaza) => number) => veri?.magazalar.reduce((s, m) => s + f(m), 0) ?? 0;
  const oncekiHafta = haftaOnce.veri?.gunler[0]?.toplam;
  const enCok = Math.max(...(veri?.magazalar.map((m) => m.zToplam) ?? [0]), 1);

  const grafikVerisi = (() => {
    const g = gecmis.veri?.gunler ?? [];
    if (aralik !== "12ay")
      return g.map((x) => {
        const d = new Date(x.tarih + "T00:00:00");
        return { etiket: `${String(d.getDate()).padStart(2, "0")} ${AYLAR[d.getMonth()]}`, ipucu: d.toLocaleDateString("tr-TR", { weekday: "long", day: "numeric", month: "long" }), toplam: x.toplam };
      });
    const aylar = new Map<string, number>();
    g.forEach((x) => aylar.set(x.tarih.slice(0, 7), (aylar.get(x.tarih.slice(0, 7)) ?? 0) + x.toplam));
    return [...aylar].map(([a, t]) => ({ etiket: `${AYLAR[+a.slice(5, 7) - 1]} ${a.slice(2, 4)}`, ipucu: `${AYLAR[+a.slice(5, 7) - 1]} ${a.slice(0, 4)}`, toplam: t }));
  })();

  return (
    <section>
      <header className="sayfa-baslik">
        <h1>Günün cirosu</h1>
        <span className={"canli" + (gun === bugun() ? "" : " eski")}>
          {gun === bugun() ? `Canlı · ${yenilenme.toLocaleTimeString("tr-TR", { hour: "2-digit", minute: "2-digit" })}'de yenilendi` : "Geçmiş gün"}
        </span>
        <div className="filtre sag">
          <input type="date" value={gun} aria-label="Tarih" onChange={(e) => setGun(e.target.value)} />
          <button onClick={() => { yenile(); gecmis.yenile(); bekleyenler.yenile(); setYenilenme(new Date()); }}>Yenile</button>
        </div>
      </header>
      <Durum hata={hata} yukleniyor={yukleniyor && !veri} />
      {veri && (
        <>
          <div className="kartlar">
            <Kart ikon="ciro" baslik="Ciro (KDV dahil)" deger={`${paraTam(veri.toplam)} ₺`}
                  alt={<Degisim simdi={veri.toplam} once={oncekiHafta} ek="geçen haftaya göre" />} />
            <Kart ikon="nakit" renk="mavi" baslik="Nakit" deger={`${paraTam(toplam((m) => m.nakit))} ₺`}
                  alt={veri.toplam ? `cironun ${yuzde((toplam((m) => m.nakit) / veri.toplam) * 100)}` : undefined} />
            <Kart ikon="kart" renk="turuncu" baslik="Kredi kartı" deger={`${paraTam(toplam((m) => m.krediKarti))} ₺`}
                  alt={veri.toplam ? `cironun ${yuzde((toplam((m) => m.krediKarti) / veri.toplam) * 100)}` : undefined} />
            {karYetkisi
              ? <Kart ikon="kar" renk="mor" baslik="Bu ay brüt kâr oranı" deger={kar.veri ? yuzde(kar.veri.karOrani) : "…"}
                      alt={kar.veri ? `${paraTam(kar.veri.kar)} ₺ kâr · KDV hariç` : "hesaplanıyor"} />
              : <Kart ikon="iade" renk="kirmizi" baslik="İade" deger={`${paraTam(toplam((m) => m.iade))} ₺`} />}
          </div>
          {[...new Set([...veri.ulasilamayanKaynaklar, ...(gecmis.veri?.ulasilamayanKaynaklar ?? [])])].map((u) =>
            <div key={u} className="uyari">Orion kaynağına ulaşılamadı: {u}</div>)}

          <div className="izgara ana">
            <div className="blok">
              <div className="blok-baslik">
                <h3>{aralik === "12ay" ? "Son 12 ay ciro" : `Son ${aralik} gün ciro`} <small>· tüm mağazalar</small></h3>
                <Cipler<Aralik> secenekler={[["14", "14 gün"], ["30", "30 gün"], ["12ay", "12 ay"]]} deger={aralik} degisti={setAralik} />
              </div>
              {gecmis.veri ? <CiroGrafigi veri={grafikVerisi} /> : <Durum hata={gecmis.hata} yukleniyor={true} />}
            </div>
            <div className="blok">
              <div className="blok-baslik"><h3>Mağazalar <small>· {gun === bugun() ? "bugün" : new Date(gun + "T00:00:00").toLocaleDateString("tr-TR")}</small></h3></div>
              {veri.magazalar.length === 0 && <div className="bilgi">Bu tarih için kasa kaydı yok.</div>}
              <div className="magazalar">
                {veri.magazalar.map((m) => {
                  // Bugün, son 3 saatte verisi gelmemiş mağaza "gecikmeli" (geçmiş günlerde Z raporu ertesi gün de gelebilir).
                  const gecikmeli = gun === bugun() && m.sonGuncelleme && Date.now() - new Date(m.sonGuncelleme).getTime() > 3 * 3600e3;
                  return (
                    <div className="magaza-satir" key={m.magaza}>
                      <div className="ust"><b>{m.magaza || "(mağaza adı yok)"}</b><b>{paraTam(m.zToplam)} ₺</b></div>
                      <span className="cubuk"><i style={{ width: `${(m.zToplam / enCok) * 100}%` }} /></span>
                      <span className="not" style={{ margin: 0 }}>
                        {m.kasalar.length} kasa · son veri {saat(m.sonGuncelleme)}{gecikmeli && <> <span className="pill turuncu">gecikmeli</span></>}
                      </span>
                    </div>
                  );
                })}
              </div>
            </div>
          </div>

          <div className={karYetkisi ? "izgara alt" : "izgara"}>
            {karYetkisi && (
              <div className="blok">
                <div className="blok-baslik">
                  <h3>En çok kâr getiren gruplar <small>· bu ay</small></h3>
                  <button className="ikincil" onClick={() => (location.hash = "karlilik")}>Karlılık analizi →</button>
                </div>
                {kar.veri ? (
                  <table className="sade">
                    <thead><tr><th>Grup</th><th className="s">Net satış</th><th className="s">Kâr</th><th style={{ width: "34%" }}>Kâr oranı</th></tr></thead>
                    <tbody>
                      {kar.veri.satirlar.slice().sort((a, b) => b.kar - a.kar).slice(0, 5).map((r) => (
                        <tr key={r.anahtar}>
                          <td className="b">{r.ad}</td><td className="s">{paraTam(r.netSatis)} ₺</td><td className="s b">{paraTam(r.kar)} ₺</td>
                          <td><div className="oranli"><span className="cubuk"><i className={oranSinifi(r.karOrani)} style={{ width: `${Math.max(0, Math.min(100, (r.karOrani ?? 0) * 2))}%` }} /></span><span>{yuzde(r.karOrani)}</span></div></td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                ) : <Durum hata={kar.hata} yukleniyor={true} />}
              </div>
            )}
            <div className="blok">
              <div className="blok-baslik"><h3>Bekleyen işler</h3></div>
              {bekleyenler.veri?.length === 0 && <div className="bilgi">Bekleyen iş yok.</div>}
              {bekleyenler.veri && bekleyenler.veri.length > 0 && (
                <table className="sade">
                  <tbody>
                    {bekleyenler.veri.map((b) => (
                      <tr key={b.tur} className="tiklanir" onClick={() => (location.hash = b.sayfa)}>
                        <td style={{ width: 80 }}><span className={"pill " + (PILL[b.tur] ?? "")}>{b.baslik}</span></td>
                        <td style={{ whiteSpace: "normal" }}>{b.metin}</td><td className="ek">{b.ek}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
              <Durum hata={bekleyenler.hata} yukleniyor={bekleyenler.yukleniyor && !bekleyenler.veri} />
            </div>
          </div>

          {veri.magazalar.length > 0 && <h2>Kasa ayrıntısı <small>Orion kasa takip (Z raporları); şubelerin verisi merkeze gecikmeli gelebilir.</small></h2>}
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
          {karYetkisi && kar.veri && <p className="not">Kâr oranı Mikro satış faturalarından, satış gününün son alış fiyatıyla hesaplanır (ayrıntısı Karlılık analizinde).</p>}
        </>
      )}
    </section>
  );
}
