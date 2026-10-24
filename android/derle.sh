#!/bin/bash
# APK derleme (Gradle'sız): javac -> dx -> aapt -> zipalign -> apksigner
# Gerekenler (Ubuntu): openjdk, android-sdk-platform-23, android-sdk-build-tools, dalvik-exchange
set -euo pipefail
cd "$(dirname "$0")"
SDK=${ANDROID_SDK:-/usr/lib/android-sdk}
BT=$(ls -d $SDK/build-tools/*/ | grep -v debian | sort -V | tail -1)
JAR=$SDK/platforms/android-23/android.jar
DX=${DX:-dalvik-exchange}
OUT=build
rm -rf $OUT && mkdir -p $OUT/classes $OUT/gen

"$BT/aapt" package -f -m -J $OUT/gen -M AndroidManifest.xml -S res -I "$JAR"
javac -nowarn -Xlint:-options -source 8 -target 8 -encoding UTF-8 -bootclasspath "$JAR" -d $OUT/classes \
  $(find src $OUT/gen -name '*.java')
$DX --dex --output=$OUT/classes.dex $OUT/classes
"$BT/aapt" package -f -M AndroidManifest.xml -S res -I "$JAR" -F $OUT/imzasiz.apk
(cd $OUT && "$BT/aapt" add imzasiz.apk classes.dex >/dev/null)
"$BT/zipalign" -f 4 $OUT/imzasiz.apk $OUT/hizali.apk

# İmza anahtarı ilk derlemede oluşturulur; sonraki sürümlerin güncelleme olarak kurulabilmesi için SAKLAYIN.
KS=${KEYSTORE:-butcem.keystore}
if [ ! -f "$KS" ]; then
  keytool -genkeypair -keystore "$KS" -alias butcem -keyalg RSA -keysize 2048 -validity 10000 \
    -storepass "${KS_SIFRE:-butcem123}" -keypass "${KS_SIFRE:-butcem123}" -dname "CN=Butcem Terminal, O=Butcem, C=TR" 2>/dev/null
fi
"$BT/apksigner" sign --ks "$KS" --ks-pass pass:"${KS_SIFRE:-butcem123}" --out ButcemTerminal.apk $OUT/hizali.apk
"$BT/apksigner" verify ButcemTerminal.apk && echo "Hazır: $(pwd)/ButcemTerminal.apk"
