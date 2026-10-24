import { Fragment, useState } from "react";
import { csvIndir, para, qs, tarih } from "../api";
import { Alan, Durum, Kart, useVeri } from "../ortak";

type Cek = { guid: string; tip: number; tipAdi: string; refNo: string; seriNo: string | null; borclu: string | null; banka: string | null; vade: string; tutar: number; odenen: number; pozisyon: number; pozisyonAdi: string; sahipKod: string | null; sahipAdi: string | null; neredeKod: string | null; neredeAdi: string | null; vadeyeKalanGun: number };
type Sonuc = { ozet: { adet: number; toplam: number; vadesi30Gun: number; vadesiGecmis: number }; cekler: Cek[] };
type Hareket = { tarih: string; evrakAdi: string; evrakNo: string | null; cariKod: string | null; cariAdi: string | null; tip: number; tutar: number; pozisyonAdi: string };

function Hareketler({ refno }: { refno: string }) {
  const { veri, hata, yukleniyor } = useVeri<Hareket[]>(`/api/cek/${encodeURIComponent(refno)}/hareketler`);
  return (
    <tr className="alt-satir"><td colSpan={9}>
      <Durum hata={hata} yukleniyor={yukleniyor} />
      {veri && (
        <table className="ic">
          <thead><tr><th>Tarih</th><th>İşlem</th><th>Evrak no</th><th>Cari</th><th>Pozisyon</th><th className="s">Tutar</th></tr></thead>
          <tbody>{veri.length === 0 && <tr><td colSpan={6} className="bos">Bu çek için cari hareket kaydı yok (devir ile gelmiş olabilir).</td></tr>}{veri.map((h, i) => <tr key={i}><td>{tarih(h.tarih)}</td><td>{h.evrakAdi}</td><td>{h.evrakNo}</td><td>{h.cariAdi ?? h.cariKod}</td><td>{h.pozisyonAdi}</td><td className="s">{para(h.tutar)}</td></tr>)}</tbody>
        </table>
      )}
    </td></tr>
  );
}

export default function CekSayfa() {
  const [f, setF] = useState({ tipler: "0,2", durum: "portfoy", vadeBas: "", vadeBit: "", ara: "" });
  const [sorgu, setSorgu] = useState(f);
  const [acik, setAcik] = useState<string | null>(null);
  const { veri, hata, yukleniyor } = useVeri<Sonuc>(`/api/cek?${qs(sorgu)}`);

  return (
    <section>
      <header className="sayfa-baslik">
        <h1>Çek raporu</h1>
        <form className="filtre" onSubmit={(e) => { e.preventDefault(); setSorgu(f); }}>
          <Alan etiket="Tür">
            <select value={f.tipler} onChange={(e) => setF({ ...f, tipler: e.target.value })}>
              <option value="0,2">Müşteri + firma çekleri</option><option value="0">Müşteri çekleri</option><option value="2">Firma (kendi) çeklerimiz</option>
            </select>
          </Alan>
          <Alan etiket="Durum">
            <select value={f.durum} onChange={(e) => setF({ ...f, durum: e.target.value })}>
              <option value="portfoy">Portföyde</option><option value="cikan">Ciro edilen / verilen</option><option value="tahsil">Tahsil / ödendi</option><option value="hepsi">Hepsi</option>
            </select>
          </Alan>
          <Alan etiket="Vade başı"><input type="date" value={f.vadeBas} onChange={(e) => setF({ ...f, vadeBas: e.target.value })} /></Alan>
          <Alan etiket="Vade sonu"><input type="date" value={f.vadeBit} onChange={(e) => setF({ ...f, vadeBit: e.target.value })} /></Alan>
          <Alan etiket="Ara (no, borçlu)"><input value={f.ara} onChange={(e) => setF({ ...f, ara: e.target.value })} /></Alan>
          <button type="submit">Getir</button>
        </form>
      </header>
      <Durum hata={hata} yukleniyor={yukleniyor} />
      {veri && (
        <>
          <div className="kartlar">
            <Kart baslik="Adet" deger={veri.ozet.adet} />
            <Kart baslik="Toplam" deger={`${para(veri.ozet.toplam)} ₺`} />
            <Kart baslik="30 gün içinde vadeli" deger={`${para(veri.ozet.vadesi30Gun)} ₺`} />
            <Kart baslik="Vadesi geçmiş (portföyde)" deger={`${para(veri.ozet.vadesiGecmis)} ₺`} />
          </div>
          <div className="arac">
            <button className="ikincil" onClick={() => csvIndir("cek_raporu.csv",
              ["Tür", "Referans", "Seri no", "Borçlu", "Vade", "Tutar", "Durum", "Kimden", "Nerede"],
              veri.cekler.map((c) => [c.tipAdi, c.refNo, c.seriNo, c.borclu, tarih(c.vade), c.tutar, c.pozisyonAdi, c.sahipAdi ?? c.sahipKod, c.neredeAdi ?? c.neredeKod]))}>
              Excel'e aktar
            </button>
          </div>
          <table>
            <thead><tr><th>Tür</th><th>Seri no</th><th>Borçlu</th><th>Vade</th><th className="s">Gün</th><th className="s">Tutar</th><th>Durum</th><th>Kimden</th><th>Nerede</th></tr></thead>
            <tbody>
              {veri.cekler.map((c) => (
                <Fragment key={c.guid}>
                  <tr className="tiklanir" onClick={() => setAcik(acik === c.refNo ? null : c.refNo)}>
                    <td>{c.tipAdi}</td><td>{c.seriNo || c.refNo}</td><td>{c.borclu}</td><td>{tarih(c.vade)}</td>
                    <td className={"s" + (c.vadeyeKalanGun < 0 ? " eksi" : "")}>{c.vadeyeKalanGun}</td>
                    <td className="s b">{para(c.tutar)}</td><td>{c.pozisyonAdi}</td><td>{c.sahipAdi ?? c.sahipKod}</td><td>{c.neredeAdi ?? c.neredeKod}</td>
                  </tr>
                  {acik === c.refNo && <Hareketler refno={c.refNo} />}
                </Fragment>
              ))}
            </tbody>
          </table>
        </>
      )}
    </section>
  );
}
