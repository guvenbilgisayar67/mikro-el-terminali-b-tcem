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
 * Cihazdaki veritabanı: çevrimdışı sayım için ürün listesi ve henüz merkeze gitmemiş okutmalar.
 * Okutmalar gönderilene kadar silinmez; uygulama kapansa, oturum düşse de cihazda kalır.
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
        final String barkod, kod, ad, birim;
        Urun(String barkod, String kod, String ad, String birim) { this.barkod = barkod; this.kod = kod; this.ad = ad; this.birim = birim; }
    }

    private static Yerel tek;

    static synchronized Yerel al(Context c) {
        if (tek == null) tek = new Yerel(c.getApplicationContext());
        return tek;
    }

    private Yerel(Context c) { super(c, "butcem.db", null, 1); }

    @Override public void onCreate(SQLiteDatabase db) {
        db.execSQL("CREATE TABLE urunler (barkod TEXT PRIMARY KEY, kod TEXT NOT NULL, ad TEXT NOT NULL, birim TEXT NOT NULL)");
        db.execSQL("CREATE TABLE okutmalar (id TEXT PRIMARY KEY, sayim_id INTEGER NOT NULL, depo INTEGER NOT NULL, barkod TEXT NOT NULL, "
                + "kod TEXT NOT NULL, ad TEXT NOT NULL, birim TEXT NOT NULL, miktar REAL NOT NULL, zaman INTEGER NOT NULL, "
                + "gonderildi INTEGER NOT NULL DEFAULT 0, hata TEXT)");
    }

    @Override public void onUpgrade(SQLiteDatabase db, int eski, int yeni) { }

    /** Sunucudan gelen [barkod, kod, ad, birim] listesiyle ürünleri tamamen yeniler. */
    int urunleriYaz(JSONArray a) {
        SQLiteDatabase db = getWritableDatabase();
        db.beginTransaction();
        try {
            db.delete("urunler", null, null);
            SQLiteStatement st = db.compileStatement("INSERT OR REPLACE INTO urunler (barkod, kod, ad, birim) VALUES (?, ?, ?, ?)");
            for (int i = 0; i < a.length(); i++) {
                JSONArray u = a.optJSONArray(i);
                if (u == null) continue;
                st.bindString(1, u.optString(0));
                st.bindString(2, u.optString(1));
                st.bindString(3, u.optString(2));
                st.bindString(4, u.optString(3));
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

    Urun urunBul(String barkod) {
        Cursor c = getReadableDatabase().rawQuery("SELECT barkod, kod, ad, birim FROM urunler WHERE barkod = ? OR kod = ? LIMIT 1",
                new String[]{barkod, barkod});
        try { return c.moveToFirst() ? new Urun(c.getString(0), c.getString(1), c.getString(2), c.getString(3)) : null; }
        finally { c.close(); }
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
