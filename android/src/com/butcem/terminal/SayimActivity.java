package com.butcem.terminal;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.DialogInterface;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.SharedPreferences;
import android.graphics.Color;
import android.media.AudioManager;
import android.media.ToneGenerator;
import android.os.Bundle;
import android.os.Handler;
import android.text.InputType;
import android.view.KeyEvent;
import android.view.View;
import android.view.inputmethod.EditorInfo;
import android.widget.AdapterView;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Spinner;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONObject;

import java.net.URLEncoder;
import java.text.SimpleDateFormat;
import java.util.ArrayList;
import java.util.Date;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;

/**
 * Stok sayımı. Barkod okutulur, ürün adı görünür, adet girilir. Okutma önce cihaza yazılır, bağlantı varsa hemen
 * merkeze gider; yoksa cihazda bekler ve bağlantı gelince kendiliğinden gönderilir (her 20 saniyede bir denenir).
 * Ürün listesi cihazda tutulduğu için bağlantı yokken de yanlış barkod anında fark edilir.
 */
public class SayimActivity extends Activity {
    private static final long DENEME_ARALIGI = 20000;

    private Api api;
    private Yerel yerel;
    private SharedPreferences ayar;
    private final Handler zamanlayici = new Handler();
    private ToneGenerator ses;

    private EditText barkod, adet;
    private TextView baglanti, sayimBilgi, urunAdi, mesaj, listeBilgi;
    private LinearLayout liste, depoSatiri;
    private Spinner depoSecim;
    private Button ekleDugmesi;

    private int depo;
    private final List<Integer> depoNolari = new ArrayList<Integer>();
    private Yerel.Urun secili;
    private String seciliBarkod;
    private boolean gonderiliyor, cevrimici = true;

    private final BroadcastReceiver tarayici = new BroadcastReceiver() {
        @Override public void onReceive(Context c, Intent i) {
            String v = i.getStringExtra("value");
            if (v == null) v = i.getStringExtra("scannerdata");
            if (v == null) v = i.getStringExtra("barcode_string");
            if (v != null && v.trim().length() > 0) okut(v.trim());
        }
    };

    private final Runnable periyodik = new Runnable() {
        @Override public void run() {
            gonder(false);
            zamanlayici.postDelayed(this, DENEME_ARALIGI);
        }
    };

    @Override
    protected void onCreate(Bundle b) {
        super.onCreate(b);
        api = new Api(this);
        yerel = Yerel.al(this);
        ayar = getSharedPreferences("butcem", MODE_PRIVATE);
        try { ses = new ToneGenerator(AudioManager.STREAM_NOTIFICATION, 100); } catch (RuntimeException e) { ses = null; }
        setTitle("Sayım");

        LinearLayout l = Ui.dikey(this);
        int p = Ui.dp(this, 14);
        l.setPadding(p, p, p, p);

        baglanti = Ui.yazi(this, "", 13, true);
        l.addView(baglanti);
        sayimBilgi = Ui.yazi(this, "", 13, false);
        sayimBilgi.setTextColor(Ui.SOLUK);
        l.addView(sayimBilgi, Ui.genis(this, 2));

        depoSatiri = Ui.dikey(this);
        depoSatiri.addView(Ui.yazi(this, "Sayılan depo", 13, false));
        depoSecim = new Spinner(this);
        depoSatiri.addView(depoSecim, Ui.genis(this, 2));
        l.addView(depoSatiri, Ui.genis(this, 6));

        barkod = new EditText(this);
        barkod.setHint("Barkod okutun");
        barkod.setSingleLine(true);
        barkod.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        barkod.setImeOptions(EditorInfo.IME_ACTION_NEXT);
        barkod.setOnEditorActionListener(new TextView.OnEditorActionListener() {
            @Override public boolean onEditorAction(TextView v, int id, KeyEvent e) {
                if (enter(id, e)) { if (e == null || e.getAction() == KeyEvent.ACTION_DOWN) okut(barkod.getText().toString().trim()); return true; }
                return false;
            }
        });
        l.addView(barkod, Ui.genis(this, 10));

        urunAdi = Ui.yazi(this, "", 20, true);
        l.addView(urunAdi, Ui.genis(this, 6));

        LinearLayout satir = Ui.yatay(this);
        satir.addView(Ui.yazi(this, "Adet", 16, false));
        adet = new EditText(this);
        adet.setSingleLine(true);
        adet.setInputType(InputType.TYPE_CLASS_NUMBER | InputType.TYPE_NUMBER_FLAG_DECIMAL);
        adet.setImeOptions(EditorInfo.IME_ACTION_DONE);
        adet.setTextSize(22);
        adet.setOnEditorActionListener(new TextView.OnEditorActionListener() {
            @Override public boolean onEditorAction(TextView v, int id, KeyEvent e) {
                if (enter(id, e)) { if (e == null || e.getAction() == KeyEvent.ACTION_DOWN) ekle(); return true; }
                return false;
            }
        });
        satir.addView(adet, Ui.agirlik(this, 1, 10));
        ekleDugmesi = Ui.dugme(this, "Ekle", true);
        ekleDugmesi.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { ekle(); } });
        satir.addView(ekleDugmesi, Ui.agirlik(this, 0, 8));
        l.addView(satir, Ui.genis(this, 4));

        mesaj = Ui.yazi(this, "", 15, true);
        l.addView(mesaj, Ui.genis(this, 6));

        listeBilgi = Ui.yazi(this, "", 13, false);
        l.addView(listeBilgi, Ui.genis(this, 12));
        liste = Ui.dikey(this);
        l.addView(liste, Ui.genis(this, 4));

        LinearLayout dugmeler = Ui.yatay(this);
        Button gonderDugmesi = Ui.dugme(this, "Şimdi gönder", false);
        gonderDugmesi.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { gonder(true); } });
        dugmeler.addView(gonderDugmesi, Ui.agirlik(this, 1, 0));
        Button urunDugmesi = Ui.dugme(this, "Ürün listesini güncelle", false);
        urunDugmesi.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { urunleriIndir(); } });
        dugmeler.addView(urunDugmesi, Ui.agirlik(this, 1, 8));
        l.addView(dugmeler, Ui.genis(this, 14));

        Button bitir = Ui.dugme(this, "Sayımı bitir, merkeze onaya gönder", true);
        bitir.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { bitirSor(); } });
        l.addView(bitir, Ui.genis(this, 10));

        ScrollView sv = new ScrollView(this);
        sv.addView(l);
        setContentView(sv);

        depoHazirla();
        urunSec(null, null);
        if (yerel.urunSayisi() == 0) urunleriIndir();
        listeyiGoster();
    }

    private static boolean enter(int id, KeyEvent e) {
        return id == EditorInfo.IME_ACTION_DONE || id == EditorInfo.IME_ACTION_NEXT || id == EditorInfo.IME_ACTION_SEARCH
                || (e != null && e.getKeyCode() == KeyEvent.KEYCODE_ENTER);
    }

    @Override protected void onResume() {
        super.onResume();
        IntentFilter f = new IntentFilter();
        f.addAction("android.intent.action.SCANRESULT");
        f.addAction("com.android.server.scannerservice.broadcast");
        registerReceiver(tarayici, f);
        zamanlayici.post(periyodik);
        barkod.requestFocus();
    }

    @Override protected void onPause() {
        super.onPause();
        unregisterReceiver(tarayici);
        zamanlayici.removeCallbacks(periyodik);
    }

    @Override protected void onDestroy() {
        super.onDestroy();
        if (ses != null) ses.release();
    }

    // ---- depo ve sayım numarası ----

    /** Mağaza kullanıcısı kendi deposunu sayar; merkez kullanıcısı depo seçer (liste çevrimdışı için saklanır). */
    private void depoHazirla() {
        if (api.depoNo() > 0) {
            depo = api.depoNo();
            depoSatiri.setVisibility(View.GONE);
            sayimiSor();
            return;
        }
        depo = ayar.getInt("sayimDepo", 0);
        String sakli = ayar.getString("depolar", null);
        if (sakli != null) depolariGoster(sakli);
        api.get("/api/depolar", new Api.Sonuc() {
            @Override public void tamam(String govde) { ayar.edit().putString("depolar", govde).apply(); depolariGoster(govde); }
            @Override public void hata(int durum, String m) { if (durum == 0) cevrimdisi(); }
        });
    }

    private void depolariGoster(String json) {
        try {
            JSONArray a = new JSONArray(json);
            List<String> adlar = new ArrayList<String>();
            depoNolari.clear();
            int sec = 0;
            for (int i = 0; i < a.length(); i++) {
                JSONObject d = a.getJSONObject(i);
                depoNolari.add(d.optInt("no"));
                adlar.add(d.optInt("no") + " · " + d.optString("ad").trim());
                if (d.optInt("no") == depo) sec = i;
            }
            depoSecim.setAdapter(new ArrayAdapter<String>(this, android.R.layout.simple_spinner_dropdown_item, adlar));
            depoSecim.setSelection(sec);
            depoSecim.setOnItemSelectedListener(new AdapterView.OnItemSelectedListener() {
                @Override public void onItemSelected(AdapterView<?> p, View v, int pos, long id) {
                    depo = depoNolari.get(pos);
                    ayar.edit().putInt("sayimDepo", depo).apply();
                    sayimiSor();
                    listeyiGoster();
                }
                @Override public void onNothingSelected(AdapterView<?> p) { }
            });
        } catch (Exception ignored) { }
    }

    private int sayimId() { return ayar.getInt("sayim_" + depo, 0); }

    private void sayimIdYaz(int id) { ayar.edit().putInt("sayim_" + depo, id).apply(); }

    /** Merkezdeki açık sayımı öğrenir. Kapanmışsa gönderilmemiş okutmalar bağlantıda açılacak yeni sayıma gider. */
    private void sayimiSor() {
        if (depo == 0) return;
        final int eski = sayimId();
        api.get("/api/sayim/acik?depo=" + depo, new Api.Sonuc() {
            @Override public void tamam(String govde) {
                cevrimici();
                int yeni = 0;
                try { if (govde.trim().length() > 0 && !"null".equals(govde.trim())) yeni = new JSONObject(govde).optInt("id"); } catch (Exception ignored) { }
                if (yeni != eski) { yerel.sayimaAta(depo, eski, yeni); sayimIdYaz(yeni); }
                listeyiGoster();
                gonder(false);
            }
            @Override public void hata(int durum, String m) { if (durum == 0) cevrimdisi(); else hataGoster(m); }
        });
    }

    // ---- okutma ----

    private void okut(final String kod) {
        if (kod.length() == 0) return;
        barkod.setText("");
        Yerel.Urun u = yerel.urunBul(kod);
        if (u != null) { urunSec(u, kod); return; }
        if (!cevrimici) { bulunamadi(kod); return; }
        // Cihazdaki liste eski olabilir: bağlantı varsa merkeze de sorulur.
        bilgi("Aranıyor…");
        api.get("/api/stok/" + kodla(kod) + "?miktar=false", new Api.Sonuc() {
            @Override public void tamam(String govde) {
                cevrimici();
                try {
                    JSONObject j = new JSONObject(govde).getJSONObject("urun");
                    urunSec(new Yerel.Urun(kod, j.optString("stokKodu"), j.optString("urunAdi"), j.optString("birim"),
                            j.optDouble("fiyat", 0), "", null), kod);
                    mesaj.setText("");
                } catch (Exception e) { bulunamadi(kod); }
            }
            @Override public void hata(int durum, String m) {
                if (durum == 0) cevrimdisi();
                bulunamadi(kod);
            }
        });
    }

    private void bulunamadi(String kod) {
        urunSec(null, null);
        bip(false);
        hataGoster("BU BARKOD BULUNAMADI: " + kod + "\nYanlış barkod olabilir; ürünün üzerindeki barkodu tekrar okutun.");
        barkod.requestFocus();
    }

    private void urunSec(Yerel.Urun u, String okunan) {
        secili = u;
        seciliBarkod = okunan;
        ekleDugmesi.setEnabled(u != null);
        adet.setEnabled(u != null);
        if (u == null) { urunAdi.setText("Barkod okutun"); urunAdi.setTextColor(Ui.SOLUK); adet.setText(""); return; }
        urunAdi.setText(u.ad + "\n" + okunan + " · " + u.birim);
        urunAdi.setTextColor(Color.rgb(27, 31, 36));
        mesaj.setText("");
        adet.setText("1");
        adet.requestFocus();
        adet.selectAll();
    }

    private void ekle() {
        if (secili == null) return;
        double m;
        try { m = Double.parseDouble(adet.getText().toString().replace(",", ".")); } catch (NumberFormatException e) { m = 0; }
        if (m <= 0 || m > 100000) { hataGoster("Geçerli bir adet girin."); return; }
        if (depo == 0) { hataGoster("Önce sayılan depoyu seçin."); return; }
        yerel.ekle(sayimId(), depo, seciliBarkod, secili.kod, secili.ad, secili.birim, m);
        bip(true);
        bilgi("+" + Ui.miktar(m) + " " + secili.birim + " · " + secili.ad + "\nToplam: " + Ui.miktar(toplam(secili.kod)));
        urunSec(null, null);
        barkod.requestFocus();
        listeyiGoster();
        gonder(false);
    }

    private double toplam(String kod) {
        double t = 0;
        for (Yerel.Okutma o : yerel.okutmalar(depo)) if (o.kod.equals(kod) && o.hata == null) t += o.miktar;
        return t;
    }

    // ---- liste ----

    private static final class Kalem {
        String kod, barkod, ad, birim, hata;
        double miktar;
        int bekleyen;
        final List<String> hataliIdler = new ArrayList<String>();
    }

    private void listeyiGoster() {
        liste.removeAllViews();
        Map<String, Kalem> kalemler = new LinkedHashMap<String, Kalem>();
        int bekleyen = 0, hatali = 0;
        for (Yerel.Okutma o : yerel.okutmalar(depo)) {
            Kalem k = kalemler.get(o.kod);
            if (k == null) { k = new Kalem(); k.kod = o.kod; k.barkod = o.barkod; k.ad = o.ad; k.birim = o.birim; kalemler.put(o.kod, k); }
            if (o.hata != null) { k.hata = o.hata; k.hataliIdler.add(o.id); hatali++; continue; }
            k.miktar += o.miktar;
            if (!o.gonderildi) { k.bekleyen++; bekleyen++; }
        }
        for (final Kalem k : kalemler.values()) {
            String durum = k.hata != null ? "  ✗ " + k.hata : k.bekleyen > 0 ? "  ⏳" : "  ✓";
            TextView t = Ui.yazi(this, k.barkod + "  ·  " + Ui.miktar(k.miktar) + " " + k.birim + "\n" + k.ad + durum, 15, false);
            if (k.hata != null) t.setTextColor(Ui.HATA);
            int p = Ui.dp(this, 8);
            t.setPadding(0, p, 0, p);
            t.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { duzeltSor(k); } });
            liste.addView(t);
        }
        if (kalemler.isEmpty()) liste.addView(Ui.yazi(this, "Henüz okutma yok.", 14, false));
        listeBilgi.setText(kalemler.size() + " ürün sayıldı" + (bekleyen > 0 ? " · " + bekleyen + " okutma gönderilmeyi bekliyor" : " · hepsi merkezde")
                + (hatali > 0 ? " · " + hatali + " hatalı" : "") + "\n(Düzeltmek için ürüne dokunun)");
        int id = sayimId();
        String tarih = new SimpleDateFormat("dd.MM.yyyy HH:mm", new Locale("tr", "TR")).format(new Date(ayar.getLong("urunlerZamani", 0)));
        sayimBilgi.setText((id > 0 ? "Sayım no " + id : "Sayım numarası bağlantı gelince alınacak") + " · depo " + depo
                + "\nÜrün listesi: " + yerel.urunSayisi() + " ürün" + (ayar.getLong("urunlerZamani", 0) > 0 ? ", " + tarih : ""));
    }

    /** Toplamı düzeltme: fark kadar artı/eksi okutma eklenir (merkezde iz kalır). Hatalı okutmalar silinebilir. */
    private void duzeltSor(final Kalem k) {
        if (k.hata != null) {
            new AlertDialog.Builder(this).setTitle(k.ad).setMessage("Merkez bu okutmayı kabul etmedi:\n" + k.hata)
                    .setPositiveButton("Sil", new DialogInterface.OnClickListener() {
                        @Override public void onClick(DialogInterface d, int w) { for (String id : k.hataliIdler) yerel.sil(id); listeyiGoster(); }
                    })
                    .setNegativeButton("Vazgeç", null).show();
            return;
        }
        final EditText e = new EditText(this);
        e.setInputType(InputType.TYPE_CLASS_NUMBER | InputType.TYPE_NUMBER_FLAG_DECIMAL);
        e.setText(Ui.miktar(k.miktar).replace(".", "").replace(",", "."));
        e.selectAll();
        new AlertDialog.Builder(this).setTitle(k.ad).setMessage("Toplam sayılan (" + k.birim + ")").setView(e)
                .setPositiveButton("Kaydet", new DialogInterface.OnClickListener() {
                    @Override public void onClick(DialogInterface d, int w) {
                        try {
                            double m = Double.parseDouble(e.getText().toString().replace(",", "."));
                            if (m < 0) throw new NumberFormatException();
                            double fark = m - k.miktar;
                            if (Math.abs(fark) > 1e-9) {
                                yerel.ekle(sayimId(), depo, k.barkod, k.kod, k.ad, k.birim, fark);
                                bilgi(k.ad + " toplamı " + Ui.miktar(m) + " yapıldı.");
                                listeyiGoster();
                                gonder(false);
                            }
                        } catch (NumberFormatException ex) { hataGoster("Geçerli bir miktar girin."); }
                    }
                })
                .setNegativeButton("Vazgeç", null).show();
    }

    // ---- merkeze gönderim ----

    private void gonder(final boolean elle) {
        if (gonderiliyor || depo == 0) return;
        final List<Yerel.Okutma> bekleyen = new ArrayList<Yerel.Okutma>();
        for (Yerel.Okutma o : yerel.okutmalar(depo)) if (!o.gonderildi && o.hata == null) bekleyen.add(0, o);
        if (bekleyen.isEmpty()) { if (elle) bilgi("Gönderilecek okutma yok."); return; }
        gonderiliyor = true;
        final int id = sayimId();
        if (id == 0) {
            // Çevrimdışı başlanmış sayım: merkezde açık sayım varsa ona, yoksa yeni sayıma.
            try {
                api.post("/api/sayim", new JSONObject().put("depo", depo), new Api.Sonuc() {
                    @Override public void tamam(String govde) {
                        gonderiliyor = false;
                        cevrimici();
                        try {
                            int yeni = new JSONObject(govde).optInt("id");
                            yerel.sayimaAta(depo, 0, yeni);
                            sayimIdYaz(yeni);
                            listeyiGoster();
                            gonder(elle);
                        } catch (Exception e) { hataGoster(e.getMessage()); }
                    }
                    @Override public void hata(int durum, String m) { gonderiliyor = false; sonucHata(durum, m, elle); }
                });
            } catch (Exception e) { gonderiliyor = false; }
            return;
        }
        JSONArray dizi = new JSONArray();
        try {
            for (Yerel.Okutma o : bekleyen)
                dizi.put(new JSONObject().put("urun", o.barkod).put("miktar", o.miktar).put("istemciId", o.id));
        } catch (Exception e) { gonderiliyor = false; return; }
        api.postMetin("/api/sayim/" + id + "/okut-toplu", dizi.toString(), new Api.Sonuc() {
            @Override public void tamam(String govde) {
                gonderiliyor = false;
                cevrimici();
                int tamam = 0, hatali = 0;
                try {
                    JSONArray a = new JSONArray(govde);
                    for (int i = 0; i < a.length(); i++) {
                        JSONObject r = a.getJSONObject(i);
                        if (r.optBoolean("tamam")) { yerel.gonderildi(r.optString("istemciId")); tamam++; }
                        else { yerel.hata(r.optString("istemciId"), r.optString("hata")); hatali++; }
                    }
                } catch (Exception e) { hataGoster("Beklenmeyen cevap: " + e.getMessage()); }
                listeyiGoster();
                if (hatali > 0) { bip(false); hataGoster(hatali + " okutma merkezde kabul edilmedi (listede kırmızı)."); }
                else if (elle) bilgi(tamam + " okutma merkeze gönderildi.");
            }
            @Override public void hata(int durum, String m) {
                gonderiliyor = false;
                if (m != null && m.contains("kapatılmış")) {
                    // Merkez sayımı kapattı/onayladı: bekleyenler yeni sayıma gidecek.
                    yerel.sayimaAta(depo, id, 0);
                    sayimIdYaz(0);
                    hataGoster("Bu sayım merkezde kapatılmış. Gönderilmemiş okutmalar yeni sayım olarak gönderilecek.");
                    listeyiGoster();
                    return;
                }
                sonucHata(durum, m, elle);
            }
        });
    }

    private void sonucHata(int durum, String m, boolean elle) {
        if (durum == 0) { cevrimdisi(); if (elle) hataGoster("Merkeze ulaşılamadı. Okutmalar cihazda saklı, bağlantı gelince gönderilecek."); }
        else if (durum == 401) hataGoster("Oturum süresi doldu. Okutmalar cihazda saklı; çıkış yapıp tekrar giriş yapın, sonra gönderilir.");
        else hataGoster(m);
    }

    private void bitirSor() {
        int bekleyen = 0;
        for (Yerel.Okutma o : yerel.okutmalar(depo)) if (!o.gonderildi && o.hata == null) bekleyen++;
        if (bekleyen > 0 || sayimId() == 0) {
            hataGoster("Önce bütün okutmalar merkeze gitmeli (" + bekleyen + " bekliyor). Bağlantıyı kontrol edip \"Şimdi gönder\"e basın.");
            gonder(true);
            return;
        }
        new AlertDialog.Builder(this).setTitle("Sayımı bitir")
                .setMessage("Sayım merkeze onaya gönderilecek; sonra bu sayıma okutma yapılamaz. Emin misiniz?")
                .setPositiveButton("Bitir", new DialogInterface.OnClickListener() {
                    @Override public void onClick(DialogInterface d, int w) { bitir(); }
                })
                .setNegativeButton("Vazgeç", null).show();
    }

    private void bitir() {
        final int id = sayimId();
        api.postMetin("/api/sayim/" + id + "/gonder", "{}", new Api.Sonuc() {
            @Override public void tamam(String govde) {
                yerel.sayimiTemizle(id);
                sayimIdYaz(0);
                listeyiGoster();
                bilgi("Sayım " + id + " merkeze onaya gönderildi.");
            }
            @Override public void hata(int durum, String m) { sonucHata(durum, m, true); }
        });
    }

    private void urunleriIndir() {
        bilgi("Ürün listesi indiriliyor…");
        Katalog.indir(this, new Katalog.Bitti() {
            @Override public void sonuc(int durum, String m) {
                if (durum == 200) { cevrimici(); bilgi(m); listeyiGoster(); }
                else if (durum == -1) hataGoster(m);
                else sonucHata(durum, m, true);
            }
        });
    }

    // ---- durum ----

    private void cevrimici() {
        cevrimici = true;
        baglanti.setText("● Çevrimiçi: okutmalar merkeze anında gidiyor");
        baglanti.setTextColor(Ui.VURGU);
    }

    private void cevrimdisi() {
        cevrimici = false;
        baglanti.setText("● Çevrimdışı: okutmalar cihazda saklanıyor, bağlantı gelince gönderilecek");
        baglanti.setTextColor(Ui.HATA);
    }

    private void bip(boolean tamam) {
        if (ses == null) return;
        try { ses.startTone(tamam ? ToneGenerator.TONE_PROP_ACK : ToneGenerator.TONE_SUP_ERROR, tamam ? 120 : 600); } catch (RuntimeException ignored) { }
    }

    private void hataGoster(String m) { mesaj.setTextColor(Ui.HATA); mesaj.setText(m); }

    private void bilgi(String m) { mesaj.setTextColor(Ui.VURGU); mesaj.setText(m); }

    private static String kodla(String s) {
        try { return URLEncoder.encode(s, "UTF-8").replace("+", "%20"); } catch (Exception e) { return s; }
    }
}
