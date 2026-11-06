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
import android.text.InputType;
import android.view.KeyEvent;
import android.view.MenuItem;
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
import android.widget.Toast;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * Sipariş talebi. Talep merkezde patronun onayını bekler; patron miktarları düzeltip onaylayınca Mikro'ya yazılır.
 * Planlı: mağazanın satışına göre öneri listesi gelir (her ürün son geldiği tedarikçiye / depoya), miktarlar düzeltilir.
 * Plansız: tedarikçi ya da depo seçilir, ürünler okutulur.
 */
public class SiparisActivity extends Activity implements Kuyruk.Dinleyici {
    static final String PLANLI = "planli", PLANSIZ = "plansiz";

    private Api api;
    private Yerel yerel;
    private boolean planli;
    private TextView durum, mesaj, ozet, kimeYazi;
    private EditText barkod, not;
    private Spinner depoSecim, kaynakDepoSecim;
    private LinearLayout liste;
    private Button gonderDugmesi;
    private final List<Integer> depoNolari = new ArrayList<Integer>();
    private final List<String> depoAdlari = new ArrayList<String>();
    private final Map<String, Kalem> kalemler = new LinkedHashMap<String, Kalem>();
    private String kaynakTur = "tedarikci", kaynakKod, kaynakAd;   // plansız
    private boolean gonderiliyor, depoHazir;

    private static final class Kalem {
        final String kod, ad, birim;
        String kaynakTur = "", kaynakKod = "", kaynakAd = "";
        double miktar, oneri = -1, stok = Double.NaN;
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

    @Override
    protected void onCreate(Bundle b) {
        super.onCreate(b);
        api = new Api(this);
        yerel = Yerel.al(this);
        planli = PLANLI.equals(getIntent().getStringExtra("tur"));
        setTitle(planli ? "Planlı sipariş" : "Plansız sipariş");
        if (getActionBar() != null) getActionBar().setDisplayHomeAsUpEnabled(true);

        LinearLayout l = Ui.dikey(this);
        int p = Ui.dp(this, 14);
        l.setPadding(p, p, p, p);
        durum = Ui.yazi(this, "", 12, false);
        durum.setOnClickListener(new View.OnClickListener() {
            @Override public void onClick(View v) { Kuyruk.goster(SiparisActivity.this); }
        });
        l.addView(durum);

        LinearLayout baslik = Ui.dikey(this);
        baslik.setBackground(Ui.kart(this));
        int ip = Ui.dp(this, 10);
        baslik.setPadding(ip, ip, ip, ip);
        baslik.addView(Ui.yazi(this, "Siparişi veren mağaza / depo", 13, false));
        depoSecim = new Spinner(this);
        baslik.addView(depoSecim);
        if (!planli) {
            baslik.addView(Ui.yazi(this, "Kime", 13, false), Ui.genis(this, 6));
            final Spinner kime = new Spinner(this);
            kime.setAdapter(new ArrayAdapter<String>(this, android.R.layout.simple_spinner_dropdown_item, new String[]{"Tedarikçiye", "Depoya (depolar arası)"}));
            baslik.addView(kime);
            kimeYazi = Ui.yazi(this, "Tedarikçi seçmek için dokunun", 16, true);
            kimeYazi.setTextColor(Ui.VURGU);
            kimeYazi.setPadding(0, Ui.dp(this, 6), 0, Ui.dp(this, 6));
            kimeYazi.setOnClickListener(new View.OnClickListener() {
                @Override public void onClick(View v) { tedarikciSec(); }
            });
            baslik.addView(kimeYazi);
            kaynakDepoSecim = new Spinner(this);
            kaynakDepoSecim.setVisibility(View.GONE);
            baslik.addView(kaynakDepoSecim);
            kime.setOnItemSelectedListener(new AdapterView.OnItemSelectedListener() {
                @Override public void onItemSelected(AdapterView<?> a, View v, int i, long id) {
                    kaynakTur = i == 0 ? "tedarikci" : "depo";
                    kimeYazi.setVisibility(i == 0 ? View.VISIBLE : View.GONE);
                    kaynakDepoSecim.setVisibility(i == 0 ? View.GONE : View.VISIBLE);
                    dugmeyiAyarla();
                }
                @Override public void onNothingSelected(AdapterView<?> a) { }
            });
        }
        l.addView(baslik, Ui.genis(this, 6));

        if (planli) {
            Button hesapla = Ui.dugme(this, "Öneri listesini getir", false);
            hesapla.setOnClickListener(new View.OnClickListener() {
                @Override public void onClick(View v) { oneriGetir(); }
            });
            l.addView(hesapla, Ui.genis(this, 8));
        }

        LinearLayout satir = Ui.yatay(this);
        barkod = new EditText(this);
        barkod.setHint(planli ? "Listede olmayan ürünü okutun" : "Barkod okutun / stok kodu");
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
        l.addView(Ui.yazi(this, "Ürünler (miktarı değiştirmek için dokunun)", 13, false), Ui.genis(this, 10));
        liste = Ui.dikey(this);
        l.addView(liste, Ui.genis(this, 4));
        ozet = Ui.yazi(this, "", 15, true);
        l.addView(ozet, Ui.genis(this, 8));

        not = new EditText(this);
        not.setHint("Not (isteğe bağlı, patron görür)");
        not.setSingleLine(true);
        l.addView(not, Ui.genis(this, 8));

        gonderDugmesi = Ui.dugme(this, "Onaya gönder", true);
        gonderDugmesi.setOnClickListener(new View.OnClickListener() {
            @Override public void onClick(View v) { onayla(); }
        });
        l.addView(gonderDugmesi, Ui.genis(this, 10));
        TextView aciklama = Ui.yazi(this, "Sipariş patron onayından sonra Mikro'ya aktarılır. Durumunu Siparişler → Verilen siparişler'den izleyin.", 12, false);
        aciklama.setTextColor(Ui.SOLUK);
        l.addView(aciklama, Ui.genis(this, 8));

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

    // --- Depolar ---

    private void depolariYukle() {
        final SharedPreferences ayar = getSharedPreferences("butcem", MODE_PRIVATE);
        String eski = ayar.getString("depolar", null);
        if (eski != null) depolariGoster(eski);
        api.get("/api/depolar", new Api.Sonuc() {
            @Override public void tamam(String govde) {
                ayar.edit().putString("depolar", govde).apply();
                if (!depoHazir) depolariGoster(govde);
            }
            @Override public void hata(int d, String m) { durumGoster(); }
        });
    }

    private void depolariGoster(String govde) {
        try {
            JSONArray a = new JSONArray(govde);
            depoNolari.clear();
            depoAdlari.clear();
            int kendi = -1;
            for (int i = 0; i < a.length(); i++) {
                JSONObject d = a.getJSONObject(i);
                depoNolari.add(d.optInt("no"));
                depoAdlari.add(d.optInt("no") + " · " + d.optString("ad").trim());
                if (d.optInt("no") == api.depoNo()) kendi = i;
            }
            depoSecim.setAdapter(new ArrayAdapter<String>(this, android.R.layout.simple_spinner_dropdown_item, depoAdlari));
            if (kendi >= 0) { depoSecim.setSelection(kendi); depoSecim.setEnabled(false); }
            if (kaynakDepoSecim != null)
                kaynakDepoSecim.setAdapter(new ArrayAdapter<String>(this, android.R.layout.simple_spinner_dropdown_item, depoAdlari));
            depoHazir = true;
            dugmeyiAyarla();
        } catch (Exception e) { hataGoster("Depolar okunamadı: " + e.getMessage()); }
    }

    private int depo() { return depoNolari.isEmpty() ? api.depoNo() : depoNolari.get(depoSecim.getSelectedItemPosition()); }

    private void tedarikciSec() {
        CariSecici.ac(this, "Tedarikçi seç", new CariSecici.Secildi() {
            @Override public void sec(String kod, String unvan) {
                kaynakKod = kod;
                kaynakAd = unvan;
                kimeYazi.setText(unvan + "\n" + kod);
                kimeYazi.setTextColor(0xFF1F2933);
                dugmeyiAyarla();
            }
        });
    }

    // --- Ürünler ---

    private void oneriGetir() {
        if (!kalemler.isEmpty()) {
            new AlertDialog.Builder(this).setTitle("Öneri listesi").setMessage("Listedeki " + kalemler.size() + " ürün silinip öneri yeniden getirilsin mi?")
                    .setPositiveButton("Getir", new DialogInterface.OnClickListener() {
                        @Override public void onClick(DialogInterface d, int w) { kalemler.clear(); oneriGetirOnayli(); }
                    })
                    .setNegativeButton("Vazgeç", null).show();
            return;
        }
        oneriGetirOnayli();
    }

    private void oneriGetirOnayli() {
        bilgi("Öneri hesaplanıyor…");
        api.get("/api/siparis-talep/oneri?depo=" + depo(), new Api.Sonuc() {
            @Override public void tamam(String govde) {
                durumGoster();
                try {
                    JSONArray a = new JSONArray(govde);
                    for (int i = 0; i < a.length(); i++) {
                        Kalem k = kalemOku(a.getJSONObject(i));
                        k.miktar = k.oneri;
                        kalemler.put(k.kod, k);
                    }
                    listeyiGoster();
                    bilgi(a.length() == 0 ? "Sipariş gereken ürün çıkmadı." : a.length() + " ürün önerildi. Miktarları düzeltin, gerekmeyeni 0 yapın.");
                } catch (Exception e) { hataGoster("Beklenmeyen cevap: " + e.getMessage()); }
            }
            @Override public void hata(int d, String m) {
                durumGoster();
                hataGoster(d == 0 ? "Merkeze ulaşılamıyor: öneri listesi için bağlantı gerekir." : m);
            }
        });
    }

    private static Kalem kalemOku(JSONObject o) {
        Kalem k = new Kalem(o.optString("kod"), o.optString("ad"), o.optString("birim"));
        k.kaynakTur = o.optString("kaynakTur");
        k.kaynakKod = o.optString("kaynakKod");
        k.kaynakAd = o.optString("kaynakAd");
        k.oneri = o.optDouble("miktar", 0);
        k.stok = o.optDouble("stok", Double.NaN);
        return k;
    }

    private void okut(final String kod) {
        if (kod.length() == 0) return;
        barkod.setText("");
        if (!planli) {
            // Plansızda kaynak başlıktan gelir; ürün cihazdaki listeden de bulunabilir (çevrimdışı).
            Yerel.Urun y = yerel.urunBul(kod);
            if (y != null) { arttir(new Kalem(y.kod, y.ad, y.birim)); return; }
        }
        bilgi("Aranıyor…");
        api.get("/api/siparis-talep/urun?depo=" + depo() + "&k=" + EvrakActivity.kodla(kod), new Api.Sonuc() {
            @Override public void tamam(String govde) {
                durumGoster();
                try { arttir(kalemOku(new JSONObject(govde))); }
                catch (Exception e) { hataGoster("Beklenmeyen cevap: " + e.getMessage()); }
            }
            @Override public void hata(int d, String m) {
                durumGoster();
                hataGoster(kod + ": " + (d == 0 ? "merkeze ulaşılamıyor ve ürün cihazdaki listede yok" : m));
            }
        });
    }

    private void arttir(Kalem yeni) {
        Kalem x = kalemler.get(yeni.kod);
        if (x == null) { x = yeni; x.miktar = 0; kalemler.put(x.kod, x); }
        x.miktar += 1;
        bilgi(Ui.miktar(x.miktar) + " × " + x.ad);
        listeyiGoster();
    }

    private void listeyiGoster() {
        liste.removeAllViews();
        int gidecek = 0;
        String sonKaynak = null;
        List<Kalem> sirali = new ArrayList<Kalem>(kalemler.values());
        if (planli) java.util.Collections.sort(sirali, new java.util.Comparator<Kalem>() {
            @Override public int compare(Kalem a, Kalem b) { return a.kaynakAd.compareTo(b.kaynakAd); }
        });
        for (final Kalem k : sirali) {
            if (planli && !k.kaynakAd.equals(sonKaynak)) {
                sonKaynak = k.kaynakAd;
                TextView g = Ui.yazi(this, k.kaynakAd.length() > 0 ? k.kaynakAd : "Kaynağı bilinmeyen (patron seçer)", 14, true);
                g.setTextColor(Ui.VURGU);
                liste.addView(g, Ui.genis(this, 10));
            }
            LinearLayout s = Ui.dikey(this);
            int p = Ui.dp(this, 7);
            s.setPadding(0, p, 0, p);
            s.addView(Ui.yazi(this, k.ad, 15, k.miktar > 0));
            StringBuilder alt = new StringBuilder(k.kod).append("  ·  sipariş ").append(Ui.miktar(k.miktar)).append(" ").append(k.birim);
            if (k.oneri >= 0) alt.append("  ·  öneri ").append(Ui.miktar(k.oneri));
            if (!Double.isNaN(k.stok)) alt.append("  ·  stok ").append(Ui.miktar(k.stok));
            TextView t = Ui.yazi(this, alt.toString(), 13, false);
            t.setTextColor(k.miktar > 0 ? Ui.SOLUK : Ui.HATA);
            s.addView(t);
            s.setOnClickListener(new View.OnClickListener() {
                @Override public void onClick(View v) { miktarSor(k); }
            });
            liste.addView(s);
            if (k.miktar > 0) gidecek++;
        }
        if (kalemler.isEmpty()) liste.addView(Ui.yazi(this, planli ? "Öneri listesini getirin ya da ürün okutun." : "Henüz ürün okutulmadı.", 14, false));
        ozet.setText(gidecek + " ürün siparişe gidecek" + (kalemler.size() > gidecek ? " (" + (kalemler.size() - gidecek) + " ürün 0)" : ""));
        dugmeyiAyarla();
    }

    private void miktarSor(final Kalem k) {
        final EditText e = new EditText(this);
        e.setInputType(InputType.TYPE_CLASS_NUMBER | InputType.TYPE_NUMBER_FLAG_DECIMAL);
        e.setText(String.valueOf(k.miktar).replaceAll("\\.0$", ""));
        e.selectAll();
        new AlertDialog.Builder(this).setTitle(k.ad).setMessage("Sipariş miktarı (" + k.birim + "), 0: sipariş verme").setView(e)
                .setPositiveButton("Tamam", new DialogInterface.OnClickListener() {
                    @Override public void onClick(DialogInterface d, int w) {
                        try {
                            double m = Double.parseDouble(e.getText().toString().replace(",", "."));
                            if (m < 0) throw new NumberFormatException();
                            k.miktar = m;
                            listeyiGoster();
                        } catch (NumberFormatException ex) { hataGoster("Geçerli bir miktar girin."); }
                    }
                })
                .setNeutralButton("Listeden çıkar", new DialogInterface.OnClickListener() {
                    @Override public void onClick(DialogInterface d, int w) { kalemler.remove(k.kod); listeyiGoster(); }
                })
                .setNegativeButton("Vazgeç", null).show();
    }

    private int gidecekSayisi() {
        int n = 0;
        for (Kalem k : kalemler.values()) if (k.miktar > 0) n++;
        return n;
    }

    private boolean kaynakTamam() {
        if (planli) return true;
        if ("depo".equals(kaynakTur)) return !depoNolari.isEmpty() && depoNolari.get(kaynakDepoSecim.getSelectedItemPosition()) != depo();
        return kaynakKod != null;
    }

    private void dugmeyiAyarla() {
        if (gonderDugmesi != null) gonderDugmesi.setEnabled(!gonderiliyor && depoHazir && gidecekSayisi() > 0 && kaynakTamam());
    }

    // --- Gönderim ---

    private void onayla() {
        String kime = planli ? "öneriye göre tedarikçilere / depolara"
                : "depo".equals(kaynakTur) ? kaynakDepoSecim.getSelectedItem() + " deposuna" : kaynakAd + " firmasına";
        new AlertDialog.Builder(this).setTitle("Onaya gönder")
                .setMessage(gidecekSayisi() + " ürün, " + kime + ". Patron onayından sonra Mikro'ya aktarılacak. Gönderilsin mi?")
                .setPositiveButton("Gönder", new DialogInterface.OnClickListener() {
                    @Override public void onClick(DialogInterface d, int w) { gonder(); }
                })
                .setNegativeButton("Vazgeç", null).show();
    }

    private void gonder() {
        try {
            JSONArray satirlar = new JSONArray();
            for (Kalem k : kalemler.values()) {
                if (k.miktar <= 0) continue;
                JSONObject s = new JSONObject().put("urun", k.kod).put("miktar", k.miktar);
                if (planli) {
                    if (k.kaynakTur.length() > 0) s.put("kaynakTur", k.kaynakTur).put("kaynakKod", k.kaynakKod);
                    if (k.oneri >= 0) s.put("oneri", k.oneri);
                }
                satirlar.put(s);
            }
            JSONObject o = new JSONObject().put("tur", planli ? PLANLI : PLANSIZ).put("depo", depo())
                    .put("not", not.getText().toString().trim()).put("satirlar", satirlar);
            if (!planli) {
                o.put("kaynakTur", kaynakTur);
                o.put("kaynakKod", "depo".equals(kaynakTur) ? String.valueOf(depoNolari.get(kaynakDepoSecim.getSelectedItemPosition())) : kaynakKod);
            }
            final String ozetMetni = (planli ? "Planlı" : "Plansız") + " sipariş, " + satirlar.length() + " ürün";
            gonderiliyor = true;
            dugmeyiAyarla();
            bilgi("Gönderiliyor…");
            Kuyruk.isle(this, "siparis_talep", "/api/siparis-talep", o, ozetMetni, new Api.Sonuc() {
                @Override public void tamam(String govde) {
                    gonderiliyor = false;
                    temizle();
                    bilgi("Sipariş patron onayına gönderildi (" + Kuyruk.sonucMetni("siparis_talep", govde) + ").");
                }
                @Override public void hata(int d, String m) {
                    gonderiliyor = false;
                    if (Kuyruk.tekrarDenenir(d)) {
                        temizle();
                        bilgi("Merkeze ulaşılamadı. Sipariş cihazda kaydedildi; bağlantı gelince ya da Gönder'e basınca onaya gider.");
                        return;
                    }
                    dugmeyiAyarla();
                    hataGoster("Gönderilemedi: " + m);
                }
            });
        } catch (Exception e) {
            gonderiliyor = false;
            hataGoster(e.getMessage());
        }
    }

    private void temizle() {
        kalemler.clear();
        not.setText("");
        listeyiGoster();
    }

    private void cik() {
        if (kalemler.isEmpty()) { finish(); return; }
        new AlertDialog.Builder(this).setTitle("Çıkılsın mı?").setMessage("Gönderilmemiş " + kalemler.size() + " ürün silinecek.")
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
}
