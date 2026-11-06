package com.butcem.terminal;

import android.app.Activity;
import android.app.AlertDialog;
import android.graphics.Color;
import android.os.Bundle;
import android.view.MenuItem;
import android.view.View;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONObject;

/** Verilen siparişler: son 60 günün talepleri ve durumları (onay bekliyor / onaylandı / reddedildi). Bağlantı gerekir. */
public class SiparisListeActivity extends Activity {
    private Api api;
    private LinearLayout liste;
    private TextView mesaj;

    @Override
    protected void onCreate(Bundle b) {
        super.onCreate(b);
        api = new Api(this);
        setTitle("Verilen siparişler");
        if (getActionBar() != null) getActionBar().setDisplayHomeAsUpEnabled(true);
        LinearLayout l = Ui.dikey(this);
        int p = Ui.dp(this, 14);
        l.setPadding(p, p, p, p);
        mesaj = Ui.yazi(this, "Yükleniyor…", 14, false);
        l.addView(mesaj);
        liste = Ui.dikey(this);
        l.addView(liste, Ui.genis(this, 4));
        ScrollView s = new ScrollView(this);
        s.addView(l);
        setContentView(s);
    }

    @Override protected void onResume() {
        super.onResume();
        yukle();
    }

    private void yukle() {
        api.get("/api/siparis-talep", new Api.Sonuc() {
            @Override public void tamam(String govde) {
                try {
                    JSONArray a = new JSONArray(govde);
                    liste.removeAllViews();
                    yaz(Ui.SOLUK, a.length() == 0 ? "Son 60 günde verilen sipariş yok." : "Son 60 gün · ayrıntı için dokunun");
                    for (int i = 0; i < a.length(); i++) satir(a.getJSONObject(i));
                } catch (Exception e) { yaz(Ui.HATA, e.getMessage()); }
            }
            @Override public void hata(int d, String m) { yaz(Ui.HATA, d == 0 ? "Merkeze ulaşılamıyor: bu liste bağlantı gerektirir." : m); }
        });
    }

    static String durumAdi(String d) {
        if ("onaylandi".equals(d)) return "✓ Onaylandı, Mikro'da";
        if ("reddedildi".equals(d)) return "✕ Reddedildi";
        return "⏳ Onay bekliyor";
    }

    private static int durumRengi(String d) {
        if ("onaylandi".equals(d)) return Ui.VURGU;
        if ("reddedildi".equals(d)) return Ui.HATA;
        return Color.rgb(180, 120, 0);
    }

    private void satir(final JSONObject o) {
        LinearLayout s = Ui.dikey(this);
        s.setBackground(Ui.kart(this));
        int p = Ui.dp(this, 10);
        s.setPadding(p, p, p, p);
        String d = o.optString("durum");
        TextView ust = Ui.yazi(this, durumAdi(d), 15, true);
        ust.setTextColor(durumRengi(d));
        s.addView(ust);
        String tarih = o.optString("zaman");
        if (tarih.length() >= 16) tarih = tarih.substring(8, 10) + "." + tarih.substring(5, 7) + " " + tarih.substring(11, 16);
        StringBuilder alt = new StringBuilder("planli".equals(o.optString("tur")) ? "Planlı" : "Plansız")
                .append(" · talep ").append(o.optInt("id")).append(" · ").append(tarih).append(" · ").append(o.optInt("satirSayisi")).append(" ürün")
                .append("\n").append(o.optString("olusturan"));
        if (!o.isNull("evraklar")) alt.append("\nMikro: ").append(o.optString("evraklar"));
        if (!o.isNull("redSebebi") && o.optString("redSebebi").length() > 0) alt.append("\nSebep: ").append(o.optString("redSebebi"));
        TextView t = Ui.yazi(this, alt.toString(), 13, false);
        t.setTextColor(Ui.SOLUK);
        s.addView(t);
        s.setOnClickListener(new View.OnClickListener() {
            @Override public void onClick(View v) { detay(o.optInt("id")); }
        });
        liste.addView(s, Ui.genis(this, 8));
    }

    private void detay(final int id) {
        api.get("/api/siparis-talep/" + id, new Api.Sonuc() {
            @Override public void tamam(String govde) {
                try {
                    JSONObject r = new JSONObject(govde);
                    JSONArray a = r.getJSONArray("satirlar");
                    boolean bekliyor = "bekliyor".equals(r.getJSONObject("talep").optString("durum"));
                    StringBuilder m = new StringBuilder();
                    for (int i = 0; i < a.length(); i++) {
                        JSONObject x = a.getJSONObject(i);
                        m.append("• ").append(x.optString("urunAdi")).append("\n   istenen ").append(Ui.miktar(x.optDouble("istenenMiktar")))
                                .append(" ").append(x.optString("birim"));
                        if (!bekliyor && !x.isNull("onayMiktar")) m.append(" → onay ").append(Ui.miktar(x.optDouble("onayMiktar")));
                        if (x.optString("kaynakAd").length() > 0) m.append("\n   ").append(x.optString("kaynakAd"));
                        m.append("\n");
                    }
                    new AlertDialog.Builder(SiparisListeActivity.this).setTitle("Talep " + id + " · " + durumAdi(r.getJSONObject("talep").optString("durum")))
                            .setMessage(m.toString()).setPositiveButton("Tamam", null).show();
                } catch (Exception e) { yaz(Ui.HATA, e.getMessage()); }
            }
            @Override public void hata(int d, String m) { yaz(Ui.HATA, m); }
        });
    }

    private void yaz(int renk, String m) { mesaj.setTextColor(renk); mesaj.setText(m); }

    @Override public boolean onOptionsItemSelected(MenuItem i) {
        if (i.getItemId() == android.R.id.home) finish();
        return true;
    }
}
