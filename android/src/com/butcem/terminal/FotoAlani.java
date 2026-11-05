package com.butcem.terminal;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.DialogInterface;
import android.content.Intent;
import android.content.SharedPreferences;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Matrix;
import android.media.ExifInterface;
import android.net.Uri;
import android.provider.MediaStore;
import android.view.View;
import android.widget.Button;
import android.widget.HorizontalScrollView;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.TextView;
import android.widget.Toast;

import java.io.File;
import java.io.FileOutputStream;
import java.util.ArrayList;
import java.util.List;

/**
 * Evrak ekranlarında fotoğraf: kamerayla çekilir, 1600 piksele küçültülüp JPEG olarak cihazda tutulur.
 * Evrak kaydedilince Kuyruk.fotograflariGonder ile merkeze gider (Mikro'ya değil). Küçük resme dokununca silinir.
 */
final class FotoAlani {
    static final int ISTEK = 7301;
    private static final int EN_BUYUK = 1600;

    private final Activity a;
    private final LinearLayout kok, resimler;
    private final TextView bilgi;
    private final List<File> dosyalar = new ArrayList<File>();
    private final SharedPreferences ayar;

    FotoAlani(Activity a) {
        this.a = a;
        ayar = a.getSharedPreferences("butcem", Activity.MODE_PRIVATE);
        kok = Ui.dikey(a);
        LinearLayout satir = Ui.yatay(a);
        Button cek = Ui.dugme(a, "📷 Fotoğraf ekle", false);
        cek.setOnClickListener(new View.OnClickListener() {
            @Override public void onClick(View v) { cek(); }
        });
        satir.addView(cek, Ui.agirlik(a, 0, 0));
        bilgi = Ui.yazi(a, "", 13, false);
        bilgi.setTextColor(Ui.SOLUK);
        satir.addView(bilgi, Ui.agirlik(a, 1, 10));
        kok.addView(satir);
        resimler = Ui.yatay(a);
        HorizontalScrollView h = new HorizontalScrollView(a);
        h.addView(resimler);
        kok.addView(h, Ui.genis(a, 4));
        goster();
    }

    View gorunum() { return kok; }

    List<File> dosyalar() { return new ArrayList<File>(dosyalar); }

    /** Kayıttan sonra: dosyalar artık kuyruğun (silinmez), ekran temizlenir. */
    void birak() { dosyalar.clear(); goster(); }

    private static File klasor(Activity a) {
        File d = a.getExternalFilesDir("fotograf");
        if (d == null) d = new File(a.getFilesDir(), "fotograf");
        d.mkdirs();
        return d;
    }

    private void cek() {
        try {
            File ham = new File(klasor(a), "ham_" + System.currentTimeMillis() + ".jpg");
            ayar.edit().putString("fotoHam", ham.getAbsolutePath()).apply();
            Intent i = new Intent(MediaStore.ACTION_IMAGE_CAPTURE);
            i.putExtra(MediaStore.EXTRA_OUTPUT, Uri.fromFile(ham));
            if (i.resolveActivity(a.getPackageManager()) == null) {
                Toast.makeText(a, "Bu cihazda kamera uygulaması bulunamadı.", Toast.LENGTH_LONG).show();
                return;
            }
            a.startActivityForResult(i, ISTEK);
        } catch (Exception e) {
            Toast.makeText(a, "Kamera açılamadı: " + e.getMessage(), Toast.LENGTH_LONG).show();
        }
    }

    /** Activity.onActivityResult'tan çağrılır; fotoğraf isteği değilse false. */
    boolean sonuc(int istek, int durum) {
        if (istek != ISTEK) return false;
        String yol = ayar.getString("fotoHam", null);
        ayar.edit().remove("fotoHam").apply();
        if (yol == null) return true;
        File ham = new File(yol);
        if (durum != Activity.RESULT_OK || !ham.exists() || ham.length() == 0) { ham.delete(); return true; }
        try {
            File kucuk = new File(klasor(a), "foto_" + System.currentTimeMillis() + ".jpg");
            kucult(ham, kucuk);
            ham.delete();
            dosyalar.add(kucuk);
            goster();
        } catch (Throwable e) {
            Toast.makeText(a, "Fotoğraf işlenemedi: " + e.getMessage(), Toast.LENGTH_LONG).show();
        }
        return true;
    }

    private static void kucult(File kaynak, File hedef) throws Exception {
        BitmapFactory.Options o = new BitmapFactory.Options();
        o.inJustDecodeBounds = true;
        BitmapFactory.decodeFile(kaynak.getAbsolutePath(), o);
        int olcek = 1;
        while (Math.max(o.outWidth, o.outHeight) / (olcek * 2) >= EN_BUYUK) olcek *= 2;
        BitmapFactory.Options o2 = new BitmapFactory.Options();
        o2.inSampleSize = olcek;
        Bitmap b = BitmapFactory.decodeFile(kaynak.getAbsolutePath(), o2);
        if (b == null) throw new Exception("resim okunamadı");
        float k = Math.min(1f, (float) EN_BUYUK / Math.max(b.getWidth(), b.getHeight()));
        Matrix m = new Matrix();
        if (k < 1f) m.postScale(k, k);
        int yon = new ExifInterface(kaynak.getAbsolutePath()).getAttributeInt(ExifInterface.TAG_ORIENTATION, ExifInterface.ORIENTATION_NORMAL);
        if (yon == ExifInterface.ORIENTATION_ROTATE_90) m.postRotate(90);
        else if (yon == ExifInterface.ORIENTATION_ROTATE_180) m.postRotate(180);
        else if (yon == ExifInterface.ORIENTATION_ROTATE_270) m.postRotate(270);
        Bitmap s = Bitmap.createBitmap(b, 0, 0, b.getWidth(), b.getHeight(), m, true);
        FileOutputStream out = new FileOutputStream(hedef);
        try { s.compress(Bitmap.CompressFormat.JPEG, 75, out); } finally { out.close(); }
        if (s != b) s.recycle();
        b.recycle();
    }

    private void goster() {
        resimler.removeAllViews();
        bilgi.setText(dosyalar.isEmpty() ? "Fotoğraf yok (isteğe bağlı)" : dosyalar.size() + " fotoğraf · silmek için dokunun");
        int boy = Ui.dp(a, 64);
        for (final File f : dosyalar) {
            ImageView iv = new ImageView(a);
            iv.setScaleType(ImageView.ScaleType.CENTER_CROP);
            BitmapFactory.Options o = new BitmapFactory.Options();
            o.inSampleSize = 8;
            iv.setImageBitmap(BitmapFactory.decodeFile(f.getAbsolutePath(), o));
            iv.setOnClickListener(new View.OnClickListener() {
                @Override public void onClick(View v) {
                    new AlertDialog.Builder(a).setTitle("Fotoğraf silinsin mi?")
                            .setPositiveButton("Sil", new DialogInterface.OnClickListener() {
                                @Override public void onClick(DialogInterface d, int w) { dosyalar.remove(f); f.delete(); goster(); }
                            })
                            .setNegativeButton("Vazgeç", null).show();
                }
            });
            LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(boy, boy);
            lp.rightMargin = Ui.dp(a, 6);
            resimler.addView(iv, lp);
        }
    }

    /** Ekrandan kaydetmeden çıkılınca çekilen fotoğraflar silinir. */
    void sil() {
        for (File f : dosyalar) f.delete();
        dosyalar.clear();
    }
}
