package com.butcem.terminal;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.DialogInterface;
import android.content.Intent;
import android.content.IntentFilter;
import android.os.Bundle;
import android.text.InputType;
import android.view.KeyEvent;
import android.view.View;
import android.view.inputmethod.EditorInfo;
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
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * Depolar arası sevk: kendi deponuzdan başka depoya gönderilecek ürünleri okutun, miktarları düzeltin, kaydedin.
 * Kayıt Mikro'da depolar arası sevk fişi olarak oluşur (seri: deponun serisi).
 */
public class SevkActivity extends Activity {
    private Api api;
    private EditText barkod;
    private TextView mesaj, ozet;
    private LinearLayout liste;
    private Spinner cikisSecim, girisSecim;
    private Button kaydetDugmesi;
    private final List<Integer> depoNolari = new ArrayList<Integer>();
    private final Map<String, Kalem> kalemler = new LinkedHashMap<String, Kalem>();
    private boolean kaydediliyor;

    private static final class Kalem {
        final String kod, ad, birim;
        final double birimFiyat;
        double miktar;
        Kalem(String kod, String ad, String birim, double birimFiyat) { this.kod = kod; this.ad = ad; this.birim = birim; this.birimFiyat = birimFiyat; }
    }

    private final BroadcastReceiver tarayici = new BroadcastReceiver() {
        @Override public void onReceive(Context c, Intent i) {
            String v = i.getStringExtra("value");
            if (v == null) v = i.getStringExtra("scannerdata");
            if (v == null) v = i.getStringExtra("barcode_string");
            if (v != null && v.trim().length() > 0) okut(v.trim());
        }
    };

    @Override
    protected void onCreate(Bundle b) {
        super.onCreate(b);
        api = new Api(this);
        setTitle("Depolar arası sevk");

        LinearLayout l = Ui.dikey(this);
        int p = Ui.dp(this, 14);
        l.setPadding(p, p, p, p);

        l.addView(Ui.yazi(this, "Çıkış deposu", 13, false));
        cikisSecim = new Spinner(this);
        l.addView(cikisSecim, Ui.genis(this, 2));
        l.addView(Ui.yazi(this, "Giriş deposu (gönderilecek yer)", 13, false), Ui.genis(this, 8));
        girisSecim = new Spinner(this);
        l.addView(girisSecim, Ui.genis(this, 2));

        LinearLayout satir = Ui.yatay(this);
        barkod = new EditText(this);
        barkod.setHint("Barkod okutun");
        barkod.setSingleLine(true);
        barkod.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        barkod.setImeOptions(EditorInfo.IME_ACTION_DONE);
        barkod.setOnEditorActionListener(new TextView.OnEditorActionListener() {
            @Override public boolean onEditorAction(TextView v, int id, KeyEvent e) {
                boolean enter = e != null && e.getKeyCode() == KeyEvent.KEYCODE_ENTER;
                if (id == EditorInfo.IME_ACTION_DONE || id == EditorInfo.IME_ACTION_SEARCH || enter) {
                    if (e == null || e.getAction() == KeyEvent.ACTION_DOWN) okut(barkod.getText().toString().trim());
                    return true;
                }
                return false;
            }
        });
        satir.addView(barkod, Ui.agirlik(this, 1, 0));
        Button ekle = Ui.dugme(this, "Ekle", false);
        ekle.setOnClickListener(new View.OnClickListener() {
            @Override public void onClick(View v) { okut(barkod.getText().toString().trim()); }
        });
        satir.addView(ekle, Ui.agirlik(this, 0, 8));
        l.addView(satir, Ui.genis(this, 10));

        mesaj = Ui.yazi(this, "", 14, false);
        l.addView(mesaj, Ui.genis(this, 4));

        l.addView(Ui.yazi(this, "Gönderilecekler (miktarı değiştirmek için dokunun)", 13, false), Ui.genis(this, 10));
        liste = Ui.dikey(this);
        l.addView(liste, Ui.genis(this, 4));
        ozet = Ui.yazi(this, "", 14, true);
        l.addView(ozet, Ui.genis(this, 8));

        kaydetDugmesi = Ui.dugme(this, "Sevki kaydet", true);
        kaydetDugmesi.setOnClickListener(new View.OnClickListener() {
            @Override public void onClick(View v) { onayla(); }
        });
        l.addView(kaydetDugmesi, Ui.genis(this, 10));

        ScrollView sv = new ScrollView(this);
        sv.addView(l);
        setContentView(sv);
        depolariYukle();
        listeyiGoster();
    }

    @Override protected void onResume() {
        super.onResume();
        IntentFilter f = new IntentFilter();
        f.addAction("android.intent.action.SCANRESULT");
        f.addAction("com.android.server.scannerservice.broadcast");
        registerReceiver(tarayici, f);
        barkod.requestFocus();
    }

    @Override protected void onPause() {
        super.onPause();
        unregisterReceiver(tarayici);
    }

    private void depolariYukle() {
        api.get("/api/depolar", new Api.Sonuc() {
            @Override public void tamam(String govde) {
                try {
                    JSONArray a = new JSONArray(govde);
                    List<String> adlar = new ArrayList<String>();
                    depoNolari.clear();
                    int kendi = -1;
                    for (int i = 0; i < a.length(); i++) {
                        JSONObject d = a.getJSONObject(i);
                        depoNolari.add(d.optInt("no"));
                        adlar.add(d.optInt("no") + " · " + d.optString("ad").trim());
                        if (d.optInt("no") == api.depoNo()) kendi = i;
                    }
                    ArrayAdapter<String> ad1 = new ArrayAdapter<String>(SevkActivity.this, android.R.layout.simple_spinner_dropdown_item, adlar);
                    ArrayAdapter<String> ad2 = new ArrayAdapter<String>(SevkActivity.this, android.R.layout.simple_spinner_dropdown_item, adlar);
                    cikisSecim.setAdapter(ad1);
                    girisSecim.setAdapter(ad2);
                    // Mağaza kullanıcısı yalnız kendi deposundan gönderir.
                    if (kendi >= 0) { cikisSecim.setSelection(kendi); cikisSecim.setEnabled(false); girisSecim.setSelection(kendi == 0 ? 1 : 0); }
                } catch (Exception e) {
                    hataGoster("Depolar okunamadı: " + e.getMessage());
                }
            }
            @Override public void hata(int durum, String m) { hataGoster(m); }
        });
    }

    private void okut(final String kod) {
        if (kod.length() == 0) return;
        barkod.setText("");
        bilgi("Aranıyor…");
        api.get("/api/sevk/urun?k=" + kodla(kod), new Api.Sonuc() {
            @Override public void tamam(String govde) {
                try {
                    JSONObject u = new JSONObject(govde);
                    String k = u.optString("kod");
                    Kalem x = kalemler.get(k);
                    if (x == null) {
                        x = new Kalem(k, u.optString("ad"), u.optString("birim"), u.optDouble("birimFiyat", 0));
                        kalemler.put(k, x);
                    }
                    x.miktar += 1;
                    bilgi(Ui.miktar(x.miktar) + " × " + x.ad);
                    listeyiGoster();
                } catch (Exception e) {
                    hataGoster("Beklenmeyen cevap: " + e.getMessage());
                }
            }
            @Override public void hata(int durum, String m) { hataGoster(kod + ": " + m); }
        });
    }

    private void listeyiGoster() {
        liste.removeAllViews();
        double toplam = 0;
        for (final Kalem k : kalemler.values()) {
            TextView t = Ui.yazi(this, Ui.miktar(k.miktar) + " " + k.birim + "  ·  " + k.ad, 15, false);
            int p = Ui.dp(this, 8);
            t.setPadding(0, p, 0, p);
            t.setOnClickListener(new View.OnClickListener() {
                @Override public void onClick(View v) { miktarSor(k); }
            });
            liste.addView(t);
            toplam += k.miktar * k.birimFiyat;
        }
        if (kalemler.isEmpty()) liste.addView(Ui.yazi(this, "Liste boş.", 14, false));
        ozet.setText(kalemler.size() + " ürün · maliyet " + Ui.para(toplam) + " ₺");
        kaydetDugmesi.setEnabled(!kalemler.isEmpty() && !kaydediliyor);
    }

    private void miktarSor(final Kalem k) {
        final EditText e = new EditText(this);
        e.setInputType(InputType.TYPE_CLASS_NUMBER | InputType.TYPE_NUMBER_FLAG_DECIMAL);
        e.setText(Ui.miktar(k.miktar).replace(".", "").replace(",", "."));
        e.selectAll();
        new AlertDialog.Builder(this).setTitle(k.ad).setMessage("Miktar (" + k.birim + ")").setView(e)
                .setPositiveButton("Tamam", new DialogInterface.OnClickListener() {
                    @Override public void onClick(DialogInterface d, int w) {
                        try {
                            double m = Double.parseDouble(e.getText().toString().replace(",", "."));
                            if (m <= 0) kalemler.remove(k.kod); else k.miktar = m;
                            listeyiGoster();
                        } catch (NumberFormatException ex) { hataGoster("Geçerli bir miktar girin."); }
                    }
                })
                .setNeutralButton("Listeden çıkar", new DialogInterface.OnClickListener() {
                    @Override public void onClick(DialogInterface d, int w) { kalemler.remove(k.kod); listeyiGoster(); }
                })
                .setNegativeButton("Vazgeç", null).show();
    }

    private void onayla() {
        if (depoNolari.isEmpty() || kalemler.isEmpty()) return;
        final int cikis = depoNolari.get(cikisSecim.getSelectedItemPosition());
        final int giris = depoNolari.get(girisSecim.getSelectedItemPosition());
        if (cikis == giris) { hataGoster("Çıkış ve giriş deposu aynı olamaz."); return; }
        new AlertDialog.Builder(this).setTitle("Sevki kaydet")
                .setMessage(kalemler.size() + " ürün " + cikisSecim.getSelectedItem() + " deposundan " + girisSecim.getSelectedItem() + " deposuna gönderilecek. Mikro'ya kaydedilsin mi?")
                .setPositiveButton("Kaydet", new DialogInterface.OnClickListener() {
                    @Override public void onClick(DialogInterface d, int w) { kaydet(cikis, giris); }
                })
                .setNegativeButton("Vazgeç", null).show();
    }

    private void kaydet(int cikis, int giris) {
        try {
            JSONArray satirlar = new JSONArray();
            for (Kalem k : kalemler.values()) satirlar.put(new JSONObject().put("urun", k.kod).put("miktar", k.miktar));
            JSONObject o = new JSONObject().put("cikisDepo", cikis).put("girisDepo", giris).put("satirlar", satirlar);
            kaydediliyor = true;
            listeyiGoster();
            bilgi("Kaydediliyor…");
            api.post("/api/sevk", o, new Api.Sonuc() {
                @Override public void tamam(String govde) {
                    kaydediliyor = false;
                    try {
                        JSONObject r = new JSONObject(govde);
                        kalemler.clear();
                        listeyiGoster();
                        bilgi("Kaydedildi: sevk fişi " + r.optString("seri") + " " + r.optInt("sira") + ", " + r.optInt("satirSayisi") + " satır.");
                    } catch (Exception e) { hataGoster(e.getMessage()); }
                }
                @Override public void hata(int durum, String m) {
                    kaydediliyor = false;
                    listeyiGoster();
                    hataGoster("Kaydedilemedi: " + m);
                }
            });
        } catch (Exception e) {
            hataGoster(e.getMessage());
        }
    }

    private void hataGoster(String m) { mesaj.setTextColor(Ui.HATA); mesaj.setText(m); }

    private void bilgi(String m) { mesaj.setTextColor(Ui.VURGU); mesaj.setText(m); }

    private static String kodla(String s) {
        try { return URLEncoder.encode(s, "UTF-8").replace("+", "%20"); } catch (Exception e) { return s; }
    }
}
