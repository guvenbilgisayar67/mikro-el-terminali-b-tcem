package com.butcem.terminal;

import android.app.Activity;
import android.content.Intent;
import android.os.Bundle;
import android.text.InputType;
import android.view.View;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import org.json.JSONObject;

/** Sunucu adresi + kullanıcı girişi. Kullanıcılar web panelindeki Yönetim sayfasından açılır (Mikro kullanıcısı değil). */
public class GirisActivity extends Activity {
    private Api api;
    private EditText sunucu, kullanici, sifre;
    private TextView mesaj;
    private Button giris;

    @Override
    protected void onCreate(Bundle b) {
        super.onCreate(b);
        api = new Api(this);
        if (api.token() != null) {
            anaEkranaGec();
            return;
        }
        setTitle("Bütçem Terminal · Giriş");

        LinearLayout l = Ui.dikey(this);
        int p = Ui.dp(this, 20);
        l.setPadding(p, p, p, p);

        l.addView(Ui.yazi(this, "Sunucu adresi", 13, false));
        sunucu = new EditText(this);
        sunucu.setSingleLine(true);
        sunucu.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_URI);
        sunucu.setText(api.sunucu());
        l.addView(sunucu, Ui.genis(this, 0));

        l.addView(Ui.yazi(this, "Kullanıcı adı", 13, false), Ui.genis(this, 12));
        kullanici = new EditText(this);
        kullanici.setSingleLine(true);
        kullanici.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        l.addView(kullanici, Ui.genis(this, 0));

        l.addView(Ui.yazi(this, "Şifre", 13, false), Ui.genis(this, 12));
        sifre = new EditText(this);
        sifre.setSingleLine(true);
        sifre.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD);
        l.addView(sifre, Ui.genis(this, 0));

        mesaj = Ui.yazi(this, "", 14, false);
        mesaj.setTextColor(Ui.HATA);
        l.addView(mesaj, Ui.genis(this, 12));

        giris = Ui.dugme(this, "Giriş", true);
        giris.setOnClickListener(new View.OnClickListener() {
            @Override public void onClick(View v) { girisYap(); }
        });
        l.addView(giris, Ui.genis(this, 12));

        ScrollView s = new ScrollView(this);
        s.addView(l);
        setContentView(s);
    }

    private void girisYap() {
        String adres = sunucu.getText().toString().trim();
        if (!adres.startsWith("http")) adres = "http://" + adres;
        api.sunucuKaydet(adres);
        mesaj.setText("");
        giris.setEnabled(false);
        try {
            JSONObject g = new JSONObject();
            g.put("kullaniciAdi", kullanici.getText().toString().trim());
            g.put("sifre", sifre.getText().toString());
            api.post("/api/giris", g, new Api.Sonuc() {
                @Override public void tamam(String govde) {
                    try {
                        api.oturumKaydet(new JSONObject(govde));
                        anaEkranaGec();
                    } catch (Exception e) {
                        hataGoster("Beklenmeyen cevap: " + e.getMessage());
                    }
                }
                @Override public void hata(int durum, String m) {
                    hataGoster(durum == 401 ? "Kullanıcı adı ya da şifre hatalı." : m);
                }
            });
        } catch (Exception e) {
            hataGoster(e.getMessage());
        }
    }

    private void hataGoster(String m) {
        mesaj.setText(m);
        giris.setEnabled(true);
    }

    private void anaEkranaGec() {
        startActivity(new Intent(this, AnaActivity.class));
        finish();
    }
}
