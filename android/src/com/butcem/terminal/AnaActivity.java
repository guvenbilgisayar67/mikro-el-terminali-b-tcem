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
 * "Listeye ekle" ile etiket listesi oluşur; seçilen mağaza yazıcısına panelde tasarlanan etiketlerle basılır.
 * Okuyucu iki şekilde çalışır: klavye modu (okutunca alana yazar + Enter) ya da iData yayın (broadcast) modu.
 * Bağlantı yokken ürün cihazdaki listeden gösterilir, basım isteği cihazda bekler ve bağlantı gelince gönderilir.
 */
public class AnaActivity extends Activity implements Kuyruk.Dinleyici {
    private static final String[] TARAYICI_YAYINLARI = {
            "android.intent.action.SCANRESULT",              // iData varsayılanı, ek: "value"
            "com.android.server.scannerservice.broadcast",   // eski iData / Urovo, ek: "scannerdata"
    };

    private Api api;
    private Yerel yerel;
    private int sorguNo;
    private EditText barkod;
    private TextView durum, urunAdi, fiyat, ayrinti, stoklar, liste, mesaj;
    private Spinner yaziciSecim, sablonSecim;
    private Button basDugmesi;
    private JSONObject sonUrun;
    private final Map<String, Kalem> kalemler = new LinkedHashMap<String, Kalem>();
    private final List<Integer> yaziciIdleri = new ArrayList<Integer>();
    private final List<String> yaziciAdlari = new ArrayList<String>();
    private final List<String> sablonKodlari = new ArrayList<String>();

    private static final class Kalem {
        final String barkod, ad;
        int adet;
        Kalem(String barkod, String ad) { this.barkod = barkod; this.ad = ad; }
    }

    private LinearLayout etiketBolumu;

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
        yerel = Yerel.al(this);
        setTitle("Bütçem · " + api.adSoyad());

        LinearLayout l = Ui.dikey(this);
        int p = Ui.dp(this, 14);
        l.setPadding(p, p, p, p);

        durum = Ui.yazi(this, "", 12, false);
        durum.setOnClickListener(new View.OnClickListener() {
            @Override public void onClick(View v) { Kuyruk.goster(AnaActivity.this); }
        });
        l.addView(durum, Ui.genis(this, 0));

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

        etiketBolumu = Ui.dikey(this);
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
        etiketBolumu.addView(ekleSatiri, Ui.genis(this, 10));

        View cizgi = new View(this);
        cizgi.setBackgroundColor(Color.rgb(226, 229, 234));
        LinearLayout.LayoutParams cp = Ui.genis(this, 14);
        cp.height = Ui.dp(this, 1);
        etiketBolumu.addView(cizgi, cp);

        etiketBolumu.addView(Ui.yazi(this, "Etiket listesi", 15, true), Ui.genis(this, 10));
        liste = Ui.yazi(this, "", 14, false);
        etiketBolumu.addView(liste, Ui.genis(this, 4));

        sablonSecim = new Spinner(this);
        etiketBolumu.addView(sablonSecim, Ui.genis(this, 8));

        yaziciSecim = new Spinner(this);
        etiketBolumu.addView(yaziciSecim, Ui.genis(this, 4));

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
        etiketBolumu.addView(basSatiri, Ui.genis(this, 8));

        l.addView(etiketBolumu);

        ScrollView s = new ScrollView(this);
        s.addView(l);
        setContentView(s);

        listeyiGoster();
        yetkileriUygula();
        if (Katalog.eski(this)) urunleriIndir(false);
        // Yetkiler panelden değişmiş olabilir; güncelini al.
        api.get("/api/ben", new Api.Sonuc() {
            @Override public void tamam(String govde) {
                try { api.oturumKaydet(new JSONObject(govde)); } catch (Exception e) { return; }
                yetkileriUygula();
            }
            @Override public void hata(int durum, String m) { if (durum == 401) oturumBitti(); }
        });
    }

    /** Etiket yetkisi yoksa terminal yalnız fiyat/stok sorgular; sevk menüsü sevk yetkisine bağlı. */
    private void yetkileriUygula() {
        boolean etiket = api.yetkili("etiket");
        etiketBolumu.setVisibility(etiket ? View.VISIBLE : View.GONE);
        if (etiket && yaziciSecim.getAdapter() == null) { yazicilariYukle(); sablonlariYukle(); }
        invalidateOptionsMenu();
    }

    @Override protected void onResume() {
        super.onResume();
        IntentFilter f = new IntentFilter();
        for (String a : TARAYICI_YAYINLARI) f.addAction(a);
        registerReceiver(tarayici, f);
        barkod.requestFocus();
        Kuyruk.baslat(this, this);
        durumGoster();
    }

    @Override protected void onPause() {
        super.onPause();
        unregisterReceiver(tarayici);
        Kuyruk.durdur(this);
    }

    @Override public void degisti(String bilgi) {
        durumGoster();
        if (bilgi != null) Toast.makeText(this, bilgi, Toast.LENGTH_LONG).show();
    }

    private void durumGoster() {
        durum.setText(Kuyruk.durumMetni(this));
        durum.setTextColor(Api.cevrimici ? Ui.SOLUK : Ui.HATA);
    }

    @Override public boolean onCreateOptionsMenu(Menu m) {
        if (api.yetkili("etiket")) {
            m.add(0, 1, 0, "Bugün fiyatı değişenleri ekle");
            m.add(0, 2, 0, "Yazıcıları yenile");
        }
        if (api.yetkili("sevk")) m.add(0, 4, 0, "Depolar arası sevk");
        if (api.yetkili("sayim")) m.add(0, 5, 0, "Sayım");
        m.add(0, 6, 0, "Bekleyen işlemler");
        m.add(0, 7, 0, "Ürün listesini güncelle");
        m.add(0, 3, 0, "Çıkış");
        return true;
    }

    @Override public boolean onOptionsItemSelected(MenuItem i) {
        if (i.getItemId() == 1) degisenleriEkle();
        else if (i.getItemId() == 2) { yazicilariYukle(); sablonlariYukle(); }
        else if (i.getItemId() == 6) Kuyruk.goster(this);
        else if (i.getItemId() == 7) urunleriIndir(true);
        else if (i.getItemId() == 4) startActivity(new Intent(this, SevkActivity.class));
        else if (i.getItemId() == 5) startActivity(new Intent(this, SayimActivity.class));
        else if (i.getItemId() == 3) {
            api.cikis();
            startActivity(new Intent(this, GirisActivity.class));
            finish();
        }
        return true;
    }

    /** Önce cihazdaki listeden hemen gösterir, sonra merkezden güncel fiyat ve tüm depoların stoğunu alır. */
    private void sorgula(final String kod) {
        if (kod.length() == 0) return;
        barkod.setText("");
        final int no = ++sorguNo;
        final Yerel.Urun y = yerel.urunBul(kod);
        if (y != null) {
            urunGoster(yereldenJson(y), null);
            stoklar.setText(y.stok == null ? "" : "▶ Bu depo: " + Ui.miktar(y.stok) + "  (liste " + Kuyruk.tarih(Katalog.zaman(this)) + ")");
            mesaj.setText("");
        } else {
            mesaj.setText("Sorgulanıyor…");
            mesaj.setTextColor(Ui.SOLUK);
        }
        api.get("/api/stok/" + kodla(kod), new Api.Sonuc() {
            @Override public void tamam(String govde) {
                durumGoster();
                if (no != sorguNo) return;
                try {
                    JSONObject j = new JSONObject(govde);
                    urunGoster(j.getJSONObject("urun"), j.optJSONArray("miktarlar"));
                    mesaj.setText("");
                } catch (Exception e) {
                    hataGoster("Beklenmeyen cevap: " + e.getMessage());
                }
            }
            @Override public void hata(int durum, String m) {
                durumGoster();
                if (no != sorguNo) return;
                if (durum == 0 && y != null) {
                    mesaj.setTextColor(Ui.SOLUK);
                    mesaj.setText("Çevrimdışı: fiyat ve stok cihazdaki listeden (" + Kuyruk.tarih(Katalog.zaman(AnaActivity.this)) + ").");
                    return;
                }
                sonUrun = null;
                urunAdi.setText("");
                fiyat.setText("");
                ayrinti.setText("");
                stoklar.setText("");
                hataGoster(durum == 404 ? kod + ": ürün bulunamadı"
                        : durum == 0 ? kod + ": merkeze ulaşılamıyor ve ürün cihazdaki listede yok" : m);
                if (durum == 401) oturumBitti();
            }
        });
    }

    private static JSONObject yereldenJson(Yerel.Urun y) {
        JSONObject o = new JSONObject();
        try {
            o.put("urunAdi", y.ad).put("barkod", y.barkod).put("birim", y.birim).put("fiyat", y.fiyat)
                    .put("fiyatDegisimTarihi", y.fd.length() == 0 ? JSONObject.NULL : y.fd);
        } catch (Exception ignored) { }
        return o;
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
            @Override public void hata(int durum, String m) {
                durumGoster();
                if (durum != 0) { hataGoster(m); return; }
                // Çevrimdışı: listenin indirildiği ana kadar bugün fiyatı değişenler.
                List<Yerel.Urun> l = yerel.fiyatiDegisenler(new SimpleDateFormat("yyyy-MM-dd", Locale.US).format(new java.util.Date()));
                for (Yerel.Urun y : l) if (!kalemler.containsKey(y.barkod)) kalemEkle(y.barkod, y.ad, 1);
                listeyiGoster();
                bilgi("Çevrimdışı: cihazdaki listeye göre bugün fiyatı değişen " + l.size() + " ürün eklendi ("
                        + Kuyruk.tarih(Katalog.zaman(AnaActivity.this)) + " itibarıyla).");
            }
        });
    }

    /** Yazıcı ve tasarım listeleri bağlantı yokken de seçilebilsin diye son hali saklanır. */
    private void yazicilariYukle() {
        api.get("/api/etiket/yazicilar", new Api.Sonuc() {
            @Override public void tamam(String govde) {
                saklananlar().edit().putString("yazicilar", govde).apply();
                yazicilariGoster(govde);
            }
            @Override public void hata(int durum, String m) {
                String eski = saklananlar().getString("yazicilar", null);
                if (durum == 0 && eski != null) { yazicilariGoster(eski); return; }
                hataGoster("Yazıcılar alınamadı: " + m);
                if (durum == 401) oturumBitti();
            }
        });
    }

    private void yazicilariGoster(String json) {
        try {
            JSONArray y = new JSONArray(json);
            List<String> adlar = new ArrayList<String>();
            yaziciIdleri.clear();
            yaziciAdlari.clear();
            int secili = 0;
            for (int i = 0; i < y.length(); i++) {
                JSONObject o = y.getJSONObject(i);
                yaziciIdleri.add(o.getInt("id"));
                yaziciAdlari.add(o.optString("ad"));
                adlar.add(o.optString("ad") + ("a4".equals(o.optString("tur")) ? " (A4)" : ""));
                if (o.getInt("id") == api.sonYazici()) secili = i;
            }
            if (adlar.isEmpty()) adlar.add("Bu mağazada tanımlı yazıcı yok");
            yaziciSecim.setAdapter(new ArrayAdapter<String>(this, android.R.layout.simple_spinner_dropdown_item, adlar));
            yaziciSecim.setSelection(secili);
        } catch (Exception e) {
            hataGoster("Yazıcı listesi okunamadı: " + e.getMessage());
        }
    }

    /** Panelin Etiket tasarımı ekranında kullanıma açık tasarımlar. */
    private void sablonlariYukle() {
        api.get("/api/etiket/sablonlar", new Api.Sonuc() {
            @Override public void tamam(String govde) {
                saklananlar().edit().putString("sablonlar", govde).apply();
                sablonlariGoster(govde);
            }
            @Override public void hata(int durum, String m) {
                sablonlariGoster(saklananlar().getString("sablonlar", "[{\"kod\":\"raf\",\"ad\":\"Raf etiketi\"},{\"kod\":\"manav\",\"ad\":\"Manav etiketi\"}]"));
            }
        });
    }

    private void sablonlariGoster(String json) {
        try {
            JSONArray a = new JSONArray(json);
            String onceki = sablonKodu();
            List<String> adlar = new ArrayList<String>();
            sablonKodlari.clear();
            int secili = 0;
            for (int i = 0; i < a.length(); i++) {
                JSONObject o = a.getJSONObject(i);
                sablonKodlari.add(o.optString("kod"));
                adlar.add(o.optString("ad"));
                if (o.optString("kod").equals(onceki)) secili = i;
            }
            sablonSecim.setAdapter(new ArrayAdapter<String>(this, android.R.layout.simple_spinner_dropdown_item, adlar));
            sablonSecim.setSelection(secili);
        } catch (Exception e) {
            hataGoster("Etiket tasarımları okunamadı: " + e.getMessage());
        }
    }

    private String sablonKodu() {
        int i = sablonSecim.getSelectedItemPosition();
        return i >= 0 && i < sablonKodlari.size() ? sablonKodlari.get(i) : "raf";
    }

    private android.content.SharedPreferences saklananlar() { return getSharedPreferences("butcem", MODE_PRIVATE); }

    private void urunleriIndir(final boolean elle) {
        if (elle) bilgi("Ürün listesi indiriliyor…");
        durum.setText("Ürün listesi indiriliyor…");
        Katalog.indir(this, new Katalog.Bitti() {
            @Override public void sonuc(int d, String m) {
                durumGoster();
                if (elle || d == -1) { if (d == 200) bilgi(m); else hataGoster(m); }
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
            g.put("sablon", sablonKodu());
            JSONArray k = new JSONArray();
            int toplam = 0;
            for (Kalem x : kalemler.values()) { k.put(new JSONObject().put("barkod", x.barkod).put("adet", x.adet)); toplam += x.adet; }
            g.put("kalemler", k);
            final String ozet = toplam + " etiket (" + sablonSecim.getSelectedItem() + ", " + yaziciAdlari.get(i) + ")";
            basDugmesi.setEnabled(false);
            Kuyruk.isle(this, "etiket", "/api/etiket/bas", g, ozet, new Api.Sonuc() {
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
                    if (Kuyruk.tekrarDenenir(durum)) {
                        // İstek cihazda; bağlantı (ya da yeniden giriş) olunca gönderilir.
                        api.yaziciKaydet(yaziciId);
                        kalemler.clear();
                        listeyiGoster();
                        bilgi("Merkeze ulaşılamadı. " + ozet + " cihazda bekliyor; bağlantı gelince basılacak.");
                        if (durum == 401) oturumBitti();
                        return;
                    }
                    basDugmesi.setEnabled(true);
                    hataGoster(m);
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
