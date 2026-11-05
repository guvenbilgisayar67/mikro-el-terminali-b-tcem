package com.butcem.terminal;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.DialogInterface;
import android.os.Bundle;
import android.text.InputType;
import android.view.MenuItem;
import android.view.View;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONObject;

import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.Locale;

/**
 * Faturası henüz gelmemiş alış irsaliyeleri. Fatura gelince irsaliyeye dokunulur, fatura no yazılır;
 * Mikro'da irsaliye faturaya bağlanır (cariye alacak yazılır). Bağlantı gerekir.
 */
public class BekleyenIrsaliyeActivity extends Activity {
    private Api api;
    private LinearLayout liste;
    private TextView mesaj;

    @Override
    protected void onCreate(Bundle b) {
        super.onCreate(b);
        api = new Api(this);
        setTitle("Bekleyen irsaliye → fatura");
        if (getActionBar() != null) getActionBar().setDisplayHomeAsUpEnabled(true);
        LinearLayout l = Ui.dikey(this);
        int p = Ui.dp(this, 14);
        l.setPadding(p, p, p, p);
        TextView not = Ui.yazi(this, "Faturası gelen irsaliyeye dokunun ve fatura numarasını yazın.", 13, false);
        not.setTextColor(Ui.SOLUK);
        l.addView(not);
        mesaj = Ui.yazi(this, "Yükleniyor…", 14, false);
        l.addView(mesaj, Ui.genis(this, 8));
        liste = Ui.dikey(this);
        l.addView(liste, Ui.genis(this, 4));
        ScrollView s = new ScrollView(this);
        s.addView(l);
        setContentView(s);
        yukle();
    }

    private void yukle() {
        api.get("/api/fatura/bekleyen-irsaliyeler", new Api.Sonuc() {
            @Override public void tamam(String govde) {
                try {
                    JSONArray a = new JSONArray(govde);
                    liste.removeAllViews();
                    bilgi(a.length() == 0 ? "Faturası beklenen irsaliye yok." : a.length() + " irsaliye faturasını bekliyor.");
                    for (int i = 0; i < a.length(); i++) satir(a.getJSONObject(i));
                } catch (Exception e) { uyari(e.getMessage()); }
            }
            @Override public void hata(int d, String m) {
                uyari(d == 0 ? "Merkeze ulaşılamıyor: bu ekran bağlantı gerektirir." : m);
            }
        });
    }

    private void satir(final JSONObject o) {
        LinearLayout s = Ui.dikey(this);
        s.setBackground(Ui.kart(this));
        int p = Ui.dp(this, 10);
        s.setPadding(p, p, p, p);
        s.addView(Ui.yazi(this, o.optString("cariAdi").trim(), 15, true));
        String tarih = o.optString("tarih");
        if (tarih.length() >= 10) tarih = tarih.substring(8, 10) + "." + tarih.substring(5, 7) + "." + tarih.substring(0, 4);
        TextView alt = Ui.yazi(this, "İrsaliye " + o.optString("seri") + " " + o.optInt("sira") + " · belge " + o.optString("belgeNo")
                + " · " + tarih + "\n" + o.optInt("satirSayisi") + " satır · " + Ui.para(o.optDouble("araToplam")) + " ₺ (KDV hariç)", 13, false);
        alt.setTextColor(Ui.SOLUK);
        s.addView(alt);
        s.setOnClickListener(new View.OnClickListener() {
            @Override public void onClick(View v) { faturalastir(o); }
        });
        liste.addView(s, Ui.genis(this, 8));
    }

    private void faturalastir(final JSONObject o) {
        final EditText e = new EditText(this);
        e.setSingleLine(true);
        e.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_CAP_CHARACTERS);
        e.setHint("Fatura no");
        new AlertDialog.Builder(this).setTitle(o.optString("cariAdi").trim()).setMessage("Bu irsaliyenin faturası kaydedilsin mi?")
                .setView(e)
                .setPositiveButton("Fatura oluştur", new DialogInterface.OnClickListener() {
                    @Override public void onClick(DialogInterface d, int w) {
                        String no = e.getText().toString().trim();
                        if (no.length() == 0) { uyari("Fatura numarasını yazın."); return; }
                        try {
                            JSONObject g = new JSONObject().put("seri", o.optString("seri")).put("sira", o.optInt("sira")).put("belgeNo", no)
                                    .put("tarih", new SimpleDateFormat("yyyy-MM-dd", Locale.US).format(new Date()));
                            bilgi("Kaydediliyor…");
                            api.post("/api/fatura/alis/irsaliyeden", g, new Api.Sonuc() {
                                @Override public void tamam(String govde) {
                                    String r = "";
                                    try { JSONObject j = new JSONObject(govde); r = " (fatura " + j.optString("seri") + " " + j.optInt("sira") + ", toplam " + Ui.para(j.optDouble("toplam")) + " ₺)"; }
                                    catch (Exception ignored) { }
                                    yukle();
                                    bilgi("Fatura kaydedildi" + r + ".");
                                }
                                @Override public void hata(int d, String m) { uyari("Kaydedilemedi: " + m); }
                            });
                        } catch (Exception ex) { uyari(ex.getMessage()); }
                    }
                })
                .setNegativeButton("Vazgeç", null).show();
    }

    private void uyari(String m) { mesaj.setTextColor(Ui.HATA); mesaj.setText(m); }

    private void bilgi(String m) { mesaj.setTextColor(Ui.VURGU); mesaj.setText(m); }

    @Override public boolean onOptionsItemSelected(MenuItem i) {
        if (i.getItemId() == android.R.id.home) finish();
        return true;
    }
}
