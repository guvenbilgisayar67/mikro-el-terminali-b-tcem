package com.butcem.terminal;

import android.app.Activity;
import android.content.Context;

import org.json.JSONObject;

/**
 * Cihazdaki ürün listesi (barkod, ad, birim, satış fiyatı, fiyat değişim tarihi, kendi deponun stoğu).
 * Fiyat sorgu, etiket, sevk ve sayım bağlantı yokken bu listeyle çalışır. Uygulama açılışında 6 saatten
 * eskiyse kendiliğinden, menüden istenince elle yenilenir.
 */
final class Katalog {
    interface Bitti { void sonuc(int durum, String mesaj); }

    static final long ESKIME = 6L * 3600 * 1000;
    private static boolean indiriliyor;

    private Katalog() { }

    static long zaman(Context c) { return c.getSharedPreferences("butcem", Context.MODE_PRIVATE).getLong("urunlerZamani", 0); }

    static boolean eski(Context c) { return Yerel.al(c).urunSayisi() == 0 || System.currentTimeMillis() - zaman(c) > ESKIME; }

    static void indir(final Activity a, final Bitti b) {
        if (indiriliyor) { b.sonuc(-1, "Ürün listesi zaten indiriliyor…"); return; }
        indiriliyor = true;
        final Api api = new Api(a);
        api.get("/api/katalog" + (api.depoNo() > 0 ? "?depo=" + api.depoNo() : ""), new Api.Sonuc() {
            @Override public void tamam(final String govde) {
                // 18 bin satır: ayrıştırma ve yazma arka planda, ekran donmasın.
                new Thread(new Runnable() {
                    @Override public void run() {
                        int durum = 200;
                        String sonuc;
                        try {
                            int n = Yerel.al(a).urunleriYaz(new JSONObject(govde).getJSONArray("urunler"));
                            a.getSharedPreferences("butcem", Context.MODE_PRIVATE).edit().putLong("urunlerZamani", System.currentTimeMillis()).apply();
                            sonuc = "Ürün listesi güncellendi: " + n + " barkod.";
                        } catch (Exception e) {
                            durum = -1;
                            sonuc = "Ürün listesi yazılamadı: " + e.getMessage();
                        }
                        final int d = durum;
                        final String s = sonuc;
                        a.runOnUiThread(new Runnable() { @Override public void run() { indiriliyor = false; b.sonuc(d, s); } });
                    }
                }).start();
            }
            @Override public void hata(int durum, String m) { indiriliyor = false; b.sonuc(durum, m); }
        });
    }
}
