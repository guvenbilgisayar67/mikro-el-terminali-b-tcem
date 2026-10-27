package com.butcem.terminal;

import android.content.ContentValues;
import android.content.Context;
import android.database.Cursor;
import android.database.sqlite.SQLiteDatabase;
import android.database.sqlite.SQLiteOpenHelper;
import android.database.sqlite.SQLiteStatement;

import org.json.JSONArray;

import java.util.ArrayList;
import java.util.List;
import java.util.UUID;

/**
 * Cihazdaki veritabanı: çevrimdışı çalışmak için ürün listesi (fiyat ve indirildiği andaki depo stoğuyla),
 * henüz merkeze gitmemiş sayım okutmaları ve bekleyen işlemler (sevk, etiket basımı).
 * Gönderilmemiş kayıtlar silinmez; uygulama kapansa, oturum düşse de cihazda kalır.
 */
final class Yerel extends SQLiteOpenHelper {
    static final class Okutma {
        String id, barkod, kod, ad, birim, hata;
        int sayimId, depo;
        double miktar;
        long zaman;
        boolean gonderildi;
    }

    static final class Urun {
        final String barkod, kod, ad, birim, fd;
        final double fiyat;
        final Double stok;     // listenin indirildiği andaki depo stoğu; bilinmiyorsa null
        Urun(String barkod, String kod, String ad, String birim, double fiyat, String fd, Double stok) {
            this.barkod = barkod; this.kod = kod; this.ad = ad; this.birim = birim; this.fiyat = fiyat; this.fd = fd; this.stok = stok;
        }
    }

    /** Bağlantı gelince gönderilecek işlem (sevk fişi, etiket basımı). */
    static final class Bekleyen {
        String id, tur, yol, govde, ozet, hata, sonuc;
        long zaman;
        int durum;      // 0 bekliyor, 1 gönderildi, 2 merkez kabul etmedi
    }

    static final int BEKLIYOR = 0, GONDERILDI = 1, REDDEDILDI = 2;

    private static Yerel tek;

    static synchronized Yerel al(Context c) {
        if (tek == null) tek = new Yerel(c.getApplicationContext());
        return tek;
    }

    private Yerel(Context c) { super(c, "butcem.db", null, 2); }

    @Override public void onCreate(SQLiteDatabase db) {
        db.execSQL("CREATE TABLE urunler (barkod TEXT PRIMARY KEY, kod TEXT NOT NULL, ad TEXT NOT NULL, birim TEXT NOT NULL)");
        db.execSQL("CREATE TABLE okutmalar (id TEXT PRIMARY KEY, sayim_id INTEGER NOT NULL, depo INTEGER NOT NULL, barkod TEXT NOT NULL, "
                + "kod TEXT NOT NULL, ad TEXT NOT NULL, birim TEXT NOT NULL, miktar REAL NOT NULL, zaman INTEGER NOT NULL, "
                + "gonderildi INTEGER NOT NULL DEFAULT 0, hata TEXT)");
        onUpgrade(db, 1, 2);
    }

    /** 1 → 2 (0.5.0): ürün listesine fiyat, fiyat değişim tarihi, stok; bekleyen işlemler tablosu. */
    @Override public void onUpgrade(SQLiteDatabase db, int eski, int yeni) {
        if (eski < 2) {
            db.execSQL("ALTER TABLE urunler ADD COLUMN fiyat REAL NOT NULL DEFAULT 0");
            db.execSQL("ALTER TABLE urunler ADD COLUMN fd TEXT NOT NULL DEFAULT ''");
            db.execSQL("ALTER TABLE urunler ADD COLUMN stok REAL");
            db.execSQL("CREATE INDEX ix_urunler_kod ON urunler (kod)");
            db.execSQL("CREATE TABLE bekleyenler (id TEXT PRIMARY KEY, tur TEXT NOT NULL, yol TEXT NOT NULL, govde TEXT NOT NULL, "
                    + "ozet TEXT NOT NULL, zaman INTEGER NOT NULL, durum INTEGER NOT NULL DEFAULT 0, hata TEXT, sonuc TEXT)");
        }
    }

    /** Sunucudan gelen [barkod, kod, ad, birim, fiyat, fd, stok] listesiyle ürünleri tamamen yeniler. */
    int urunleriYaz(JSONArray a) {
        SQLiteDatabase db = getWritableDatabase();
        db.beginTransaction();
        try {
            db.delete("urunler", null, null);
            SQLiteStatement st = db.compileStatement("INSERT OR REPLACE INTO urunler (barkod, kod, ad, birim, fiyat, fd, stok) VALUES (?, ?, ?, ?, ?, ?, ?)");
            for (int i = 0; i < a.length(); i++) {
                JSONArray u = a.optJSONArray(i);
                if (u == null) continue;
                st.clearBindings();
                st.bindString(1, u.optString(0));
                st.bindString(2, u.optString(1));
                st.bindString(3, u.optString(2));
                st.bindString(4, u.optString(3));
                st.bindDouble(5, u.optDouble(4, 0));
                st.bindString(6, u.isNull(5) ? "" : u.optString(5));
                if (u.length() > 6 && !u.isNull(6)) st.bindDouble(7, u.optDouble(6, 0)); else st.bindNull(7);
                st.executeInsert();
            }
            db.setTransactionSuccessful();
        } finally {
            db.endTransaction();
        }
        return urunSayisi();
    }

    int urunSayisi() {
        Cursor c = getReadableDatabase().rawQuery("SELECT COUNT(*) FROM urunler", null);
        try { return c.moveToFirst() ? c.getInt(0) : 0; } finally { c.close(); }
    }

    private static final String URUN_SUTUN = "SELECT barkod, kod, ad, birim, fiyat, fd, stok FROM urunler ";

    private static Urun urunOku(Cursor c) {
        return new Urun(c.getString(0), c.getString(1), c.getString(2), c.getString(3), c.getDouble(4), c.getString(5),
                c.isNull(6) ? null : c.getDouble(6));
    }

    /** Barkod ya da stok kodu ile; barkodu tam tutan kayıt önce. */
    Urun urunBul(String barkod) {
        Cursor c = getReadableDatabase().rawQuery(URUN_SUTUN + "WHERE barkod = ? OR kod = ? ORDER BY barkod = ? DESC LIMIT 1",
                new String[]{barkod, barkod, barkod});
        try { return c.moveToFirst() ? urunOku(c) : null; }
        finally { c.close(); }
    }

    /** Fiyat değişim tarihi verilen gün (yyyy-MM-dd) olan ürünler, ürün başına bir barkod. */
    List<Urun> fiyatiDegisenler(String gun) {
        List<Urun> l = new ArrayList<Urun>();
        Cursor c = getReadableDatabase().rawQuery(URUN_SUTUN + "WHERE fd = ? GROUP BY kod ORDER BY ad", new String[]{gun});
        try { while (c.moveToNext()) l.add(urunOku(c)); } finally { c.close(); }
        return l;
    }

    // ---- bekleyen işlemler ----

    Bekleyen bekleyenEkle(String tur, String yol, String govde, String ozet) {
        Bekleyen b = new Bekleyen();
        b.id = UUID.randomUUID().toString();
        b.tur = tur; b.yol = yol; b.govde = govde; b.ozet = ozet; b.zaman = System.currentTimeMillis();
        ContentValues v = new ContentValues();
        v.put("id", b.id); v.put("tur", tur); v.put("yol", yol); v.put("govde", govde); v.put("ozet", ozet); v.put("zaman", b.zaman);
        getWritableDatabase().insertOrThrow("bekleyenler", null, v);
        return b;
    }

    /** Sadece bekleyenler (eskiden yeniye) ya da hepsi (yeniden eskiye, en fazla 50). */
    List<Bekleyen> bekleyenler(boolean hepsi) {
        List<Bekleyen> l = new ArrayList<Bekleyen>();
        Cursor c = getReadableDatabase().rawQuery("SELECT id, tur, yol, govde, ozet, zaman, durum, hata, sonuc FROM bekleyenler "
                + (hepsi ? "ORDER BY zaman DESC LIMIT 50" : "WHERE durum = 0 ORDER BY zaman"), null);
        try {
            while (c.moveToNext()) {
                Bekleyen b = new Bekleyen();
                b.id = c.getString(0); b.tur = c.getString(1); b.yol = c.getString(2); b.govde = c.getString(3); b.ozet = c.getString(4);
                b.zaman = c.getLong(5); b.durum = c.getInt(6); b.hata = c.isNull(7) ? null : c.getString(7); b.sonuc = c.isNull(8) ? null : c.getString(8);
                l.add(b);
            }
        } finally { c.close(); }
        return l;
    }

    int bekleyenSayisi() {
        Cursor c = getReadableDatabase().rawQuery("SELECT COUNT(*) FROM bekleyenler WHERE durum = 0", null);
        try { return c.moveToFirst() ? c.getInt(0) : 0; } finally { c.close(); }
    }

    void bekleyenSonuc(String id, int durum, String metin) {
        ContentValues v = new ContentValues();
        v.put("durum", durum);
        if (durum == GONDERILDI) { v.put("sonuc", metin); v.putNull("hata"); } else if (durum == REDDEDILDI) v.put("hata", metin);
        getWritableDatabase().update("bekleyenler", v, "id = ?", new String[]{id});
    }

    void bekleyenSil(String id) { getWritableDatabase().delete("bekleyenler", "id = ?", new String[]{id}); }

    /** Gönderilmiş ve reddedilmiş kayıtları siler; bekleyenlere dokunmaz. */
    void bekleyenleriTemizle(long oncesi) {
        getWritableDatabase().delete("bekleyenler", "durum <> 0 AND zaman < ?", new String[]{String.valueOf(oncesi)});
    }

    Okutma ekle(int sayimId, int depo, String barkod, String kod, String ad, String birim, double miktar) {
        Okutma o = new Okutma();
        o.id = UUID.randomUUID().toString();
        o.sayimId = sayimId; o.depo = depo; o.barkod = barkod; o.kod = kod; o.ad = ad; o.birim = birim; o.miktar = miktar;
        o.zaman = System.currentTimeMillis();
        ContentValues v = new ContentValues();
        v.put("id", o.id); v.put("sayim_id", sayimId); v.put("depo", depo); v.put("barkod", barkod); v.put("kod", kod);
        v.put("ad", ad); v.put("birim", birim); v.put("miktar", miktar); v.put("zaman", o.zaman);
        getWritableDatabase().insertOrThrow("okutmalar", null, v);
        return o;
    }

    /** Deponun (henüz numarası alınmamış 0 dahil) okutmaları, en yenisi başta. */
    List<Okutma> okutmalar(int depo) {
        List<Okutma> l = new ArrayList<Okutma>();
        Cursor c = getReadableDatabase().rawQuery(
                "SELECT id, sayim_id, depo, barkod, kod, ad, birim, miktar, zaman, gonderildi, hata FROM okutmalar WHERE depo = ? ORDER BY zaman DESC",
                new String[]{String.valueOf(depo)});
        try {
            while (c.moveToNext()) {
                Okutma o = new Okutma();
                o.id = c.getString(0); o.sayimId = c.getInt(1); o.depo = c.getInt(2); o.barkod = c.getString(3); o.kod = c.getString(4);
                o.ad = c.getString(5); o.birim = c.getString(6); o.miktar = c.getDouble(7); o.zaman = c.getLong(8);
                o.gonderildi = c.getInt(9) == 1; o.hata = c.isNull(10) ? null : c.getString(10);
                l.add(o);
            }
        } finally { c.close(); }
        return l;
    }

    void gonderildi(String id) {
        ContentValues v = new ContentValues();
        v.put("gonderildi", 1);
        v.putNull("hata");
        getWritableDatabase().update("okutmalar", v, "id = ?", new String[]{id});
    }

    void hata(String id, String hata) {
        ContentValues v = new ContentValues();
        v.put("hata", hata);
        getWritableDatabase().update("okutmalar", v, "id = ?", new String[]{id});
    }

    /** Numarası olmayan (çevrimdışı başlanmış) ya da kapanmış sayıma ait gönderilmemiş okutmaları yeni sayıma taşır. */
    void sayimaAta(int depo, int eskiSayim, int yeniSayim) {
        ContentValues v = new ContentValues();
        v.put("sayim_id", yeniSayim);
        getWritableDatabase().update("okutmalar", v, "depo = ? AND sayim_id = ? AND gonderildi = 0",
                new String[]{String.valueOf(depo), String.valueOf(eskiSayim)});
    }

    void sil(String id) { getWritableDatabase().delete("okutmalar", "id = ?", new String[]{id}); }

    /** Sayım merkeze gönderilince cihazdaki kopyası silinir. */
    void sayimiTemizle(int sayimId) {
        getWritableDatabase().delete("okutmalar", "sayim_id = ? AND gonderildi = 1", new String[]{String.valueOf(sayimId)});
    }
}
