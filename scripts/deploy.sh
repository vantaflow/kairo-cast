#!/usr/bin/env bash
# Compile la version release, l'installe sur la box et la lance. Idempotent.
# Usage : scripts/deploy.sh <IP> [port]
set -euo pipefail

IP="${1:?usage: scripts/deploy.sh <IP> [port]}"
PORT="${2:-5555}"
PKG="com.vantaflow.kairo.cast"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

if command -v adb >/dev/null 2>&1; then
  ADB=adb
elif [ -n "${ANDROID_HOME:-}" ] && [ -x "$ANDROID_HOME/platform-tools/adb" ]; then
  ADB="$ANDROID_HOME/platform-tools/adb"
else
  echo "adb introuvable (ajoutez-le au PATH ou définissez ANDROID_HOME)" >&2
  exit 1
fi

# Kotlin/Gradle exigent un JDK 17 (le JDK 26 par défaut échoue).
java_major() { "$1/bin/java" -version 2>&1 | sed -n '1s/.*version "\([0-9]*\).*/\1/p'; }
if [ -z "${JAVA_HOME:-}" ] || [ "$(java_major "$JAVA_HOME" 2>/dev/null)" != "17" ]; then
  JAVA_HOME="$(/usr/libexec/java_home -v 17 2>/dev/null || true)"
  [ -n "$JAVA_HOME" ] || { echo "JDK 17 introuvable : installez-le ou définissez JAVA_HOME" >&2; exit 1; }
  export JAVA_HOME
fi

# La release est signée avec la clé de debug si keystore.properties est absent.
if [ ! -f "$ROOT/keystore.properties" ] && [ ! -f "$HOME/.android/debug.keystore" ]; then
  echo "Clé de signature introuvable : créez keystore.properties ou ~/.android/debug.keystore" >&2
  exit 1
fi

cd "$ROOT"
./gradlew :app:assembleRelease

APK="$ROOT/app/build/outputs/apk/release/app-release.apk"
[ -f "$APK" ] || { echo "APK introuvable : $APK" >&2; exit 1; }

DEV="$IP:$PORT"
OUT="$("$ADB" connect "$DEV" 2>&1 || true)"
case "$OUT" in
  *"connected to"*) ;;
  *) echo "Impossible de joindre la box $DEV : $OUT" >&2; exit 1 ;;
esac
"$ADB" -s "$DEV" install -r "$APK"
"$ADB" -s "$DEV" shell appops set "$PKG" SYSTEM_ALERT_WINDOW allow
"$ADB" -s "$DEV" shell pm grant "$PKG" android.permission.POST_NOTIFICATIONS || true
"$ADB" -s "$DEV" shell am start -n "$PKG/.MainActivity" >/dev/null
echo "installé et lancé"
