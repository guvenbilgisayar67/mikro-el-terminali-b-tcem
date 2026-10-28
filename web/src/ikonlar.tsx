/** Menü ve kart simgeleri (çizgi simge, 24×24). */
const YOLLAR = {
  ciro: '<path d="M3 17l6-6 4 4 8-8"/><path d="M15 7h6v6"/>',
  rapor: '<path d="M4 20V10M10 20V4M16 20v-7M22 20H2"/>',
  kar: '<circle cx="12" cy="12" r="8"/><path d="M12 4v8l6 3"/>',
  cari: '<circle cx="9" cy="8" r="3.5"/><path d="M2.5 20c.8-3.6 3.4-5.5 6.5-5.5s5.7 1.9 6.5 5.5"/><path d="M16 4.5a3.5 3.5 0 010 7M18 14.8c1.8.8 3 2.6 3.5 5.2"/>',
  cek: '<rect x="3" y="6" width="18" height="12" rx="2"/><path d="M7 14h5"/>',
  fatura: '<path d="M6 3h9l4 4v14H6z"/><path d="M14 3v5h5M9 13h7M9 17h5"/>',
  sevk: '<path d="M3 7h11v9H3zM14 10h4l3 3v3h-7"/><circle cx="7" cy="18" r="1.6"/><circle cx="17" cy="18" r="1.6"/>',
  siparis: '<path d="M6 7h12l-1 13H7z"/><path d="M9 7a3 3 0 016 0"/>',
  sayim: '<path d="M9 4h6v3H9z"/><path d="M6 6h12v15H6z"/><path d="M9 12l2 2 4-4"/>',
  fiyat: '<path d="M20 12l-8 8-8-8V4h8z"/><circle cx="8" cy="8" r="1.5"/>',
  etiket: '<path d="M4 7V4h16v3M4 17v3h16v-3"/><path d="M7 10v4M10 10v4M13 10v4M17 10v4"/>',
  tasarim: '<path d="M4 20l4-1 11-11-3-3L5 16z"/>',
  yonetim: '<circle cx="12" cy="12" r="3"/><path d="M12 2v3M12 19v3M2 12h3M19 12h3M5 5l2 2M17 17l2 2M5 19l2-2M17 7l2-2"/>',
  nakit: '<rect x="2.5" y="6" width="19" height="12" rx="2"/><circle cx="12" cy="12" r="2.5"/><path d="M6 9v.01M18 15v.01"/>',
  kart: '<rect x="2.5" y="5" width="19" height="14" rx="2"/><path d="M2.5 10h19M6 15h4"/>',
  iade: '<path d="M9 14L4 9l5-5"/><path d="M4 9h11a5 5 0 010 10h-3"/>',
  cikis: '<path d="M15 4h4v16h-4"/><path d="M10 8l-4 4 4 4M6 12h10"/>',
  menu: '<path d="M4 6h16M4 12h16M4 18h16"/>',
} as const;

export type IkonAdi = keyof typeof YOLLAR;

export function Ikon({ ad, boyut = 18 }: { ad: IkonAdi; boyut?: number }) {
  return (
    <svg className="ikon" viewBox="0 0 24 24" width={boyut} height={boyut} aria-hidden="true"
         dangerouslySetInnerHTML={{ __html: YOLLAR[ad] }} />
  );
}
