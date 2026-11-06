package com.butcem.terminal;

import android.app.Activity;
import android.app.AlertDialog;
import android.text.Editable;
import android.text.TextWatcher;
import android.view.KeyEvent;
import android.view.View;
import android.widget.AdapterView;
import android.widget.ArrayAdapter;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ListView;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.List;

/** Cari seçme penceresi: ünvan ya da kodla merkezde arar (bağlantı gerekir). */
final class CariSecici {
    interface Secildi { void sec(String kod, String unvan); }

    private CariSecici() { }

    static void ac(final Activity a, String baslik, final Secildi cb) {
        final Api api = new Api(a);
        final LinearLayout k = Ui.dikey(a);
        int p = Ui.dp(a, 16);
        k.setPadding(p, p / 2, p, 0);
        final EditText ara = new EditText(a);
        ara.setHint("Ünvan ya da kodun bir parçası");
        ara.setSingleLine(true);
        k.addView(ara);
        final ListView lv = new ListView(a);
        k.addView(lv, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, Ui.dp(a, 300)));
        final List<String[]> bulunan = new ArrayList<String[]>();
        final ArrayAdapter<String> ad = new ArrayAdapter<String>(a, android.R.layout.simple_list_item_1, new ArrayList<String>());
        lv.setAdapter(ad);
        final AlertDialog d = new AlertDialog.Builder(a).setTitle(baslik).setView(k).setNegativeButton("Vazgeç", null).create();
        lv.setOnItemClickListener(new AdapterView.OnItemClickListener() {
            @Override public void onItemClick(AdapterView<?> x, View v, int i, long id) {
                if (i >= bulunan.size()) return;
                d.dismiss();
                cb.sec(bulunan.get(i)[0], bulunan.get(i)[1]);
            }
        });
        final Runnable getir = new Runnable() {
            @Override public void run() {
                String q = ara.getText().toString().trim();
                api.get("/api/cari?ilk=40" + (q.length() > 0 ? "&ara=" + EvrakActivity.kodla(q) : ""), new Api.Sonuc() {
                    @Override public void tamam(String govde) {
                        try {
                            JSONArray j = new JSONArray(govde);
                            bulunan.clear();
                            ad.clear();
                            for (int i = 0; i < j.length(); i++) {
                                JSONObject c = j.getJSONObject(i);
                                String kod = c.optString("kod").trim(), unvan = c.optString("unvan").trim();
                                bulunan.add(new String[]{kod, unvan});
                                ad.add(unvan + "  ·  " + kod);
                            }
                            if (j.length() == 0) ad.add("Bulunamadı");
                            ad.notifyDataSetChanged();
                        } catch (Exception e) { ad.clear(); ad.add(e.getMessage()); ad.notifyDataSetChanged(); }
                    }
                    @Override public void hata(int durum, String m) {
                        bulunan.clear();
                        ad.clear();
                        ad.add(durum == 0 ? "Merkeze ulaşılamıyor: cari seçmek için bağlantı gerekir." : m);
                        ad.notifyDataSetChanged();
                    }
                });
            }
        };
        ara.setOnEditorActionListener(new TextView.OnEditorActionListener() {
            @Override public boolean onEditorAction(TextView v, int id, KeyEvent e) { getir.run(); return true; }
        });
        ara.addTextChangedListener(new TextWatcher() {
            @Override public void beforeTextChanged(CharSequence s, int x, int c, int dd) { }
            @Override public void onTextChanged(CharSequence s, int x, int c, int dd) { }
            @Override public void afterTextChanged(Editable s) { if (s.length() == 0 || s.length() >= 2) getir.run(); }
        });
        d.show();
        getir.run();
    }
}
