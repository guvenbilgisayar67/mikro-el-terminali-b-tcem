package com.butcem.terminal;

import android.app.Activity;
import android.content.Intent;
import android.graphics.Color;
import android.os.Bundle;
import android.view.MenuItem;
import android.view.View;
import android.widget.GridLayout;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Toast;

/**
 * Alış ve Satış kutularının alt menüsü. Yeni evrak türü eklemek için ALTLAR'a bir satır eklemek yeterli:
 * { grup, ad, simge, evrak türü (EvrakActivity'deki tür) }. Tür boşsa kutu "hazırlanıyor" der.
 */
public class AltMenuActivity extends Activity {
    private static final String[][] ALTLAR = {
            { "alis", "Alış faturası", "🧾", EvrakActivity.ALIS_FATURASI },
            { "alis", "Toptan alış irsaliyesi", "📦", EvrakActivity.ALIS_IRSALIYESI },
            { "alis", "Bekleyen irsaliye → fatura", "🔗", "bekleyen" },
            { "satis", "Satış faturası", "🧾", EvrakActivity.SATIS_FATURASI },
            { "satis", "Toptan satış irsaliyesi", "📦", "" },
            { "siparis", "Planlı sipariş", "📊", "siparis_planli" },
            { "siparis", "Plansız sipariş", "✍️", "siparis_plansiz" },
            { "siparis", "Verilen siparişler", "📑", "siparis_liste" },
    };

    @Override
    protected void onCreate(Bundle b) {
        super.onCreate(b);
        final String grup = getIntent().getStringExtra("grup");
        boolean alis = "alis".equals(grup), siparis = "siparis".equals(grup);
        setTitle(alis ? "Alış" : siparis ? "Siparişler" : "Satış");
        if (getActionBar() != null) getActionBar().setDisplayHomeAsUpEnabled(true);
        int renk = alis ? Color.rgb(230, 244, 238) : siparis ? Color.rgb(237, 233, 254) : Color.rgb(232, 240, 254);

        LinearLayout l = Ui.dikey(this);
        int p = Ui.dp(this, 14);
        l.setPadding(p, p, p, p);
        GridLayout izgara = new GridLayout(this);
        izgara.setColumnCount(2);
        int g = (getResources().getDisplayMetrics().widthPixels - Ui.dp(this, 28)) / 2;
        int m = Ui.dp(this, 5);
        for (final String[] a : ALTLAR) {
            if (!a[0].equals(grup)) continue;
            LinearLayout k = Ui.karo(this, a[2], renk, a[1], a[3].length() == 0 ? "yakında" : null);
            k.setOnClickListener(new View.OnClickListener() {
                @Override public void onClick(View v) { ac(a); }
            });
            GridLayout.LayoutParams lp = new GridLayout.LayoutParams();
            lp.width = g - 2 * m;
            lp.height = Ui.dp(this, 130);
            lp.setMargins(m, m, m, m);
            izgara.addView(k, lp);
        }
        l.addView(izgara);
        ScrollView s = new ScrollView(this);
        s.addView(l);
        setContentView(s);
    }

    private void ac(String[] a) {
        if (a[3].length() == 0) {
            Toast.makeText(this, a[1] + " hazırlanıyor: Mikro'daki örnek evrakla doğrulandıktan sonra açılacak.", Toast.LENGTH_LONG).show();
            return;
        }
        if ("bekleyen".equals(a[3])) { startActivity(new Intent(this, BekleyenIrsaliyeActivity.class)); return; }
        if ("siparis_liste".equals(a[3])) { startActivity(new Intent(this, SiparisListeActivity.class)); return; }
        if (a[3].startsWith("siparis_")) {
            startActivity(new Intent(this, SiparisActivity.class).putExtra("tur", a[3].substring(8)));
            return;
        }
        startActivity(new Intent(this, EvrakActivity.class).putExtra("tur", a[3]));
    }

    @Override public boolean onOptionsItemSelected(MenuItem i) {
        if (i.getItemId() == android.R.id.home) finish();
        return true;
    }
}
