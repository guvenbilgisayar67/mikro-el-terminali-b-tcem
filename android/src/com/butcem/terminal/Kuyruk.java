package com.butcem.terminal;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Context;
import android.content.DialogInterface;
import android.os.Handler;
import android.os.Looper;

import org.json.JSONObject;

import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;

/**
 * Bekleyen işlemler: sevk fişi ve etiket basımı önce cihaza yazılır, sonra merkeze gönderilir.
 * Bağlantı yoksa cihazda bekler; açık olan ekran 20 saniyede bir yeniden dener. Her işlemin kimliği
 * X-Istemci-Id ile gider, cevap yolda kaybolup tekrar gönderilse de merkezde bir kez yapılır.
 */
final class Kuyruk {
    interface Dinleyici {
        /** Bekleyen sayısı ya da bağlantı durumu değişti; bilgi boş değilse kullanıcıya gösterilecek sonuç. */
        void degisti(String bilgi);
    }

    private static final long ARALIK = 20000;
    private static final Handler ANA = new Handler(Looper.getMainLooper());
    private static final Set<String> yolda = new HashSet<String>();
    private static Context uyg;
    private static Dinleyici dinleyici;
    private static boolean gonderiliyor;

    private static final Runnable PERIYODIK = new Runnable() {
        @Override public void run() {
            gonder();
            ANA.postDelayed(this, ARALIK);
        }
    };

    private Kuyruk() { }

    /** Ekran açılınca (onResume): gönderimi başlatır ve sonuçları bu ekrana bildirir. */
    static void baslat(Activity a, Dinleyici d) {
        uyg = a.getApplicationContext();
        dinleyici = d;
        Yerel.al(uyg).bekleyenleriTemizle(System.currentTimeMillis() - 3L * 24 * 3600 * 1000);
        ANA.removeCallbacks(PERIYODIK);
        ANA.post(PERIYODIK);
    }

    /** Ekran kapanınca (onPause). */
    static void durdur(Dinleyici d) {
        if (dinleyici != d) return;
        dinleyici = null;
        ANA.removeCallbacks(PERIYODIK);
    }

    /**
     * İşlemi cihaza yazar ve hemen göndermeyi dener. Sonuç:
     * tamam → merkez yaptı; hata(0 / 5xx / 401) → cihazda bekliyor, sonra gönderilecek;
     * diğer hata → merkez kabul etmedi (doğrulama), kayıt silinir ki kullanıcı düzeltip yeniden göndersin.
     */
    static void isle(final Activity a, String tur, String yol, JSONObject govde, String ozet, final Api.Sonuc s) {
        uyg = a.getApplicationContext();
        final Yerel yerel = Yerel.al(uyg);
        final Yerel.Bekleyen b = yerel.bekleyenEkle(tur, yol, govde.toString(), ozet);
        yolda.add(b.id);
        new Api(uyg).postIslem(yol, b.govde, b.id, new Api.Sonuc() {
            @Override public void tamam(String cevap) {
                yolda.remove(b.id);
                yerel.bekleyenSonuc(b.id, Yerel.GONDERILDI, sonucMetni(b.tur, cevap));
                s.tamam(cevap);
                bildir(null);
            }
            @Override public void hata(int durum, String m) {
                yolda.remove(b.id);
                if (!tekrarDenenir(durum)) yerel.bekleyenSil(b.id);
                s.hata(durum, m);
                bildir(null);
            }
        });
    }

    static boolean tekrarDenenir(int durum) { return durum == 0 || durum == 401 || durum >= 500; }

    /** Bekleyenleri sırayla gönderir. Bekleyen yoksa bağlantıyı yoklar (gösterge için). */
    static void gonder() {
        if (uyg == null || gonderiliyor) return;
        final Yerel yerel = Yerel.al(uyg);
        Yerel.Bekleyen siradaki = null;
        for (Yerel.Bekleyen b : yerel.bekleyenler(false)) if (!yolda.contains(b.id)) { siradaki = b; break; }
        final Api api = new Api(uyg);
        if (siradaki == null) {
            if (api.token() == null) return;
            final boolean onceki = Api.cevrimici;
            api.get("/api/yazma/durum", new Api.Sonuc() {
                @Override public void tamam(String g) { if (!onceki) bildir(null); }
                @Override public void hata(int d, String m) { if (onceki != Api.cevrimici) bildir(null); }
            });
            return;
        }
        if (api.token() == null) return;
        final Yerel.Bekleyen b = siradaki;
        gonderiliyor = true;
        yolda.add(b.id);
        api.postIslem(b.yol, b.govde, b.id, new Api.Sonuc() {
            @Override public void tamam(String cevap) {
                yolda.remove(b.id);
                gonderiliyor = false;
                yerel.bekleyenSonuc(b.id, Yerel.GONDERILDI, sonucMetni(b.tur, cevap));
                bildir(b.ozet + " gönderildi" + ek(sonucMetni(b.tur, cevap)));
                gonder();
            }
            @Override public void hata(int durum, String m) {
                yolda.remove(b.id);
                gonderiliyor = false;
                if (tekrarDenenir(durum)) { bildir(null); return; }
                yerel.bekleyenSonuc(b.id, Yerel.REDDEDILDI, m);
                bildir(b.ozet + " merkezde kabul edilmedi: " + m);
                gonder();
            }
        });
    }

    private static String ek(String s) { return s == null || s.length() == 0 ? "." : ": " + s; }

    private static void bildir(String bilgi) {
        if (dinleyici != null) dinleyici.degisti(bilgi);
    }

    /** Merkezin cevabından kısa sonuç: sevk fişi numarası, basılan etiket sayısı. */
    static String sonucMetni(String tur, String cevap) {
        try {
            JSONObject r = new JSONObject(cevap);
            if ("sevk".equals(tur)) return "sevk fişi " + r.optString("seri") + " " + r.optInt("sira");
            if ("etiket".equals(tur)) return r.optInt("basilacak") + " etiket yazıcıya gönderildi";
        } catch (Exception ignored) { }
        return "";
    }

    /** Gösterge satırı: bağlantı, bekleyen sayısı, ürün listesinin tarihi. */
    static String durumMetni(Context c) {
        int n = Yerel.al(c).bekleyenSayisi();
        StringBuilder s = new StringBuilder(Api.cevrimici ? "● Çevrimiçi" : "● Çevrimdışı");
        if (n > 0) s.append(" · ").append(n).append(" işlem gönderilmeyi bekliyor");
        long z = Katalog.zaman(c);
        s.append(" · ürün listesi ").append(z == 0 ? "yok" : tarih(z));
        return s.toString();
    }

    static String tarih(long z) { return new SimpleDateFormat("dd.MM HH:mm", new Locale("tr", "TR")).format(new Date(z)); }

    /** Son 50 işlemin listesi; bekleyenleri şimdi göndermek ve bitmişleri temizlemek için. */
    static void goster(final Activity a) {
        final Yerel yerel = Yerel.al(a);
        List<Yerel.Bekleyen> l = yerel.bekleyenler(true);
        StringBuilder s = new StringBuilder();
        for (Yerel.Bekleyen b : l) {
            if (s.length() > 0) s.append("\n\n");
            s.append(b.durum == Yerel.BEKLIYOR ? "⏳ " : b.durum == Yerel.GONDERILDI ? "✓ " : "✗ ")
                    .append(tarih(b.zaman)).append(" · ").append(b.ozet).append('\n')
                    .append(b.durum == Yerel.BEKLIYOR ? "Bağlantı bekleniyor"
                            : b.durum == Yerel.GONDERILDI ? (b.sonuc == null || b.sonuc.length() == 0 ? "Gönderildi" : b.sonuc)
                            : "Kabul edilmedi: " + b.hata);
        }
        if (l.isEmpty()) s.append("Kayıtlı işlem yok.");
        new AlertDialog.Builder(a).setTitle("Bekleyen işlemler").setMessage(s.toString())
                .setPositiveButton("Şimdi gönder", new DialogInterface.OnClickListener() {
                    @Override public void onClick(DialogInterface d, int w) { uyg = a.getApplicationContext(); gonder(); }
                })
                .setNeutralButton("Bitenleri temizle", new DialogInterface.OnClickListener() {
                    @Override public void onClick(DialogInterface d, int w) { yerel.bekleyenleriTemizle(Long.MAX_VALUE); bildir(null); }
                })
                .setNegativeButton("Kapat", null).show();
    }
}
