export type Oturum = { token: string; adSoyad: string; rol: "admin" | "merkez" | "magaza"; depoNo: number | null; yetkiler: string[] };

export const yetkili = (o: Oturum | null, ...y: string[]) => !!o && (o.rol === "admin" || y.some((x) => o.yetkiler.includes(x)));

const ANAHTAR = "butcem.oturum";

export function oturumOku(): Oturum | null {
  try {
    const s = localStorage.getItem(ANAHTAR);
    const o = s ? (JSON.parse(s) as Oturum) : null;
    return o && Array.isArray(o.yetkiler) ? o : null;   // eski sürümün oturumu: yeniden giriş
  } catch {
    return null;
  }
}

export function oturumYaz(o: Oturum | null) {
  try {
    if (o) localStorage.setItem(ANAHTAR, JSON.stringify(o));
    else localStorage.removeItem(ANAHTAR);
  } catch {
    /* gizli pencere vb. */
  }
}

export class ApiHatasi extends Error {
  constructor(public durum: number, mesaj: string) {
    super(mesaj);
  }
}

let cikisYap: () => void = () => {};
export const oturumDustugunde = (f: () => void) => (cikisYap = f);

async function istek<T>(yontem: string, yol: string, govde?: unknown): Promise<T> {
  const o = oturumOku();
  const r = await fetch(yol, {
    method: yontem,
    headers: {
      ...(govde !== undefined ? { "Content-Type": "application/json" } : {}),
      ...(o ? { Authorization: `Bearer ${o.token}` } : {}),
    },
    body: govde !== undefined ? JSON.stringify(govde) : undefined,
  });
  if (r.status === 401) {
    cikisYap();
    throw new ApiHatasi(401, "Oturum süresi doldu, tekrar giriş yapın.");
  }
  if (r.status === 403) throw new ApiHatasi(403, "Bu işlem için yetkiniz yok.");
  if (!r.ok) {
    const metin = await r.text();
    let mesaj = metin || `Hata ${r.status}`;
    try {
      const j = JSON.parse(metin);
      mesaj = typeof j === "string" ? j : j.hata ?? j.title ?? mesaj;
    } catch {
      /* düz metin */
    }
    throw new ApiHatasi(r.status, mesaj);
  }
  if (r.status === 204) return undefined as T;
  const tur = r.headers.get("content-type") ?? "";
  return (tur.includes("json") ? await r.json() : await r.blob()) as T;
}

export const api = {
  get: <T>(yol: string) => istek<T>("GET", yol),
  post: <T>(yol: string, govde: unknown) => istek<T>("POST", yol, govde),
  put: <T>(yol: string, govde: unknown) => istek<T>("PUT", yol, govde),
};

export const qs = (p: Record<string, string | number | null | undefined>) =>
  Object.entries(p)
    .filter(([, v]) => v !== null && v !== undefined && v !== "")
    .map(([k, v]) => `${k}=${encodeURIComponent(String(v))}`)
    .join("&");

const tl = new Intl.NumberFormat("tr-TR", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const sayi = new Intl.NumberFormat("tr-TR", { maximumFractionDigits: 3 });
export const para = (n: number | null | undefined) => (n == null ? "" : tl.format(n));
export const miktar = (n: number | null | undefined) => (n == null ? "" : sayi.format(n));
export const tarih = (s: string | null | undefined) =>
  s ? new Date(s.length === 10 ? s + "T00:00:00" : s).toLocaleDateString("tr-TR") : "";
export const saat = (s: string | null | undefined) =>
  s ? new Date(s).toLocaleString("tr-TR", { dateStyle: "short", timeStyle: "short" }) : "";

export const bugun = () => isoGun(new Date());
export const isoGun = (d: Date) =>
  `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
export const ayBasi = () => {
  const d = new Date();
  return isoGun(new Date(d.getFullYear(), d.getMonth(), 1));
};

/** Tabloyu Excel'in açabildiği CSV olarak indirir (noktalı virgül, UTF-8 BOM). */
export function csvIndir(dosya: string, basliklar: string[], satirlar: (string | number | null | undefined)[][]) {
  const hucre = (v: string | number | null | undefined) => {
    if (v == null) return "";
    const s = typeof v === "number" ? v.toLocaleString("tr-TR", { useGrouping: false, maximumFractionDigits: 3 }) : v;
    return /[;"\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
  };
  const metin = [basliklar, ...satirlar].map((r) => r.map(hucre).join(";")).join("\r\n");
  const url = URL.createObjectURL(new Blob(["﻿" + metin], { type: "text/csv;charset=utf-8" }));
  const a = document.createElement("a");
  a.href = url;
  a.download = dosya;
  a.click();
  URL.revokeObjectURL(url);
}

/** Kullanıcının yazdığı tutarı sayıya çevirir: "325.000,50", "325000,5" ve "325000.5" hepsi kabul. */
export function sayiOku(s: string): number {
  const t = s.trim().replace(/\s/g, "");
  if (!t) return NaN;
  return t.includes(",") ? Number(t.replace(/\./g, "").replace(",", ".")) : Number(t);
}
