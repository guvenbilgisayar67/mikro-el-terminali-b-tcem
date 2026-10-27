import { useEffect, useRef, useState } from "react";
import { api, bugun, miktar, Oturum, para, saat, sayiOku, tarih, yetkili } from "../api";
import { Alan, DepoSec, Durum, Mesaj, MesajKutusu, useVeri, YazmaDurumu } from "../ortak";

type Ozet = {
  id: number; depoNo: number; tarih: string; aciklama: string; durum: number; satirSayisi: number; miktar: number; duzeltilenSayisi: number;
  olusturan: string; olusturmaZamani: string; gonderimZamani: string | null; onayZamani: string | null; girisFisi: string | null; cikisFisi: string | null;
};
type Satir = {
  stokKodu: string; barkod: string; urunAdi: string; birim: string; terminalMiktar: number; miktar: number; duzeltildi: boolean;
  sonOkutan: string; sonOkutmaZamani: string; sistemStok: number | null; fark: number | null; birimMaliyet: number | null;
};
type Detay = { sayim: Ozet; satirlar: Satir[] };

const DURUM = ["Sayılıyor", "Onay bekliyor", "Mikro'ya işlendi", "İptal", "İşleniyor"];

export default function Sayim({ oturum, depoNo }: { oturum: Oturum; depoNo: number | null }) {
  const [depo, setDepo] = useState<number | "">(depoNo ?? "");
  const [secili, setSecili] = useState<number | null>(null);
  const [gun, setGun] = useState(bugun());
  const [aciklama, setAciklama] = useState("");
  const [mesaj, setMesaj] = useState<Mesaj>(null);
  const liste = useVeri<Ozet[]>(`/api/sayim${depo ? `?depo=${depo}` : ""}`);

  async function yeni() {
    if (!depo) return;
    try {
      const s = await api.post<Ozet>("/api/sayim", { depo, tarih: gun, aciklama });
      setSecili(s.id);
      setAciklama("");
      liste.yenile();
    } catch (err) {
      setMesaj({ tur: "hata", metin: (err as Error).message });
    }
  }

  if (secili) return <SayimDetay id={secili} oturum={oturum} geri={() => { setSecili(null); liste.yenile(); }} />;

  return (
    <section>
      <header className="sayfa-baslik"><h1>Sayım</h1></header>
      <p className="not">
        Mağaza terminalden ya da bu ekrandan barkod okutup adet girer. Sayım bitince merkeze onaya düşer; merkez yanlış girilenleri düzeltip onaylayınca
        Mikro'ya sayım sonucu ve fark fişleri (SYMG giriş, SYMC çıkış) işlenir, sayılan ürünlerin stoğu sayılan miktara eşitlenir. Sayılmayan ürünlere dokunulmaz.
      </p>
      <div className="filtre">
        <DepoSec etiket="Mağaza / depo" deger={depo} degisti={setDepo} kilitli={depoNo != null} />
        <Alan etiket="Sayım tarihi"><input type="date" value={gun} max={bugun()} onChange={(e) => setGun(e.target.value)} /></Alan>
        <Alan etiket="Açıklama (isteğe bağlı)"><input value={aciklama} onChange={(e) => setAciklama(e.target.value)} placeholder="örn. Deterjan reyonu" /></Alan>
        <button disabled={!depo} onClick={yeni}>Yeni sayım başlat</button>
      </div>
      <MesajKutusu mesaj={mesaj} />
      <Durum hata={liste.hata} yukleniyor={liste.yukleniyor} />
      <table>
        <thead><tr><th>No</th><th>Depo</th><th>Tarih</th><th>Açıklama</th><th>Durum</th><th className="s">Ürün</th><th className="s">Düzeltilen</th><th>Başlatan</th><th>Mikro</th></tr></thead>
        <tbody>
          {liste.veri?.length === 0 && <tr><td colSpan={9} className="bos">Sayım yok.</td></tr>}
          {liste.veri?.map((s) => (
            <tr key={s.id} className="tiklanir" onClick={() => setSecili(s.id)}>
              <td>{s.id}</td><td>{s.depoNo}</td><td>{tarih(s.tarih)}</td><td>{s.aciklama}</td>
              <td className={s.durum === 1 ? "b" : ""}>{DURUM[s.durum]}</td>
              <td className="s">{s.satirSayisi}</td><td className="s">{s.duzeltilenSayisi || ""}</td><td>{s.olusturan}</td>
              <td>{[s.girisFisi, s.cikisFisi].filter(Boolean).join(", ")}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}

function SayimDetay({ id, oturum, geri }: { id: number; oturum: Oturum; geri: () => void }) {
  const d = useVeri<Detay>(`/api/sayim/${id}`);
  const onayci = yetkili(oturum, "sayim_onay");
  const [barkod, setBarkod] = useState("");
  const [adet, setAdet] = useState("1");
  const [mesaj, setMesaj] = useState<Mesaj>(null);
  const [bekle, setBekle] = useState(false);
  const [farkli, setFarkli] = useState(false);
  const [ara, setAra] = useState("");
  const barkodRef = useRef<HTMLInputElement>(null);
  const adetRef = useRef<HTMLInputElement>(null);
  const s = d.veri?.sayim;
  const acik = s?.durum === 0;
  const duzeltilebilir = onayci && (s?.durum === 0 || s?.durum === 1);

  useEffect(() => { barkodRef.current?.focus(); }, [acik]);

  // Barkod okutulunca adet alanına geçilir; Enter ile kaydedilir.
  async function ekle(e: React.FormEvent) {
    e.preventDefault();
    const b = barkod.trim(), m = sayiOku(adet);
    if (!b) return;
    if (!m) { adetRef.current?.focus(); return; }
    try {
      if (acik) {
        const r = await api.post<{ kod: string; ad: string; birim: string; miktar: number }>(`/api/sayim/${id}/okut`, { urun: b, miktar: m });
        setMesaj({ tur: "bilgi", metin: `${r.ad}: +${miktar(m)} → toplam ${miktar(r.miktar)} ${r.birim}` });
      } else {
        await api.put(`/api/sayim/${id}/satir`, { urun: b, miktar: m });
        setMesaj({ tur: "bilgi", metin: `${b} sayıma eklendi: ${miktar(m)}` });
      }
      setBarkod("");
      setAdet("1");
      d.yenile();
    } catch (err) {
      setMesaj({ tur: "hata", metin: `${b}: ${(err as Error).message}` });
      setBarkod("");
    }
    barkodRef.current?.focus();
  }

  async function duzelt(x: Satir, deger: string) {
    const m = deger.trim() === "" ? null : sayiOku(deger);
    if (m !== null && (isNaN(m) || m < 0)) { setMesaj({ tur: "hata", metin: "Geçerli bir miktar girin." }); return; }
    if (m === x.miktar) return;
    try {
      await api.put(`/api/sayim/${id}/satir`, { urun: x.stokKodu, miktar: m });
      d.yenile();
    } catch (err) {
      setMesaj({ tur: "hata", metin: (err as Error).message });
    }
  }

  async function islem(yol: string, soru: string, sonra?: (r: unknown) => void) {
    if (!confirm(soru)) return;
    setBekle(true);
    try {
      const r = await api.post<unknown>(`/api/sayim/${id}/${yol}`, {});
      sonra?.(r);
      d.yenile();
    } catch (err) {
      setMesaj({ tur: "hata", metin: (err as Error).message });
    }
    setBekle(false);
  }

  if (!s) return <section><button className="ikincil" onClick={geri}>← Sayımlar</button><Durum hata={d.hata} yukleniyor={d.yukleniyor} /></section>;

  const a_ = ara.trim().toLocaleUpperCase("tr-TR");
  const satirlar = (d.veri?.satirlar ?? [])
    .filter((x) => !farkli || (x.fark ?? 0) !== 0 || x.duzeltildi)
    .filter((x) => !a_ || `${x.urunAdi} ${x.barkod} ${x.stokKodu}`.toLocaleUpperCase("tr-TR").includes(a_));
  const tum = d.veri?.satirlar ?? [];
  const giris = tum.reduce((t, x) => t + ((x.fark ?? 0) > 0 ? x.fark! * (x.birimMaliyet ?? 0) : 0), 0);
  const cikis = tum.reduce((t, x) => t + ((x.fark ?? 0) < 0 ? -x.fark! * (x.birimMaliyet ?? 0) : 0), 0);

  return (
    <section>
      <header className="sayfa-baslik">
        <h1>Sayım {s.id} · Depo {s.depoNo} · {tarih(s.tarih)}</h1>
        <button className="ikincil" onClick={geri}>← Sayımlar</button>
      </header>
      <YazmaDurumu />
      <p className="not">
        Durum: <b>{DURUM[s.durum]}</b>{s.aciklama ? ` · ${s.aciklama}` : ""} · başlatan {s.olusturan}, {saat(s.olusturmaZamani)}
        {s.gonderimZamani ? ` · onaya gönderildi ${saat(s.gonderimZamani)}` : ""}
        {s.onayZamani && s.durum === 2 ? ` · Mikro'ya işlendi ${saat(s.onayZamani)}: ${[s.girisFisi, s.cikisFisi].filter(Boolean).join(", ") || "fark yok"}` : ""}
      </p>
      {(acik || duzeltilebilir) && (
        <form className="filtre" onSubmit={ekle}>
          <Alan etiket={acik ? "Barkod okut" : "Ürün ekle (barkod)"}>
            <input ref={barkodRef} value={barkod} onChange={(e) => setBarkod(e.target.value)}
              onKeyDown={(e) => { if (e.key === "Enter" && barkod.trim()) { e.preventDefault(); adetRef.current?.focus(); adetRef.current?.select(); } }} />
          </Alan>
          <Alan etiket={acik ? "Adet (eklenir)" : "Sayılan"}><input ref={adetRef} className="adet" inputMode="decimal" value={adet} onChange={(e) => setAdet(e.target.value)} /></Alan>
          <button type="submit">Ekle</button>
        </form>
      )}
      <MesajKutusu mesaj={mesaj} />
      <div className="filtre">
        <Alan etiket="Ürün ara"><input value={ara} onChange={(e) => setAra(e.target.value)} /></Alan>
        {onayci && <label className="kutu"><input type="checkbox" checked={farkli} onChange={(e) => setFarkli(e.target.checked)} /> Yalnız farklı ve düzeltilenler</label>}
      </div>
      <Durum hata={d.hata} yukleniyor={d.yukleniyor} />
      <table>
        <thead>
          <tr>
            <th>Ürün</th><th>Barkod</th><th className="s">Okutulan</th><th className="s">Sayılan</th>
            {onayci && <><th className="s">Mikro stoğu</th><th className="s">Fark</th><th className="s">Fark tutarı</th></>}
            <th>Son okutan</th>
          </tr>
        </thead>
        <tbody>
          {satirlar.length === 0 && <tr><td colSpan={8} className="bos">Henüz okutulan ürün yok.</td></tr>}
          {satirlar.map((x) => (
            <tr key={x.stokKodu} className={x.duzeltildi ? "secili" : ""}>
              <td>{x.urunAdi}</td><td>{x.barkod}</td>
              <td className="s">{miktar(x.terminalMiktar)}</td>
              <td className="s">
                {duzeltilebilir
                  ? <input key={`${x.stokKodu}-${x.miktar}`} className="adet" inputMode="decimal" defaultValue={String(x.miktar).replace(".", ",")} title="Boş bırakılırsa ürün sayımdan çıkar"
                      onBlur={(e) => duzelt(x, e.target.value)} onKeyDown={(e) => e.key === "Enter" && (e.target as HTMLInputElement).blur()} />
                  : miktar(x.miktar)} {x.birim}
              </td>
              {onayci && <>
                <td className={"s" + ((x.sistemStok ?? 0) < 0 ? " eksi" : "")}>{miktar(x.sistemStok)}</td>
                <td className={"s b" + ((x.fark ?? 0) < 0 ? " eksi" : "")}>{x.fark ? (x.fark > 0 ? "+" : "") + miktar(x.fark) : "0"}</td>
                <td className="s">{x.fark && x.birimMaliyet ? para(x.fark * x.birimMaliyet) : ""}</td>
              </>}
              <td>{x.sonOkutan}<small className="not"> {saat(x.sonOkutmaZamani)}</small></td>
            </tr>
          ))}
        </tbody>
      </table>
      {onayci && tum.length > 0 && (
        <div className="toplamlar">
          <span>{tum.length} ürün, {tum.filter((x) => (x.fark ?? 0) !== 0).length} farklı</span>
          <span>Fazla (giriş): <b>{para(giris)}</b></span>
          <span>Eksik (çıkış): <b className="eksi">{para(cikis)}</b></span>
          <span>Net: <b>{para(giris - cikis)}</b> TL (son alış fiyatıyla)</span>
        </div>
      )}
      {onayci && s.durum !== 2 && <p className="not">Mikro stoğu, sayım tarihinin gün sonu itibarıyla hesaplanır. O günün satışları Mikro'ya aktarılmadan onaylanırsa fark yanlış çıkar.</p>}
      <div className="kaydet">
        {acik && <button disabled={bekle || tum.length === 0} onClick={() => islem("gonder", "Sayım bitirilip merkeze onaya gönderilsin mi? Sonra okutma yapılamaz.")}>Sayımı bitir, onaya gönder</button>}
        {onayci && s.durum === 1 && <button className="ikincil" disabled={bekle} onClick={() => islem("geri-ac", "Sayım mağazaya geri açılsın mı? Okutmaya devam edebilirler.")}>Mağazaya geri aç</button>}
        {onayci && (s.durum === 0 || s.durum === 1) && <button className="ikincil" disabled={bekle} onClick={() => islem("iptal", "Sayım iptal edilsin mi? Mikro'ya hiçbir şey yazılmaz.")}>İptal et</button>}
        {onayci && (s.durum === 0 || s.durum === 1) && (
          <button disabled={bekle || tum.length === 0} onClick={() => islem("onay",
            `${tum.length} ürünün stoğu sayılan miktara eşitlenecek (Mikro'ya sayım sonucu ve fark fişleri yazılır). Onaylıyor musunuz?`,
            (r) => { const x = r as { girisFisi: string | null; cikisFisi: string | null; farkliSatir: number }; setMesaj({ tur: "bilgi", metin: `Mikro'ya işlendi: ${[x.girisFisi, x.cikisFisi].filter(Boolean).join(", ") || "fark yok"} (${x.farkliSatir} üründe fark).` }); })}>
            {bekle ? "İşleniyor…" : "Onayla, Mikro'ya işle"}
          </button>
        )}
      </div>
    </section>
  );
}

