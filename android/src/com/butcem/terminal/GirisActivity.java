package com.butcem.terminal;

import android.app.Activity;
import android.content.Intent;
import android.graphics.Color;
import android.graphics.drawable.GradientDrawable;
import android.os.Bundle;
import android.view.Gravity;
import android.view.Menu;
import android.view.MenuItem;
import android.view.View;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import org.json.JSONObject;

/**
 * Tek tuşla giriş. Sunucu adresi, kullanıcı adı ve şifre Ayarlar'da (yönetici şifresiyle korunur) cihaza kaydedilir;
 * personel yalnız "Giriş"e basar. Kullanıcılar web panelindeki Yönetim sayfasından açılır.
 */
public class GirisActivity extends Activity {
    private Api api;
    private TextView bilgi, mesaj;
    private Button giris;

    @Override
    protected void onCreate(Bundle b) {
        super.onCreate(b);
        api = new Api(this);
        if (api.token() != null) {
            menuyeGec();
            return;
        }
        setTitle("Bütçem Terminal");

        LinearLayout l = Ui.dikey(this);
        int p = Ui.dp(this, 20);
        l.setPadding(p, p, p, p);

        TextView logo = Ui.yazi(this, "B", 40, true);
        logo.setTextColor(Color.WHITE);
        logo.setGravity(Gravity.CENTER);
        GradientDrawable g = new GradientDrawable();
        g.setColor(Ui.VURGU);
        g.setCornerRadius(Ui.dp(this, 20));
        logo.setBackground(g);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(Ui.dp(this, 76), Ui.dp(this, 76));
        lp.gravity = Gravity.CENTER_HORIZONTAL;
        lp.topMargin = Ui.dp(this, 30);
        l.addView(logo, lp);

        TextView ad = Ui.yazi(this, "Bütçem Terminal", 20, true);
        ad.setGravity(Gravity.CENTER);
        l.addView(ad, Ui.genis(this, 8));

        bilgi = Ui.yazi(this, "", 14, false);
        bilgi.setBackground(Ui.kart(this));
        int ip = Ui.dp(this, 12);
        bilgi.setPadding(ip, ip, ip, ip);
        l.addView(bilgi, Ui.genis(this, 18));

        giris = Ui.dugme(this, "Giriş", true);
        giris.setTextSize(22);
        giris.setMinHeight(Ui.dp(this, 64));
        giris.setOnClickListener(new View.OnClickListener() {
            @Override public void onClick(View v) { girisYap(); }
        });
        l.addView(giris, Ui.genis(this, 14));

        mesaj = Ui.yazi(this, "", 14, false);
        mesaj.setTextColor(Ui.HATA);
        l.addView(mesaj, Ui.genis(this, 12));

        TextView ayarlar = Ui.yazi(this, "⚙ Ayarlar (sunucu, kullanıcı adı, şifre)", 14, false);
        ayarlar.setTextColor(Ui.VURGU);
        ayarlar.setGravity(Gravity.CENTER);
        ayarlar.setPadding(0, ip, 0, ip);
        ayarlar.setOnClickListener(new View.OnClickListener() {
            @Override public void onClick(View v) { AyarlarActivity.ac(GirisActivity.this); }
        });
        l.addView(ayarlar, Ui.genis(this, 24));

        ScrollView s = new ScrollView(this);
        s.addView(l);
        setContentView(s);
    }

    @Override protected void onResume() {
        super.onResume();
        if (bilgi == null) return;
        if (api.kullaniciAdi().length() == 0) {
            bilgi.setText("Giriş bilgileri henüz girilmemiş.\nAyarlar'dan sunucu adresi, kullanıcı adı ve şifreyi kaydedin.");
            giris.setEnabled(false);
        } else {
            String depo = api.adSoyad().length() > 0 ? api.adSoyad() + " · " : "";
            bilgi.setText(depo + api.kullaniciAdi() + "\nSunucu: " + api.sunucu().replace("http://", ""));
            giris.setEnabled(true);
        }
    }

    @Override public boolean onCreateOptionsMenu(Menu m) {
        m.add(0, 1, 0, "Ayarlar").setShowAsAction(MenuItem.SHOW_AS_ACTION_IF_ROOM);
        return true;
    }

    @Override public boolean onOptionsItemSelected(MenuItem i) {
        if (i.getItemId() == 1) AyarlarActivity.ac(this);
        return true;
    }

    private void girisYap() {
        mesaj.setText("");
        mesaj.setTextColor(Ui.SOLUK);
        mesaj.setText("Bağlanıyor…");
        giris.setEnabled(false);
        api.girisYap(api.kullaniciAdi(), api.sifre(), new Api.Sonuc() {
            @Override public void tamam(String govde) {
                try {
                    api.oturumKaydet(new JSONObject(govde));
                    menuyeGec();
                } catch (Exception e) {
                    hataGoster("Beklenmeyen cevap: " + e.getMessage());
                }
            }
            @Override public void hata(int durum, String m) {
                hataGoster(durum == 401 ? "Kullanıcı adı ya da şifre hatalı. Ayarlar'dan düzeltin." : m);
            }
        });
    }

    private void hataGoster(String m) {
        mesaj.setTextColor(Ui.HATA);
        mesaj.setText(m);
        giris.setEnabled(true);
    }

    private void menuyeGec() {
        startActivity(new Intent(this, MenuActivity.class));
        finish();
    }
}
