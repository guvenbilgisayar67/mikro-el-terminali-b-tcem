package com.butcem.terminal;

import android.content.Context;
import android.content.SharedPreferences;
import android.os.Handler;
import android.os.Looper;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** Sunucu ile konuşan küçük yardımcı. İstekler arka planda çalışır, sonuç ana iş parçacığına döner. */
public final class Api {
    public interface Sonuc {
        void tamam(String govde);
        void hata(int durum, String mesaj);
    }

    private static final ExecutorService ARKA = Executors.newFixedThreadPool(2);
    private static final Handler ANA = new Handler(Looper.getMainLooper());

    private final SharedPreferences ayar;

    public Api(Context c) {
        ayar = c.getSharedPreferences("butcem", Context.MODE_PRIVATE);
    }

    public String sunucu() { return ayar.getString("sunucu", "http://192.168.1.10:5080"); }
    public String token() { return ayar.getString("token", null); }
    public String adSoyad() { return ayar.getString("adSoyad", ""); }
    public int depoNo() { return ayar.getInt("depoNo", 0); }
    public int sonYazici() { return ayar.getInt("yazici", 0); }

    public void sunucuKaydet(String s) {
        ayar.edit().putString("sunucu", s.trim().replaceAll("/+$", "")).apply();
    }

    public void oturumKaydet(JSONObject o) {
        SharedPreferences.Editor e = ayar.edit();
        if (o.has("token")) e.putString("token", o.optString("token"));
        StringBuilder y = new StringBuilder(",");
        JSONArray a = o.optJSONArray("yetkiler");
        if (a != null) for (int i = 0; i < a.length(); i++) y.append(a.optString(i)).append(',');
        e.putString("adSoyad", o.optString("adSoyad"))
                .putString("rol", o.optString("rol"))
                .putString("yetkiler", y.toString())
                .putInt("depoNo", o.isNull("depoNo") ? 0 : o.optInt("depoNo"))
                .apply();
    }

    /** Yönetici her şeye yetkilidir; diğerleri panelin Yönetim ekranında işaretlenen yetkilere. */
    public boolean yetkili(String yetki) {
        return "admin".equals(ayar.getString("rol", "")) || ayar.getString("yetkiler", "").contains("," + yetki + ",");
    }

    public void yaziciKaydet(int id) { ayar.edit().putInt("yazici", id).apply(); }

    public void cikis() { ayar.edit().remove("token").apply(); }

    public void get(String yol, Sonuc s) { istek("GET", yol, null, s); }

    public void post(String yol, JSONObject govde, Sonuc s) { istek("POST", yol, govde.toString(), s); }

    /** Gövdesi hazır JSON metni olan istek (örn. dizi). */
    public void postMetin(String yol, String govde, Sonuc s) { istek("POST", yol, govde, s); }

    private void istek(final String yontem, final String yol, final String govde, final Sonuc s) {
        final String adres = sunucu() + yol;
        final String tk = token();
        ARKA.execute(new Runnable() {
            @Override public void run() {
                int durum = 0;
                String metin;
                try {
                    HttpURLConnection c = (HttpURLConnection) new URL(adres).openConnection();
                    c.setConnectTimeout(8000);
                    c.setReadTimeout(30000);
                    c.setRequestMethod(yontem);
                    c.setRequestProperty("Accept", "application/json");
                    if (tk != null) c.setRequestProperty("Authorization", "Bearer " + tk);
                    if (govde != null) {
                        c.setDoOutput(true);
                        c.setRequestProperty("Content-Type", "application/json; charset=utf-8");
                        OutputStream os = c.getOutputStream();
                        os.write(govde.getBytes("UTF-8"));
                        os.close();
                    }
                    durum = c.getResponseCode();
                    InputStream is = durum >= 400 ? c.getErrorStream() : c.getInputStream();
                    metin = is == null ? "" : oku(is);
                    c.disconnect();
                } catch (Exception e) {
                    metin = "Sunucuya ulaşılamadı (" + e.getClass().getSimpleName() + "). VPN bağlantısını ve sunucu adresini kontrol edin.";
                }
                final int d = durum;
                final String m = metin;
                ANA.post(new Runnable() {
                    @Override public void run() {
                        if (d >= 200 && d < 300) s.tamam(m);
                        else s.hata(d, hataMesaji(d, m));
                    }
                });
            }
        });
    }

    private static String hataMesaji(int durum, String govde) {
        if (durum == 401) return "Oturum süresi doldu ya da bilgiler hatalı.";
        if (durum == 403) return "Bu işlem için yetkiniz yok.";
        if (durum == 404) return "Bulunamadı.";
        if (govde == null || govde.length() == 0) return "Hata " + durum;
        try {
            Object j = new org.json.JSONTokener(govde).nextValue();
            if (j instanceof String) return (String) j;
            if (j instanceof JSONObject) {
                JSONObject o = (JSONObject) j;
                if (o.has("hata")) return o.optString("hata");
                if (o.has("title")) return o.optString("title");
            }
        } catch (Exception ignored) { }
        return govde.length() > 200 ? govde.substring(0, 200) : govde;
    }

    private static String oku(InputStream is) throws java.io.IOException {
        ByteArrayOutputStream b = new ByteArrayOutputStream();
        byte[] tampon = new byte[8192];
        int n;
        while ((n = is.read(tampon)) > 0) b.write(tampon, 0, n);
        is.close();
        return b.toString("UTF-8");
    }
}
