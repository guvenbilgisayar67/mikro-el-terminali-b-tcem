package com.butcem.terminal;

import android.content.Context;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.util.TypedValue;
import android.view.Gravity;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.TextView;

import java.text.DecimalFormat;
import java.text.DecimalFormatSymbols;
import java.util.Locale;

/** Kod ile kurulan ekranlar için ortak görünüm parçaları. */
final class Ui {
    static final int VURGU = Color.rgb(31, 111, 80);
    static final int SOLUK = Color.rgb(95, 107, 122);
    static final int HATA = Color.rgb(180, 35, 24);

    private static final DecimalFormat PARA =
            new DecimalFormat("#,##0.00", DecimalFormatSymbols.getInstance(new Locale("tr", "TR")));
    private static final DecimalFormat MIKTAR =
            new DecimalFormat("#,##0.###", DecimalFormatSymbols.getInstance(new Locale("tr", "TR")));

    static String para(double d) { return PARA.format(d); }
    static String miktar(double d) { return MIKTAR.format(d); }

    static int dp(Context c, float v) {
        return (int) TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, v, c.getResources().getDisplayMetrics());
    }

    static LinearLayout dikey(Context c) {
        LinearLayout l = new LinearLayout(c);
        l.setOrientation(LinearLayout.VERTICAL);
        return l;
    }

    static LinearLayout yatay(Context c) {
        LinearLayout l = new LinearLayout(c);
        l.setOrientation(LinearLayout.HORIZONTAL);
        l.setGravity(Gravity.CENTER_VERTICAL);
        return l;
    }

    static TextView yazi(Context c, String s, float sp, boolean kalin) {
        TextView t = new TextView(c);
        t.setText(s);
        t.setTextSize(sp);
        t.setTextColor(Color.rgb(27, 31, 36));
        if (kalin) t.setTypeface(Typeface.DEFAULT_BOLD);
        return t;
    }

    static Button dugme(Context c, String s, boolean birincil) {
        Button b = new Button(c);
        b.setText(s);
        b.setAllCaps(false);
        b.setTextSize(16);
        GradientDrawable g = new GradientDrawable();
        g.setCornerRadius(dp(c, 6));
        if (birincil) {
            g.setColor(VURGU);
            b.setTextColor(Color.WHITE);
        } else {
            g.setColor(Color.WHITE);
            g.setStroke(dp(c, 1), Color.rgb(200, 205, 212));
            b.setTextColor(VURGU);
        }
        b.setBackground(g);
        return b;
    }

    static LinearLayout.LayoutParams genis(Context c, int ustBosluk) {
        LinearLayout.LayoutParams p = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        p.topMargin = dp(c, ustBosluk);
        return p;
    }

    static LinearLayout.LayoutParams agirlik(Context c, float w, int solBosluk) {
        LinearLayout.LayoutParams p = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, w);
        p.leftMargin = dp(c, solBosluk);
        return p;
    }
}
