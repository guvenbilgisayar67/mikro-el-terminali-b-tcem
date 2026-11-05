package com.butcem.terminal;

import android.app.Activity;
import android.content.Intent;
import android.graphics.Color;
import android.os.Bundle;
import android.view.Menu;
import android.view.MenuItem;
import android.view.View;
import android.widget.GridLayout;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import org.json.JSONObject;

/**
 * Ana menü: kutular. Alış ve Satış alt menü açar (AltMenuActivity); diğerleri doğrudan ekranı.
 * Kullanıcının yetkisi olmayan kutu görünmez. Oturum süresi dolmuşsa kayıtlı bilgilerle sessizce yeniden girilir.
 */
public class MenuActivity extends Activity implements Kuyruk.Dinleyici {
    private Api api;
    private TextView durum;
    private GridLayout izgara;

    @Override
    protected void onCreate(Bundle b) {
        super.onCreate(b);
        api = new Api(this);
        LinearLayout l = Ui.dikey(this);
        int p = Ui.dp(this, 14);
        l.setPadding(p, p, p, p);
        durum = Ui.yazi(this, "", 12, false);
        durum.setOnClickListener(new View.OnClickListener() {
            @Override public void onClick(View v) { Kuyruk.goster(MenuActivity.this); }
        });
        l.addView(durum, Ui.genis(this, 0));
        izgara = new GridLayout(this);
        izgara.setColumnCount(3);
        l.addView(izgara, Ui.genis(this, 10));
        ScrollView s = new ScrollView(this);
        s.addView(l);
        setContentView(s);
        if (Katalog.eski(this)) Katalog.indir(this, new Katalog.Bitti() {
            @Override public void sonuc(int d, String m) { if (d != -1) Toast.makeText(MenuActivity.this, m, Toast.LENGTH_SHORT).show(); }
        });
    }

    @Override protected void onResume() {
        super.onResume();
        if (api.token() == null) { girise(); return; }
        setTitle(api.adSoyad().length() > 0 ? api.adSoyad() : "Bütçem Terminal");
        kutulariKur();
        Kuyruk.baslat(this, this);
        durumGoster();
        // Yetkiler panelden değişmiş olabilir; oturum düştüyse kayıtlı bilgilerle yeniden gir.
        api.get("/api/ben", new Api.Sonuc() {
            @Override public void tamam(String govde) {
                try { api.oturumKaydet(new JSONObject(govde)); kutulariKur(); } catch (Exception ignored) { }
            }
            @Override public void hata(int d, String m) { if (d == 401) yenidenGir(); }
        });
    }

    @Override protected void onPause() {
        super.onPause();
        Kuyruk.durdur(this);
    }

    @Override public void degisti(String bilgi) {
        durumGoster();
        kutulariKur();
        if (bilgi != null) Toast.makeText(this, bilgi, Toast.LENGTH_LONG).show();
    }

    private void durumGoster() {
        durum.setText(Kuyruk.durumMetni(this));
        durum.setTextColor(Api.cevrimici ? Ui.SOLUK : Ui.HATA);
    }

    private void kutulariKur() {
        izgara.removeAllViews();
        int bekleyen = Yerel.al(this).bekleyenSayisi();
        if (api.yetkili("alis_fatura")) kutu("📥", Color.rgb(230, 244, 238), "Alış", null, new Runnable() {
            @Override public void run() { altMenu("alis"); }
        });
        if (api.yetkili("satis_fatura")) kutu("📤", Color.rgb(232, 240, 254), "Satış", null, new Runnable() {
            @Override public void run() { altMenu("satis"); }
        });
        if (api.yetkili("siparis_talep") || api.yetkili("siparis_onay")) kutu("🛒", Color.rgb(237, 233, 254), "Siparişler", null, new Runnable() {
            @Override public void run() { altMenu("siparis"); }
        });
        if (api.yetkili("sevk")) kutu("🚚", Color.rgb(255, 244, 229), "Depolar arası sevk", null, new Runnable() {
            @Override public void run() { startActivity(new Intent(MenuActivity.this, SevkActivity.class)); }
        });
        kutu("🔎", Color.rgb(243, 232, 255), "Fiyat sorgu", null, new Runnable() {
            @Override public void run() { startActivity(new Intent(MenuActivity.this, AnaActivity.class).putExtra("mod", "fiyat")); }
        });
        if (api.yetkili("etiket")) kutu("🏷️", Color.rgb(253, 232, 232), "Etiket", null, new Runnable() {
            @Override public void run() { startActivity(new Intent(MenuActivity.this, AnaActivity.class).putExtra("mod", "etiket")); }
        });
        if (api.yetkili("sayim")) kutu("📋", Color.rgb(224, 242, 254), "Sayım", null, new Runnable() {
            @Override public void run() { startActivity(new Intent(MenuActivity.this, SayimActivity.class)); }
        });
        kutu("⏳", Color.rgb(254, 249, 195), "Bekleyen işlemler", bekleyen > 0 ? String.valueOf(bekleyen) : null, new Runnable() {
            @Override public void run() { Kuyruk.goster(MenuActivity.this); }
        });
        kutu("⬆️", Color.rgb(220, 252, 231), "Gönder", null, new Runnable() {
            @Override public void run() {
                int n = Yerel.al(MenuActivity.this).bekleyenSayisi();
                if (n == 0) { Toast.makeText(MenuActivity.this, "Gönderilecek işlem yok.", Toast.LENGTH_SHORT).show(); return; }
                Toast.makeText(MenuActivity.this, n + " işlem gönderiliyor…", Toast.LENGTH_SHORT).show();
                Kuyruk.gonder();
            }
        });
        kutu("⚙️", Color.rgb(238, 241, 244), "Ayarlar", null, new Runnable() {
            @Override public void run() { AyarlarActivity.ac(MenuActivity.this); }
        });
    }

    private void kutu(String simge, int renk, String ad, String rozet, final Runnable tik) {
        LinearLayout k = Ui.karo(this, simge, renk, ad, rozet);
        k.setOnClickListener(new View.OnClickListener() {
            @Override public void onClick(View v) { tik.run(); }
        });
        int g = (getResources().getDisplayMetrics().widthPixels - Ui.dp(this, 28)) / 3;
        GridLayout.LayoutParams lp = new GridLayout.LayoutParams();
        int m = Ui.dp(this, 4);
        lp.width = g - 2 * m;
        lp.height = g - 2 * m;
        lp.setMargins(m, m, m, m);
        izgara.addView(k, lp);
    }

    private void altMenu(String grup) {
        startActivity(new Intent(this, AltMenuActivity.class).putExtra("grup", grup));
    }

    private void yenidenGir() {
        if (api.kullaniciAdi().length() == 0) { girise(); return; }
        api.girisYap(api.kullaniciAdi(), api.sifre(), new Api.Sonuc() {
            @Override public void tamam(String govde) {
                try { api.oturumKaydet(new JSONObject(govde)); kutulariKur(); } catch (Exception e) { girise(); }
            }
            @Override public void hata(int d, String m) { if (d == 401) { api.cikis(); girise(); } }
        });
    }

    private void girise() {
        startActivity(new Intent(this, GirisActivity.class));
        finish();
    }

    @Override public boolean onCreateOptionsMenu(Menu m) {
        m.add(0, 1, 0, "Ürün listesini güncelle");
        m.add(0, 2, 0, "Çıkış");
        return true;
    }

    @Override public boolean onOptionsItemSelected(MenuItem i) {
        if (i.getItemId() == 1) Katalog.indir(this, new Katalog.Bitti() {
            @Override public void sonuc(int d, String m) { Toast.makeText(MenuActivity.this, m, Toast.LENGTH_LONG).show(); }
        });
        else if (i.getItemId() == 2) { api.cikis(); girise(); }
        return true;
    }
}
