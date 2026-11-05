package com.butcem.terminal;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Context;
import android.content.DialogInterface;
import android.content.Intent;
import android.os.Bundle;
import android.text.InputType;
import android.view.MenuItem;
import android.view.View;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

/**
 * Giriş bilgileri: sunucu adresi, kullanıcı adı, şifre. Cihaza kaydedilir; girişte personel yalnız "Giriş"e basar.
 * Ayarlar şifresi verilmişse bu ekran açılmadan önce sorulur (personel bilgileri değiştiremesin).
 */
public class AyarlarActivity extends Activity {
    private Api api;
    private EditText sunucu, kullanici, sifre, pin;
    private TextView mesaj;

    /** Ayarlar şifresi varsa sorar, doğruysa ekranı açar. */
    static void ac(final Activity a) {
        final Api api = new Api(a);
        if (api.ayarSifresi().length() == 0) {
            a.startActivity(new Intent(a, AyarlarActivity.class));
            return;
        }
        final EditText e = new EditText(a);
        e.setInputType(InputType.TYPE_CLASS_NUMBER | InputType.TYPE_NUMBER_VARIATION_PASSWORD);
        e.setHint("Ayarlar şifresi");
        new AlertDialog.Builder(a)
                .setTitle("Ayarlar")
                .setView(e)
                .setPositiveButton("Aç", new DialogInterface.OnClickListener() {
                    @Override public void onClick(DialogInterface d, int w) {
                        if (e.getText().toString().equals(api.ayarSifresi())) a.startActivity(new Intent(a, AyarlarActivity.class));
                        else Toast.makeText(a, "Ayarlar şifresi hatalı.", Toast.LENGTH_LONG).show();
                    }
                })
                .setNegativeButton("Vazgeç", null)
                .show();
    }

    @Override
    protected void onCreate(Bundle b) {
        super.onCreate(b);
        api = new Api(this);
        setTitle("Ayarlar");
        if (getActionBar() != null) getActionBar().setDisplayHomeAsUpEnabled(true);

        LinearLayout l = Ui.dikey(this);
        int p = Ui.dp(this, 20);
        l.setPadding(p, p, p, p);

        sunucu = alan(l, "Sunucu adresi", InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_URI, api.sunucu(), 0);
        kullanici = alan(l, "Kullanıcı adı", InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS, api.kullaniciAdi(), 12);
        sifre = alan(l, "Şifre", InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD, api.sifre(), 12);
        pin = alan(l, "Ayarlar şifresi (yönetici, rakam; boş bırakılırsa sorulmaz)",
                InputType.TYPE_CLASS_NUMBER | InputType.TYPE_NUMBER_VARIATION_PASSWORD, api.ayarSifresi(), 12);

        mesaj = Ui.yazi(this, "", 14, false);
        l.addView(mesaj, Ui.genis(this, 12));

        Button dene = Ui.dugme(this, "Bağlantıyı dene", false);
        dene.setOnClickListener(new View.OnClickListener() {
            @Override public void onClick(View v) { dene(); }
        });
        l.addView(dene, Ui.genis(this, 8));

        Button kaydet = Ui.dugme(this, "Kaydet", true);
        kaydet.setOnClickListener(new View.OnClickListener() {
            @Override public void onClick(View v) {
                kaydet();
                Toast.makeText(AyarlarActivity.this, "Kaydedildi.", Toast.LENGTH_SHORT).show();
                finish();
            }
        });
        l.addView(kaydet, Ui.genis(this, 8));

        TextView not = Ui.yazi(this, "Bilgiler yalnız bu cihazda saklanır. Kullanıcılar web panelinde Yönetim sayfasından açılır; "
                + "her terminale mağazasına bağlı ayrı bir kullanıcı önerilir.", 13, false);
        not.setTextColor(Ui.SOLUK);
        l.addView(not, Ui.genis(this, 14));

        ScrollView s = new ScrollView(this);
        s.addView(l);
        setContentView(s);
    }

    private EditText alan(LinearLayout l, String etiket, int tip, String deger, int bosluk) {
        l.addView(Ui.yazi(this, etiket, 13, false), Ui.genis(this, bosluk));
        EditText e = new EditText(this);
        e.setSingleLine(true);
        e.setInputType(tip);
        e.setText(deger);
        l.addView(e, Ui.genis(this, 0));
        return e;
    }

    private void kaydet() {
        String adres = sunucu.getText().toString().trim();
        if (adres.length() > 0 && !adres.startsWith("http")) adres = "http://" + adres;
        // Kullanıcı değiştiyse eski oturum geçersiz.
        if (!kullanici.getText().toString().trim().equals(api.kullaniciAdi())) api.cikis();
        api.sunucuKaydet(adres);
        api.girisBilgileriKaydet(kullanici.getText().toString().trim(), sifre.getText().toString(), pin.getText().toString().trim());
    }

    private void dene() {
        kaydet();
        mesaj.setTextColor(Ui.SOLUK);
        mesaj.setText("Deneniyor…");
        api.girisYap(api.kullaniciAdi(), api.sifre(), new Api.Sonuc() {
            @Override public void tamam(String govde) {
                mesaj.setTextColor(Ui.VURGU);
                mesaj.setText("Bağlantı ve giriş bilgileri doğru.");
            }
            @Override public void hata(int durum, String m) {
                mesaj.setTextColor(Ui.HATA);
                mesaj.setText(durum == 401 ? "Sunucuya ulaşıldı ama kullanıcı adı ya da şifre hatalı." : m);
            }
        });
    }

    @Override public boolean onOptionsItemSelected(MenuItem i) {
        if (i.getItemId() == android.R.id.home) finish();
        return true;
    }

    static boolean bilgiVar(Context c) { return new Api(c).kullaniciAdi().length() > 0; }
}
