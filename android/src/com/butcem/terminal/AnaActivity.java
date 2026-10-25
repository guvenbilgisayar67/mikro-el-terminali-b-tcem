package com.butcem.terminal;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.DialogInterface;
import android.content.Intent;
import android.content.IntentFilter;
import android.graphics.Color;
import android.os.Bundle;
import android.text.InputType;
import android.view.KeyEvent;
import android.view.Menu;
import android.view.MenuItem;
import android.view.View;
import android.view.inputmethod.EditorInfo;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Spinner;
import android.widget.TextView;
import android.widget.Toast;

import org.json.JSONArray;
import org.json.JSONObject;

import java.net.URLEncoder;
import java.text.SimpleDateFormat;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;

/**
 * Barkod okut → ürün adı, satış fiyatı, fiyat değişim tarihi ve mağaza stokları.
 * "Listeye ekle" ile etiket listesi oluşur; seçilen mağaza yazıcısına raf / manav / A4 etiket basılır.
 * Okuyucu iki şekilde çalışır: klavye modu (okutunca alana yazar + Enter) ya da iData yayın (broadcast) modu.
 */
public class AnaActivity extends Activity {
    private static final String[] TARAYICI_YAYINLARI = {
            "android.intent.action.SCANRESULT",              // iData varsayılanı, ek: "value"
            "com.android.server.scannerservice.broadcast",   // eski iData / Urovo, ek: "scannerdata"
    };

    private Api api;
    private EditText barkod;
    private TextView urunAdi, fiyat, ayrinti, stoklar, liste, mesaj;
    private Spinner yaziciSecim, sablonSecim;
    private Button basDugmesi;
    private JSONObject sonUrun;
    private final Map<String, Kalem> kalemler = new LinkedHashMap<String, Kalem>();
    private final List<Integer> yaziciIdleri = new ArrayList<Integer>();

    private static final class Kalem {
        final String barkod, ad;
        int adet;
        Kalem(String barkod, String ad) { this.barkod = barkod; this.ad = ad; }
    }

    private final BroadcastReceiver tarayici = new BroadcastReceiver() {
        @Override public void onReceive(Context c, Intent i) {
            String v = i.getStringExtra("value");
            if (v == null) v = i.getStringExtra("scannerdata");
            if (v == null) v = i.getStringExtra("barcode_string");
            if (v != null && v.trim().length() > 0) sorgula(v.trim());
        }
    };

    @Override
    protected void onCreate(Bundle b) {
        super.onCreate(b);
        api = new Api(this);
        setTitle("Bütçem · " + api.adSoyad());

        LinearLayout l = Ui.dikey(this);
        int p = Ui.dp(this, 14);
        l.setPadding(p, p, p, p);

        LinearLayout satir = Ui.yatay(this);
        barkod = new EditText(this);
        barkod.setHint("Barkod okutun");
        barkod.setSingleLine(true);
        barkod.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        barkod.setImeOptions(EditorInfo.IME_ACTION_SEARCH);
        barkod.setOnEditorActionListener(new TextView.OnEditorActionListener() {
            @Override public boolean onEditorAction(TextView v, int id, KeyEvent e) {
                boolean enter = e != null && e.getKeyCode() == KeyEvent.KEYCODE_ENTER;
                if (id == EditorInfo.IME_ACTION_SEARCH || id == EditorInfo.IME_ACTION_DONE || enter) {
                    if (e == null || e.getAction() == KeyEvent.ACTION_DOWN) sorgula(barkod.getText().toString().trim());
                    return true;
                }
                return false;
            }
        });
        satir.addView(barkod, Ui.agirlik(this, 1, 0));
        Button ara = Ui.dugme(this, "Sorgula", false);
        ara.setOnClickListener(new View.OnClickListener() {
            @Override public void onClick(View v) { sorgula(barkod.getText().toString().trim()); }
        });
        satir.addView(ara, Ui.agirlik(this, 0, 8));
        l.addView(satir);

        mesaj = Ui.yazi(this, "", 14, false);
        mesaj.setTextColor(Ui.HATA);
        l.addView(mesaj, Ui.genis(this, 4));

        urunAdi = Ui.yazi(this, "", 20, true);
        l.addView(urunAdi, Ui.genis(this, 6));
        fiyat = Ui.yazi(this, "", 34, true);
        fiyat.setTextColor(Ui.VURGU);
        l.addView(fiyat, Ui.genis(this, 2));
        ayrinti = Ui.yazi(this, "", 13, false);
        ayrinti.setTextColor(Ui.SOLUK);
        l.addView(ayrinti);
        stoklar = Ui.yazi(this, "", 14, false);
        l.addView(stoklar, Ui.genis(this, 6));

        LinearLayout ekleSatiri = Ui.yatay(this);
        Button ekle1 = Ui.dugme(this, "Listeye ekle", true);
        ekle1.setOnClickListener(new View.OnClickListener() {
            @Override public void onClick(View v) { ekle(1); }
        });
        Button ekleN = Ui.dugme(this, "Adet…", false);
        ekleN.setOnClickListener(new View.OnClickListener() {
            @Override public void onClick(View v) { adetSor(); }
        });
        ekleSatiri.addView(ekle1, Ui.agirlik(this, 2, 0));
        ekleSatiri.addView(ekleN, Ui.agirlik(this, 1, 8));
        l.addView(ekleSatiri, Ui.genis(this, 10));

        View cizgi = new View(this);
        cizgi.setBackgroundColor(Color.rgb(226, 229, 234));
        LinearLayout.LayoutParams cp = Ui.genis(this, 14);
        cp.height = Ui.dp(this, 1);
        l.addView(cizgi, cp);

        l.addView(Ui.yazi(this, "Etiket listesi", 15, true), Ui.genis(this, 10));
        liste = Ui.yazi(this, "", 14, false);
        l.addView(liste, Ui.genis(this, 4));

        sablonSecim = new Spinner(this);
        sablonSecim.setAdapter(new ArrayAdapter<String>(this, android.R.layout.simple_spinner_dropdown_item,
                new String[]{"Raf etiketi", "Manav etiketi"}));
        l.addView(sablonSecim, Ui.genis(this, 8));

        yaziciSecim = new Spinner(this);
        l.addView(yaziciSecim, Ui.genis(this, 4));

        LinearLayout basSatiri = Ui.yatay(this);
        basDugmesi = Ui.dugme(this, "Bas", true);
        basDugmesi.setOnClickListener(new View.OnClickListener() {
            @Override public void onClick(View v) { bas(); }
        });
        Button temizle = Ui.dugme(this, "Temizle", false);
        temizle.setOnClickListener(new View.OnClickListener() {
            @Override public void onClick(View v) { kalemler.clear(); listeyiGoster(); }
        });
        basSatiri.addView(basDugmesi, Ui.agirlik(this, 2, 0));
        basSatiri.addView(temizle, Ui.agirlik(this, 1, 8));
        l.addView(basSatiri, Ui.genis(this, 8));

        ScrollView s = new ScrollView(this);
        s.addView(l);
        setContentView(s);

        listeyiGoster();
        yazicilariYukle();
    }

    @Override protected void onResume() {
        super.onResume();
        IntentFilter f = new IntentFilter();
        for (String a : TARAYICI_YAYINLARI) f.addAction(a);
        registerReceiver(tarayici, f);
        barkod.requestFocus();
    }

    @Override protected void onPause() {
        super.onPause();
        unregisterReceiver(tarayici);
    }

    @Override public boolean onCreateOptionsMenu(Menu m) {
        m.add(0, 1, 0, "Bugün fiyatı değişenleri ekle");
        m.add(0, 2, 0, "Yazıcıları yenile");
        m.add(0, 4, 0, "Depolar arası sevk");
        m.add(0, 3, 0, "Çıkış");
        return true;
    }

    @Override public boolean onOptionsItemSelected(MenuItem i) {
        if (i.getItemId() == 1) degisenleriEkle();
        else if (i.getItemId() == 2) yazicilariYukle();
        else if (i.getItemId() == 4) startActivity(new Intent(this, SevkActivity.class));
        else if (i.getItemId() == 3) {
            api.cikis();
            startActivity(new Intent(this, GirisActivity.class));
            finish();
        }
        return true;
    }

    private void sorgula(final String kod) {
        if (kod.length() == 0) return;
        barkod.setText("");
        mesaj.setText("Sorgulanıyor…");
        mesaj.setTextColor(Ui.SOLUK);
        api.get("/api/stok/" + kodla(kod), new Api.Sonuc() {
            @Override public void tamam(String govde) {
                try {
                    JSONObject j = new JSONObject(govde);
                    urunGoster(j.getJSONObject("urun"), j.optJSONArray("miktarlar"));
                    mesaj.setText("");
                } catch (Exception e) {
                    hataGoster("Beklenmeyen cevap: " + e.getMessage());
                }
            }
            @Override public void hata(int durum, String m) {
                sonUrun = null;
                urunAdi.setText("");
                fiyat.setText("");
                ayrinti.setText("");
                stoklar.setText("");
                hataGoster(durum == 404 ? kod + ": ürün bulunamadı" : m);
                if (durum == 401) oturumBitti();
            }
        });
    }

    private void urunGoster(JSONObject u, JSONArray miktarlar) {
        sonUrun = u;
        urunAdi.setText(u.optString("urunAdi"));
        fiyat.setText(Ui.para(u.optDouble("fiyat", 0)) + " ₺");
        StringBuilder a = new StringBuilder(u.optString("barkod"));
        if (!u.optString("birim").isEmpty()) a.append(" · ").append(u.optString("birim"));
        String fd = tarih(u.optString("fiyatDegisimTarihi", null));
        if (fd != null) a.append(" · F.D ").append(fd);
        if (u.optDouble("eskiFiyat", 0) > 0 && u.optDouble("eskiFiyat") != u.optDouble("fiyat"))
            a.append(" · eski ").append(Ui.para(u.optDouble("eskiFiyat")));
        ayrinti.setText(a.toString());

        StringBuilder s = new StringBuilder();
        if (miktarlar != null)
            for (int i = 0; i < miktarlar.length(); i++) {
                JSONObject m = miktarlar.optJSONObject(i);
                if (s.length() > 0) s.append('\n');
                boolean benim = m.optInt("depoNo") == api.depoNo();
                s.append(benim ? "▶ " : "   ").append(m.optString("depoAdi").trim()).append(": ")
                        .append(Ui.miktar(m.optDouble("miktar", 0)));
            }
        stoklar.setText(s.toString());
    }

    private void adetSor() {
        if (sonUrun == null) { hataGoster("Önce bir ürün okutun."); return; }
        final EditText e = new EditText(this);
        e.setInputType(InputType.TYPE_CLASS_NUMBER);
        e.setText("1");
        e.selectAll();
        new AlertDialog.Builder(this).setTitle("Kaç etiket?").setView(e)
                .setPositiveButton("Ekle", new DialogInterface.OnClickListener() {
                    @Override public void onClick(DialogInterface d, int w) {
                        try { ekle(Math.max(1, Math.min(500, Integer.parseInt(e.getText().toString())))); }
                        catch (NumberFormatException ex) { hataGoster("Geçerli bir sayı girin."); }
                    }
                }).setNegativeButton("Vazgeç", null).show();
    }

    private void ekle(int adet) {
        if (sonUrun == null) { hataGoster("Önce bir ürün okutun."); return; }
        kalemEkle(sonUrun.optString("barkod"), sonUrun.optString("urunAdi"), adet);
        listeyiGoster();
        barkod.requestFocus();
    }

    private void kalemEkle(String bk, String ad, int adet) {
        Kalem k = kalemler.get(bk);
        if (k == null) { k = new Kalem(bk, ad); kalemler.put(bk, k); }
        k.adet = Math.min(500, k.adet + adet);
    }

    private void listeyiGoster() {
        if (kalemler.isEmpty()) {
            liste.setText("Liste boş.");
            basDugmesi.setText("Bas");
            basDugmesi.setEnabled(false);
            return;
        }
        StringBuilder s = new StringBuilder();
        int toplam = 0;
        for (Kalem k : kalemler.values()) {
            if (s.length() > 0) s.append('\n');
            s.append(k.adet).append(" × ").append(k.ad);
            toplam += k.adet;
        }
        liste.setText(s.toString());
        basDugmesi.setText(toplam + " etiket bas");
        basDugmesi.setEnabled(true);
    }

    private void degisenleriEkle() {
        mesaj.setText("Fiyatı değişenler alınıyor…");
        mesaj.setTextColor(Ui.SOLUK);
        api.get("/api/etiket/degisenler", new Api.Sonuc() {
            @Override public void tamam(String govde) {
                try {
                    JSONArray u = new JSONObject(govde).getJSONArray("urunler");
                    for (int i = 0; i < u.length(); i++) {
                        JSONObject o = u.getJSONObject(i);
                        if (!kalemler.containsKey(o.optString("barkod"))) kalemEkle(o.optString("barkod"), o.optString("urunAdi"), 1);
                    }
                    listeyiGoster();
                    bilgi(u.length() + " ürünün fiyatı bugün değişmiş; listeye eklendi.");
                } catch (Exception e) {
                    hataGoster("Beklenmeyen cevap: " + e.getMessage());
                }
            }
            @Override public void hata(int durum, String m) { hataGoster(m); }
        });
    }

    private void yazicilariYukle() {
        api.get("/api/etiket/yazicilar", new Api.Sonuc() {
            @Override public void tamam(String govde) {
                try {
                    JSONArray y = new JSONArray(govde);
                    List<String> adlar = new ArrayList<String>();
                    yaziciIdleri.clear();
                    int secili = 0;
                    for (int i = 0; i < y.length(); i++) {
                        JSONObject o = y.getJSONObject(i);
                        yaziciIdleri.add(o.getInt("id"));
                        adlar.add(o.optString("ad") + ("a4".equals(o.optString("tur")) ? " (A4)" : ""));
                        if (o.getInt("id") == api.sonYazici()) secili = i;
                    }
                    if (adlar.isEmpty()) adlar.add("Bu mağazada tanımlı yazıcı yok");
                    yaziciSecim.setAdapter(new ArrayAdapter<String>(AnaActivity.this,
                            android.R.layout.simple_spinner_dropdown_item, adlar));
                    yaziciSecim.setSelection(secili);
                } catch (Exception e) {
                    hataGoster("Yazıcı listesi okunamadı: " + e.getMessage());
                }
            }
            @Override public void hata(int durum, String m) {
                hataGoster("Yazıcılar alınamadı: " + m);
                if (durum == 401) oturumBitti();
            }
        });
    }

    private void bas() {
        int i = yaziciSecim.getSelectedItemPosition();
        if (i < 0 || i >= yaziciIdleri.size()) { hataGoster("Yazıcı seçin."); return; }
        final int yaziciId = yaziciIdleri.get(i);
        try {
            JSONObject g = new JSONObject();
            g.put("yaziciId", yaziciId);
            g.put("sablon", sablonSecim.getSelectedItemPosition() == 1 ? "manav" : "raf");
            JSONArray k = new JSONArray();
            for (Kalem x : kalemler.values()) k.put(new JSONObject().put("barkod", x.barkod).put("adet", x.adet));
            g.put("kalemler", k);
            basDugmesi.setEnabled(false);
            api.post("/api/etiket/bas", g, new Api.Sonuc() {
                @Override public void tamam(String govde) {
                    api.yaziciKaydet(yaziciId);
                    int basilacak = 0;
                    String bulunamayan = "";
                    try {
                        JSONObject r = new JSONObject(govde);
                        basilacak = r.optInt("basilacak");
                        JSONArray b = r.optJSONArray("bulunamayan");
                        if (b != null && b.length() > 0) bulunamayan = " Bulunamayan: " + b.join(", ").replace("\"", "");
                    } catch (Exception ignored) { }
                    kalemler.clear();
                    listeyiGoster();
                    bilgi(basilacak + " etiket yazıcıya gönderildi." + bulunamayan);
                    Toast.makeText(AnaActivity.this, "Gönderildi", Toast.LENGTH_SHORT).show();
                }
                @Override public void hata(int durum, String m) {
                    basDugmesi.setEnabled(true);
                    hataGoster(m);
                    if (durum == 401) oturumBitti();
                }
            });
        } catch (Exception e) {
            hataGoster(e.getMessage());
        }
    }

    private void oturumBitti() {
        api.cikis();
        Toast.makeText(this, "Oturum süresi doldu, tekrar giriş yapın.", Toast.LENGTH_LONG).show();
        startActivity(new Intent(this, GirisActivity.class));
        finish();
    }

    private void hataGoster(String m) { mesaj.setTextColor(Ui.HATA); mesaj.setText(m); }

    private void bilgi(String m) { mesaj.setTextColor(Ui.VURGU); mesaj.setText(m); }

    private static String kodla(String s) {
        try { return URLEncoder.encode(s, "UTF-8").replace("+", "%20"); } catch (Exception e) { return s; }
    }

    private static String tarih(String iso) {
        if (iso == null || iso.length() < 10 || "null".equals(iso)) return null;
        try {
            return new SimpleDateFormat("dd.MM.yyyy", Locale.US)
                    .format(new SimpleDateFormat("yyyy-MM-dd", Locale.US).parse(iso.substring(0, 10)));
        } catch (Exception e) { return null; }
    }
}
