# Bütçem: kurulum kılavuzu

Bu paket üç parçadan oluşur:

| Parça | Nereye kurulur | Ne yapar |
|---|---|---|
| **ButcemApi** (API + web paneli) | Mikro'nun çalıştığı sunucu (ya da VPN'den Mikro'ya erişen bir Windows makine) | Satış raporu, günün cirosu, cari ekstre, çek raporu, etiket kuyruğu, kullanıcılar |
| **ButcemYazdirma** (yazdırma programı) | Her mağazada, etiket ve A4 yazıcıların USB ile bağlı olduğu bilgisayar | Sunucudaki etiket işlerini alır ve USB yazıcıya basar |
| **ButcemTerminal.apk** | iData T1 Pro el terminalleri | Barkod okut, fiyat ve stok gör, raf / manav / A4 etiket bas |

Mikro'ya **yalnızca okuma** yetkisiyle bağlanılır. API, Mikro ve Orion veritabanlarına hiçbir şey yazmaz; kendi
verisini (kullanıcılar, yazıcılar, etiket kuyruğu, işlem kaydı) ayrı bir `ButcemApp` veritabanında tutar.

---

## 1. Sunucu: veritabanı

SQL Server Management Studio ile Mikro'nun bulunduğu SQL Server'a `sa` (ya da sysadmin) olarak bağlanın.

1. `sql/01_butcem_app.sql` dosyasını çalıştırın. `ButcemApp` veritabanını ve tabloları oluşturur, ilk yönetici
   kullanıcıyı ekler: kullanıcı adı **admin**, şifre **Butcem2026!** (ilk girişte değiştirin).
2. `sql/02_sql_kullanicilari.sql` dosyasını açın, iki şifreyi değiştirin, çalıştırın. Bu betik:
   - `butcem_api` girişini oluşturur: Mikro'da yalnızca okuma (yazma açıkça yasaklanır), `ButcemApp`'te okuma/yazma.
   - `butcem_okuma` girişini oluşturur: Orion'da yalnızca okuma.
   - Mikro veritabanının adı `MikroDB_V16_BUTCEM2024` değilse betikteki `USE` satırını düzeltin.

## 2. Sunucu: API ve web paneli

1. `ButcemApi` klasörünü sunucuya kopyalayın, örneğin `C:\Butcem\Api`.
2. `appsettings.json` dosyasını düzenleyin:
   - `ConnectionStrings:Mikro` ve `ConnectionStrings:App`: sunucu adı (`BUTCEMSVR\SQLEXPRESS` yerine kendi
     sunucunuz), `butcem_api` şifresi.
   - `Orion:Kaynaklar`: Orion veritabanının bağlantısı, `butcem_okuma` şifresi. Şube Orion veritabanları
     (ORIONSOLAK, ORION@HATIP, ORIONAKTAS) merkezden daha güncelse, her biri için listeye bir satır daha
     eklenebilir; aynı Z raporu iki kaynakta varsa bir kez sayılır.
   - `Jwt:Anahtar`: en az 32 karakterlik rastgele bir metin yazın (örneğin PowerShell'de
     `[Convert]::ToBase64String((1..48 | % {Get-Random -Max 256}))`). Bu anahtar giriş jetonlarını imzalar,
     kimseyle paylaşmayın.
   - `Etiket`: raf etiketi ölçüsü (varsayılan 100 x 39 mm). Etiket yazıcıda yan dönmüş çıkıyorsa
     `Dondur90: true` yapın. `EskiFiyatGoster` ve `YerliUretimLogosu` Orion tasarımındaki gizli alanlardır;
     isterseniz `true` yapın (yerli üretim logosu Mikro'da menşei TÜRKİYE olan ürünlerde çıkar).
3. Önce elle deneyin: `C:\Butcem\Api\Butcem.Api.exe` çalıştırın, tarayıcıda `http://localhost:5080/api/saglik`
   açın. Üç satırın da `"ok"` olması gerekir.
4. Windows hizmeti olarak kurun (yönetici komut isteminde):

   ```
   sc create ButcemApi binPath= "C:\Butcem\Api\Butcem.Api.exe" start= auto
   sc start ButcemApi
   netsh advfirewall firewall add rule name="ButcemApi" dir=in action=allow protocol=TCP localport=5080
   ```

5. Panel: `http://SUNUCU-IP:5080` (mağazalardan VPN üzerinden aynı adres). `admin` ile girin,
   **Yönetim** sayfasından kendi şifrenizi ve kullanıcıları oluşturun:
   - **Yönetici**: her şey.
   - **Merkez**: raporlar, etiket, çek giriş/çıkış, fatura ve irsaliye, sevk.
   - **Mağaza / el terminali**: etiket, stok sorgusu ve yalnızca kendi deposundan depolar arası sevk.

### 2a. Mikro'ya evrak yazma (sevk, çek, irsaliye, fatura)

Yazma varsayılan olarak **kapalıdır**; kapalıyken ekranlar açılır ama Kaydet "yazma kapalı" der.
Açmak için:

1. `sql\03_yazma_kullanicisi.sql` dosyasındaki şifreyi değiştirip sysadmin ile çalıştırın. `butcem_yazma`
   girişi yalnızca evrak tablolarına **ekleme** yapabilir; güncelleme yalnızca çek kartının yerini/pozisyonunu
   ve irsaliyenin fatura bağlantısını değiştirebilir; silme yetkisi yoktur.
2. `appsettings.json`:
   - `ConnectionStrings:MikroYazma`: `butcem_yazma` ve şifresi.
   - `Yazma:MikroKullaniciNo`: kayıtlarda "oluşturan" olarak görünecek Mikro kullanıcı numarası. Mikro'da
     "BUTCEM" adlı bir kullanıcı açıp onun numarasını yazmanızı öneririm; böylece hangi evrakın panelden
     geldiği Mikro'da da görünür.
   - `Yazma:DepoSerileri`: depoya göre evrak serisi (sevk ve alış irsaliyesi). Hazır değerler sizin
     verinizden alındı: 1 TEPE, 2 SOLAK, 3 A.DEPO, 4 HATIP, 5 boş.
   - `Yazma:CekKasasi`: müşteri çeklerinin durduğu kasa (`ÇEK KASASI`).
   - `Yazma:SatisEBelge`: `true` ise satış faturası Mikro'daki gibi e-belge kaydıyla açılır ve Mikro'nun
     e-fatura / e-arşiv ekranından gönderilir. `false` ise e-belge kaydı açılmaz.
   - `Yazma:KdvOranlari` (isteğe bağlı): KDV göstergesine göre oran. Boşsa 1:%0, 2:%1, 3:%8, 4:%18, 6:%10,
     7:%20 kullanılır (verinizden doğrulandı).
   - Son olarak `Yazma:Acik: true`.
3. Önce **kopya veritabanında** deneyin: `ConnectionStrings:Mikro` ve `MikroYazma` kopyayı göstersin, birkaç
   evrak girip Mikro'da açın. Sonra canlıya çevirin.

Ne yazılır (Mikro'nun kendi evraklarıyla alan alan karşılaştırıldı):

| Ekran | Mikro'daki karşılığı | Tablolar |
|---|---|---|
| Depolar arası sevk | Depolar arası sevk fişi (evrak tipi 2) | STOK_HAREKETLERI |
| Çek girişi | Müşteri çeki giriş bordrosu (4), ÇEK KASASI'nda portföy | CARI_HESAP_HAREKETLERI, ODEME_EMIRLERI |
| Çek çıkışı | Çek çıkış bordrosu (67): müşteri çeki ciro, ya da kendi çekimiz | CARI_HESAP_HAREKETLERI, ODEME_EMIRLERI |
| Alış irsaliyesi | Alış irsaliyesi (13) | STOK_HAREKETLERI |
| Bekleyen irsaliyeler | İrsaliyeden alış faturası (0) | CARI_HESAP_HAREKETLERI (+ irsaliyeye bağlantı) |
| Satış faturası | Satış faturası (4 / 63) | STOK_HAREKETLERI, CARI_HESAP_HAREKETLERI, E_FATURA_ISLEMLERI |
| Fiyat değişikliği | Fiyat değişiklik fişi, 1 numaralı satış fiyatı, tüm depolar | STOK_FIYAT_DEGISIKLIKLERI, STOK_SATIS_FIYAT_LISTELERI (+ STOKLAR değişti zamanı) |

Kontroller: deponun Mikro'daki kilit tarihine ve ileri tarihe evrak girilmez; portföyde olmayan çek ciro
edilmez; faturası girilmiş irsaliye tekrar faturalaşmaz; aynı anda Mikro'dan aynı numara alınırsa numara
yeniden alınır. Her kayıt `IslemLog` tablosuna kim/ne zaman/ne ile yazılır.

İrsaliye ve faturada her satıra 5 iskonto girilebilir. Mikro'daki gibi zincirlemedir: ikinci iskonto,
birincisi düşüldükten kalan tutara uygulanır (ör. 901,44 TL'ye %15 + %2 + %6 = 135,22 + 15,32 + 45,05).

v0.3'ten yükseltirken fiyat değişikliği için `sql/03_yazma_kullanicisi.sql` dosyasını bir kez daha çalıştırın
(yalnız yeni tablo yetkilerini ekler).

Sevk maliyeti ürünün son alış fiyatıdır (iskontolar düşülmüş). Mikro bazı sevklerde farklı bir maliyet
önerebiliyor; bu yalnız maliyet raporunu etkiler, stok miktarını ve cariyi etkilemez.

### 2b. Kullanıcılar ve yetkiler

Kullanıcılar panelde **Yönetim → Kullanıcılar** bölümünden açılır (Mikro kullanıcıları değildir). Üç tip vardır:

| Tip | Ne yapar |
|---|---|
| Yönetici | Her ekranı kullanır, kullanıcı ve yazıcı ekler. |
| Merkez personeli | Yalnız işaretlenen ekranları kullanır, tüm depoları görür. |
| Mağaza personeli | Yalnız işaretlenen ekranları kullanır, yalnız kendi deposunu görür ve yalnız kendi deposuna evrak girer. |

Yetkiler ekran ekran işaretlenir: Günün cirosu, Satış raporu, Cari ekstre, Çek raporu, Çek girişi, Çek çıkışı,
Alış irsaliyesi ve faturası, Satış faturası, Depolar arası sevk, Fiyat değişikliği, Etiket basma. Örneğin yalnız alış faturası
girecek muhasebe personeli için tip **Merkez personeli**, yetki yalnız **Alış irsaliyesi ve faturası** seçilir.
Bu kişi menüde yalnız "Fatura ve irsaliye" ekranını, içinde de yalnız alış sekmelerini görür. Evrakta cari
seçebilmesi için ayrıca cari ekstre yetkisi gerekmez.

- Yetki sunucuda her istekte kontrol edilir; menüden gizlemek yalnız görünüş içindir.
- Değişiklik kullanıcıya en geç 30 saniyede yansır, yeniden giriş gerekmez. Pasife alınan kullanıcının açık
  oturumu hemen kapanır.
- Günün cirosu tüm mağazaları gösterdiği için mağaza personeline verilemez.
- Stok/fiyat sorgusu (terminalde barkod okutma) her kullanıcıya açıktır.
- v0.2'den yükseltirken `sql/01_butcem_app.sql` dosyasını bir kez daha çalıştırın (yalnız `Yetkiler` sütununu
  ekler). Eski kullanıcılar eski haklarıyla devam eder: merkez kullanıcıları her ekrana, mağaza kullanıcıları
  sevk ve etikete yetkilidir. İstediğiniz zaman Yönetim'den daraltabilirsiniz.

## 3. Mağaza bilgisayarı: yazdırma programı

Her mağazada bir kez yapılır.

1. Yazıcıların Windows sürücüleri kurulu ve deneme sayfası basıyor olmalı (Xprinter XP-470B, Argox OS-214 plus,
   Epson L3250, Brother). Etiket yazıcılarında sürücüdeki kâğıt ölçüsünü etiket ölçüsüne ayarlayın.
2. Panelde **Yönetim → Mağaza bilgisayarları**: bilgisayara bir ad verin (ör. `SOLAK-KASA1`), mağazayı seçin,
   **Ekle**. Ekranda çıkan **anahtarı kopyalayın**, bir daha gösterilmez.
3. `ButcemYazdirma` klasörünü bilgisayara kopyalayın, örneğin `C:\Butcem\Yazdirma`. `appsettings.json` içinde:
   - `Sunucu`: `http://SUNUCU-IP:5080`
   - `Ad`: 2. adımda verdiğiniz ad (birebir aynı)
   - `Anahtar`: kopyaladığınız anahtar
4. Komut isteminde `C:\Butcem\Yazdirma\ButcemYazdirma.exe yazicilar` çalıştırın: bilgisayardaki yazıcı adlarını
   listeler.
5. Panelde **Yönetim → Yazıcılar**: bilgisayarı seçin, görünen bir ad yazın (ör. "Raf yazıcısı"), 4. adımdaki
   Windows yazıcı adını **birebir** yapıştırın, modeli seçin:
   - Xprinter XP-470B → **TSPL**
   - Argox OS-214 plus → **EPL**
   - Epson / Brother → **A4** (etiketler A4 sayfaya 2 x 7 dizilir)
6. Hizmet olarak kurun (yönetici komut isteminde):

   ```
   sc create ButcemYazdirma binPath= "C:\Butcem\Yazdirma\ButcemYazdirma.exe" start= auto
   sc start ButcemYazdirma
   ```

   Panelde **Yönetim → Mağaza bilgisayarları** listesinde "Son görülme" birkaç saniye içinde dolmalıdır.
   A4 yazıcı hizmet olarak basmazsa (bazı sürücüler hizmetten basmayı sevmez), hizmeti silip
   (`sc delete ButcemYazdirma`) programı Görev Zamanlayıcı ile "kullanıcı oturum açtığında" başlatın.
7. Panelde **Etiket** sayfasından bir ürün okutup basın. İş "Basıldı" olmalı; hata varsa sebebi yazar.

Etiketler yazıcıya resim olarak gönderilir; bu yüzden Türkçe karakterler ve ₺ işareti yazıcı fontundan bağımsız
olarak doğru çıkar.

## 4. El terminali (iData T1 Pro)

1. `ButcemTerminal.apk` dosyasını terminale kopyalayın (USB ya da paylaşım), açın. "Bilinmeyen kaynaklardan
   yüklemeye izin ver" sorulursa izin verin.
2. Açılışta **sunucu adresi** (`http://SUNUCU-IP:5080`), kullanıcı adı ve şifre girilir. Her terminal için
   panelden **Mağaza personeli** tipinde ayrı kullanıcı açın; kullanıcı mağazasına bağlıdır. Terminalde
   etiket bölümü **Etiket basma**, sevk menüsü **Depolar arası sevk** yetkisi varsa görünür.
3. Okuyucu ayarı (iData **ScanWedge / Tarayıcı Ayarları**), ikisinden biri:
   - **Çıkış modu: Yayın (Broadcast)**, eylem `android.intent.action.SCANRESULT`, ek alan `value`
     (iData'nın varsayılanı), ya da
   - **Klavye (Keyboard) modu** ve "sonuna Enter ekle" açık.
4. Kullanım: barkod okutulur → ürün adı, satış fiyatı, fiyat değişim tarihi, mağazaların stokları görünür.
   **Listeye ekle** (ya da **Adet…**) ile etiket listesi oluşur; şablon (raf / manav) ve mağaza yazıcısı seçilip
   **Bas** denir. Sağ üst menüde **Bugün fiyatı değişenleri ekle** ve **Depolar arası sevk** vardır.
5. Sevk: menüden **Depolar arası sevk** açılır, çıkış deposu terminalin mağazasıdır. Gidecek depo seçilir,
   ürünler okutulur (her okutma 1 ekler, dokunarak miktar değişir), **Sevki kaydet** denir.

APK'yı güncellemek için aynı imza anahtarı (`butcem.keystore`, şifresi `butcem123`) gerekir; bu dosyayı saklayın.
Kaybolursa yeni sürüm kurmadan önce eskisini kaldırmak gerekir.

## 5. Güvenlik

- API'yi internete doğrudan açmayın; mağazalar ve terminaller VPN üzerinden bağlansın. Dış ağa açmak gerekirse
  önüne HTTPS sağlayan bir ters vekil (IIS / Caddy / nginx) ve güvenlik duvarında IP kısıtı koyun.
- Orion'un `TBL_SUBE_MAIN` tablosunda `sa` şifresi düz metin duruyor. `sa` şifresini değiştirip Orion'a ayrı,
  yetkisi kısıtlı bir giriş tanımlamanızı öneririm.
- Her giriş ve etiket baskısı `ButcemApp.dbo.IslemLog` tablosuna yazılır.

## 6. Bilinen eksikler ve sıradaki işler

- **Manav etiketi** şimdilik yer tutucu tasarımdır; tasarım dosyası gelince Orion'daki gibi yapılacak.
  **A4 etiket** düzeni de tasarıma göre güncellenecek.
- Merkez Orion'da şube Z raporları gecikmeli, Aktaş kasaları (11, 12) çoğunlukla eksik. Günün cirosu ekranı bu
  yüzden mağaza bazında "son güncelleme" saatini gösterir. Şube Orion veritabanlarına VPN'den erişilebiliyorsa
  kaynak olarak eklenmesi bunu çözer.
- APK gerçek iData cihazında henüz denenmedi; ilk kurulumda okuyucu modunu kontrol edin.
- Yazma özellikleri kopya veritabanında denendi; canlıda `Yazma:Acik` sizin onayınızla açılacak.
- Satış irsaliyesi, iade faturaları, çeki bankaya tahsile verme ve senetler henüz yok.
- Kopyadaki portföyde 2021-2024 vadeli, hâlâ ÇEK KASASI'nda görünen müşteri çekleri var; Mikro'da
  kapatılmamış olabilirler.

## 7. Geliştirici notları

```
# API (Linux/Windows):  dotnet build ; dotnet run --project src/Butcem.Api
# Windows paketi:       dotnet publish src/Butcem.Api -c Release -r win-x64 --self-contained -o yayin/ButcemApi
#                       dotnet publish src/Butcem.Yazdirma -c Release -o yayin/ButcemYazdirma
# Panel:                cd web && npm install && npm run build      (çıktı src/Butcem.Api/wwwroot)
# APK:                  android/derle.sh   (javac + dx + aapt + apksigner; Gradle gerekmez)
```

Kod düzeni: `src/Butcem.Api/Features` her ekranın uç noktaları, `Infrastructure` bağlantı / yetki / Mikro kodları,
`Etiket` etiket çizimi ve yazıcı komutları (TSPL, EPL, A4), `sql` kurulum betikleri.
