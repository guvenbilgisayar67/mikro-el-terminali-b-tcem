package com.butcem.terminal;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.DialogInterface;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.SharedPreferences;
import android.os.Bundle;
import android.text.Editable;
import android.text.InputType;
import android.text.TextWatcher;
import android.view.KeyEvent;
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
import java.util.Date;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;

/**
 * Evrak girişi: üstte başlık (cari, depo, belge no), altta okutulan ürün satırları (kod, ad, birim, miktar, fiyat).
 * Alış faturası ve toptan alış irsaliyesinde fiyat son alış fiyatıdır (KDV hariç); satış faturasında satış
 * listesindeki fiyattır (KDV dahil). Satıra dokunarak miktar ve fiyat değiştirilir.
 * Kayıt bağlantı yokken cihazda bekler, gelince Mikro'ya gönderilir (merkez aynı evrakı iki kez yazmaz).
 */
public class EvrakActivity extends Activity implements Kuyruk.Dinleyici {
    static final String ALIS_FATURASI = "alis_faturasi";
    static final String ALIS_IRSALIYESI = "alis_irsaliyesi";
    static final String SATIS_FATURASI = "satis_faturasi";

    private Api api;
    private Yerel yerel;
    private String tur;
    private boolean alis;
    private TextView durum, cariYazi, mesaj, ozet;
    private EditText belgeNo, barkod;
    private Spinner depoSecim;
    private LinearLayout liste;
    private Button kaydetDugmesi;
    private String cariKod, cariAdi;
    private final List<Integer> depoNolari = new ArrayList<Integer>();
    private final Map<String, Kalem> kalemler = new LinkedHashMap<String, Kalem>();
    private boolean kaydediliyor;

    private static final class Kalem {
        final String kod, ad, birim;
        double miktar;
        double fiyat = -1;   // bilinmiyorsa -1: kaydetmeden önce yazılmalı
        Kalem(String kod, String ad, String birim) { this.kod = kod; this.ad = ad; this.birim = birim; }
    }

    private final BroadcastReceiver tarayici = new BroadcastReceiver() {
        @Override public void onReceive(Context c, Intent i) {
            String v = i.getStringExtra("value");
            if (v == null) v = i.getStringExtra("scannerdata");
            if (v == null) v = i.getStringExtra("barcode_string");
            if (v != null && v.trim().length() > 0) okut(v.trim());
        }
    };

    static String ad(String tur) {
        if (ALIS_FATURASI.equals(tur)) return "Alış faturası";
        if (ALIS_IRSALIYESI.equals(tur)) return "Toptan alış irsaliyesi";
        if (SATIS_FATURASI.equals(tur)) return "Satış faturası";
        return "Evrak";
    }

    private String yol() {
        if (ALIS_FATURASI.equals(tur)) return "/api/fatura/alis";
        if (ALIS_IRSALIYESI.equals(tur)) return "/api/fatura/alis-irsaliyesi";
        return "/api/fatura/satis";
    }

    @Override
    protected void onCreate(Bundle b) {
        super.onCreate(b);
        api = new Api(this);
        yerel = Yerel.al(this);
        tur = getIntent().getStringExtra("tur");
        if (tur == null) tur = SATIS_FATURASI;
        alis = !SATIS_FATURASI.equals(tur);
        setTitle(ad(tur));
        if (getActionBar() != null) getActionBar().setDisplayHomeAsUpEnabled(true);

        LinearLayout l = Ui.dikey(this);
        int p = Ui.dp(this, 14);
        l.setPadding(p, p, p, p);

        durum = Ui.yazi(this, "", 12, false);
        durum.setOnClickListener(new View.OnClickListener() {
            @Override public void onClick(View v) { Kuyruk.goster(EvrakActivity.this); }
        });
        l.addView(durum, Ui.genis(this, 0));

        // --- Başlık ---
        LinearLayout baslik = Ui.dikey(this);
        baslik.setBackground(Ui.kart(this));
        int ip = Ui.dp(this, 10);
        baslik.setPadding(ip, ip, ip, ip);
        baslik.addView(Ui.yazi(this, alis ? "Tedarikçi (cari)" : "Müşteri (cari)", 13, false));
        cariYazi = Ui.yazi(this, "Seçmek için dokunun", 16, true);
        cariYazi.setTextColor(Ui.VURGU);
        cariYazi.setPadding(0, Ui.dp(this, 6), 0, Ui.dp(this, 6));
        cariYazi.setOnClickListener(new View.OnClickListener() {
            @Override public void onClick(View v) { cariSec(); }
        });
        baslik.addView(cariYazi);
        baslik.addView(Ui.yazi(this, "Depo", 13, false), Ui.genis(this, 4));
        depoSecim = new Spinner(this);
        baslik.addView(depoSecim);
        if (alis) {
            baslik.addView(Ui.yazi(this, ALIS_FATURASI.equals(tur) ? "Fatura no (zorunlu)" : "İrsaliye no", 13, false), Ui.genis(this, 4));
            belgeNo = new EditText(this);
            belgeNo.setSingleLine(true);
            belgeNo.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_CAP_CHARACTERS | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
            belgeNo.addTextChangedListener(new TextWatcher() {
                @Override public void beforeTextChanged(CharSequence s, int a, int c, int d) { }
                @Override public void onTextChanged(CharSequence s, int a, int c, int d) { }
                @Override public void afterTextChanged(Editable s) { dugmeyiAyarla(); }
            });
            baslik.addView(belgeNo);
        }
        l.addView(baslik, Ui.genis(this, 6));

        // --- Satır girişi ---
        LinearLayout satir = Ui.yatay(this);
        barkod = new EditText(this);
        barkod.setHint("Barkod okutun / stok kodu");
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

        l.addView(Ui.yazi(this, "Satırlar (miktar / fiyat değiştirmek için dokunun) · fiyat "
                + (alis ? "KDV hariç" : "KDV dahil"), 13, false), Ui.genis(this, 10));
        liste = Ui.dikey(this);
        l.addView(liste, Ui.genis(this, 4));
        ozet = Ui.yazi(this, "", 15, true);
        l.addView(ozet, Ui.genis(this, 8));

        kaydetDugmesi = Ui.dugme(this, ad(tur) + " kaydet", true);
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

    // --- Cari ---

    private void cariSec() {
        CariSecici.ac(this, alis ? "Tedarikçi seç" : "Müşteri seç", new CariSecici.Secildi() {
            @Override public void sec(String kod, String unvan) {
                cariKod = kod;
                cariAdi = unvan;
                cariYazi.setText(unvan + "\n" + kod);
                cariYazi.setTextColor(0xFF1F2933);
                dugmeyiAyarla();
            }
        });
    }

    // --- Depo ---

    private void depolariYukle() {
        final SharedPreferences ayar = getSharedPreferences("butcem", MODE_PRIVATE);
        String eski = ayar.getString("depolar", null);
        if (eski != null) depolariGoster(eski);
        api.get("/api/depolar", new Api.Sonuc() {
            @Override public void tamam(String govde) {
                ayar.edit().putString("depolar", govde).apply();
                depolariGoster(govde);
            }
            @Override public void hata(int d, String m) { durumGoster(); }
        });
    }

    private void depolariGoster(String govde) {
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
            depoSecim.setAdapter(new ArrayAdapter<String>(this, android.R.layout.simple_spinner_dropdown_item, adlar));
            // Mağaza kullanıcısı yalnız kendi deposuna evrak keser.
            if (kendi >= 0) { depoSecim.setSelection(kendi); depoSecim.setEnabled(false); }
        } catch (Exception e) {
            hataGoster("Depolar okunamadı: " + e.getMessage());
        }
    }

    // --- Satırlar ---

    private void okut(final String kod) {
        if (kod.length() == 0) return;
        barkod.setText("");
        Yerel.Urun y = yerel.urunBul(kod);
        if (y != null) {
            Kalem x = ekle(y.kod, y.ad, y.birim);
            if (x.fiyat < 0) {
                if (!alis && y.fiyat > 0) { x.fiyat = y.fiyat; listeyiGoster(); }
                else fiyatAl(y.kod, x);
            }
            return;
        }
        bilgi("Aranıyor…");
        api.get("/api/fatura/urun?k=" + kodla(kod), new Api.Sonuc() {
            @Override public void tamam(String govde) {
                durumGoster();
                try {
                    JSONObject u = new JSONObject(govde);
                    Kalem x = ekle(u.optString("kod"), u.optString("ad"), u.optString("birim"));
                    if (x.fiyat < 0) {
                        if (alis) { x.fiyat = u.optDouble("sonAlis", 0); listeyiGoster(); }
                        else fiyatAl(x.kod, x);
                    }
                } catch (Exception e) { hataGoster("Beklenmeyen cevap: " + e.getMessage()); }
            }
            @Override public void hata(int d, String m) {
                durumGoster();
                hataGoster(kod + ": " + (d == 0 ? "merkeze ulaşılamıyor ve ürün cihazdaki listede yok" : m));
            }
        });
    }

    private Kalem ekle(String kod, String ad, String birim) {
        Kalem x = kalemler.get(kod);
        if (x == null) { x = new Kalem(kod, ad, birim); kalemler.put(kod, x); }
        x.miktar += 1;
        bilgi(Ui.miktar(x.miktar) + " × " + x.ad);
        listeyiGoster();
        return x;
    }

    /** Alışta son alış fiyatı, satışta satış listesi fiyatı. Bağlantı yoksa fiyat satıra dokunularak yazılır. */
    private void fiyatAl(String kod, final Kalem x) {
        String yol = alis ? "/api/fatura/urun?k=" + kodla(kod) : "/api/stok/" + kodla(kod) + "?miktar=false";
        api.get(yol, new Api.Sonuc() {
            @Override public void tamam(String govde) {
                try {
                    JSONObject o = new JSONObject(govde);
                    x.fiyat = alis ? o.optDouble("sonAlis", 0) : o.getJSONObject("urun").optDouble("fiyat", 0);
                    listeyiGoster();
                } catch (Exception ignored) { }
            }
            @Override public void hata(int d, String m) { durumGoster(); }
        });
    }

    private void listeyiGoster() {
        liste.removeAllViews();
        double toplam = 0;
        boolean eksik = false;
        int no = 0;
        for (final Kalem k : kalemler.values()) {
            no++;
            LinearLayout s = Ui.dikey(this);
            int p = Ui.dp(this, 8);
            s.setPadding(0, p, 0, p);
            s.addView(Ui.yazi(this, no + ". " + k.ad, 15, true));
            String fiyat = k.fiyat < 0 ? "fiyat yok, dokunup yazın" : Ui.para(k.fiyat) + " ₺  =  " + Ui.para(k.miktar * k.fiyat) + " ₺";
            TextView alt = Ui.yazi(this, k.kod + "  ·  " + Ui.miktar(k.miktar) + " " + k.birim + "  ×  " + fiyat, 13, false);
            alt.setTextColor(k.fiyat < 0 ? Ui.HATA : Ui.SOLUK);
            s.addView(alt);
            s.setOnClickListener(new View.OnClickListener() {
                @Override public void onClick(View v) { satirDuzenle(k); }
            });
            liste.addView(s);
            if (k.fiyat < 0) eksik = true; else toplam += k.miktar * k.fiyat;
        }
        if (kalemler.isEmpty()) liste.addView(Ui.yazi(this, "Henüz ürün okutulmadı.", 14, false));
        ozet.setText(kalemler.size() + " satır · toplam " + Ui.para(toplam) + " ₺ (" + (alis ? "KDV hariç" : "KDV dahil") + ")"
                + (eksik ? " · fiyatı eksik satır var" : ""));
        dugmeyiAyarla();
    }

    private boolean fiyatEksik() {
        for (Kalem k : kalemler.values()) if (k.fiyat < 0) return true;
        return false;
    }

    private void dugmeyiAyarla() {
        boolean belge = !ALIS_FATURASI.equals(tur) || (belgeNo != null && belgeNo.getText().toString().trim().length() > 0);
        kaydetDugmesi.setEnabled(!kaydediliyor && !kalemler.isEmpty() && cariKod != null && belge && !fiyatEksik());
    }

    private void satirDuzenle(final Kalem k) {
        LinearLayout f = Ui.dikey(this);
        int p = Ui.dp(this, 20);
        f.setPadding(p, p / 2, p, 0);
        f.addView(Ui.yazi(this, "Miktar (" + k.birim + ")", 13, false));
        final EditText m = sayiAlani(k.miktar);
        f.addView(m);
        f.addView(Ui.yazi(this, "Birim fiyat (" + (alis ? "KDV hariç" : "KDV dahil") + ")", 13, false), Ui.genis(this, 8));
        final EditText fy = sayiAlani(k.fiyat < 0 ? -1 : k.fiyat);
        f.addView(fy);
        new AlertDialog.Builder(this).setTitle(k.ad).setView(f)
                .setPositiveButton("Tamam", new DialogInterface.OnClickListener() {
                    @Override public void onClick(DialogInterface d, int w) {
                        try {
                            double yeniMiktar = Double.parseDouble(m.getText().toString().replace(",", "."));
                            String fs = fy.getText().toString().replace(",", ".").trim();
                            if (fs.length() > 0) {
                                double yeniFiyat = Double.parseDouble(fs);
                                if (yeniFiyat < 0) throw new NumberFormatException();
                                k.fiyat = yeniFiyat;
                            }
                            if (yeniMiktar <= 0) kalemler.remove(k.kod); else k.miktar = yeniMiktar;
                            listeyiGoster();
                        } catch (NumberFormatException ex) { hataGoster("Geçerli bir sayı girin."); }
                    }
                })
                .setNeutralButton("Satırı sil", new DialogInterface.OnClickListener() {
                    @Override public void onClick(DialogInterface d, int w) { kalemler.remove(k.kod); listeyiGoster(); }
                })
                .setNegativeButton("Vazgeç", null).show();
    }

    private EditText sayiAlani(double deger) {
        EditText e = new EditText(this);
        e.setSingleLine(true);
        e.setInputType(InputType.TYPE_CLASS_NUMBER | InputType.TYPE_NUMBER_FLAG_DECIMAL);
        if (deger >= 0) e.setText(String.valueOf(Math.round(deger * 10000) / 10000.0).replaceAll("\\.0$", ""));
        e.selectAll();
        return e;
    }

    // --- Kayıt ---

    private void onayla() {
        if (depoNolari.isEmpty()) { hataGoster("Depo listesi yok: bir kez bağlantı varken bu ekranı açın."); return; }
        new AlertDialog.Builder(this).setTitle(ad(tur))
                .setMessage(cariAdi + "\n" + depoSecim.getSelectedItem() + "\n" + ozet.getText() + "\n\nMikro'ya kaydedilsin mi?")
                .setPositiveButton("Kaydet", new DialogInterface.OnClickListener() {
                    @Override public void onClick(DialogInterface d, int w) { kaydet(); }
                })
                .setNegativeButton("Vazgeç", null).show();
    }

    private void kaydet() {
        try {
            JSONArray satirlar = new JSONArray();
            for (Kalem k : kalemler.values())
                satirlar.put(new JSONObject().put("urun", k.kod).put("miktar", k.miktar).put("birimFiyat", k.fiyat).put("kdvDahil", !alis));
            // Tarih cihazdan: çevrimdışı kaydedilen evrak sonra gönderilse de kaydedildiği güne yazılır.
            String tarih = new SimpleDateFormat("yyyy-MM-dd", Locale.US).format(new Date());
            int depo = depoNolari.get(depoSecim.getSelectedItemPosition());
            JSONObject o = new JSONObject().put("cariKod", cariKod).put("depoNo", depo).put("tarih", tarih).put("satirlar", satirlar);
            if (belgeNo != null && belgeNo.getText().toString().trim().length() > 0) {
                o.put("belgeNo", belgeNo.getText().toString().trim());
                o.put("belgeTarih", tarih);
            }
            final String ozetMetni = ad(tur) + ", " + cariAdi + ", " + kalemler.size() + " satır";
            kaydediliyor = true;
            dugmeyiAyarla();
            bilgi("Kaydediliyor…");
            Kuyruk.isle(this, tur, yol(), o, ozetMetni, new Api.Sonuc() {
                @Override public void tamam(String govde) {
                    kaydediliyor = false;
                    temizle();
                    bilgi("Kaydedildi: " + Kuyruk.sonucMetni(tur, govde) + ".");
                }
                @Override public void hata(int d, String m) {
                    kaydediliyor = false;
                    if (Kuyruk.tekrarDenenir(d)) {
                        temizle();
                        bilgi("Merkeze ulaşılamadı. Evrak cihazda kaydedildi; bağlantı gelince ya da Gönder'e basınca Mikro'ya gönderilecek.");
                        return;
                    }
                    dugmeyiAyarla();
                    hataGoster("Kaydedilemedi: " + m);
                }
            });
        } catch (Exception e) {
            kaydediliyor = false;
            hataGoster(e.getMessage());
        }
    }

    private void temizle() {
        kalemler.clear();
        if (belgeNo != null) belgeNo.setText("");
        listeyiGoster();
    }

    private void cik() {
        if (kalemler.isEmpty()) { finish(); return; }
        new AlertDialog.Builder(this).setTitle("Çıkılsın mı?")
                .setMessage("Kaydedilmemiş " + kalemler.size() + " satır silinecek.")
                .setPositiveButton("Çık", new DialogInterface.OnClickListener() {
                    @Override public void onClick(DialogInterface d, int w) { finish(); }
                })
                .setNegativeButton("Vazgeç", null).show();
    }

    @Override public void onBackPressed() { cik(); }

    @Override public boolean onOptionsItemSelected(MenuItem i) {
        if (i.getItemId() == android.R.id.home) cik();
        return true;
    }

    private void hataGoster(String m) { mesaj.setTextColor(Ui.HATA); mesaj.setText(m); }

    private void bilgi(String m) { mesaj.setTextColor(Ui.VURGU); mesaj.setText(m); }

    static String kodla(String s) {
        try { return URLEncoder.encode(s, "UTF-8").replace("+", "%20"); } catch (Exception e) { return s; }
    }
}
