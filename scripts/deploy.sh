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

cd "$ROOT"
./gradlew :app:assembleRelease

APK="$ROOT/app/build/outputs/apk/release/app-release.apk"
[ -f "$APK" ] || { echo "APK introuvable : $APK" >&2; exit 1; }

"$ADB" connect "$IP:$PORT" >/dev/null
DEV="$IP:$PORT"
"$ADB" -s "$DEV" install -r "$APK"
"$ADB" -s "$DEV" shell appops set "$PKG" SYSTEM_ALERT_WINDOW allow
"$ADB" -s "$DEV" shell pm grant "$PKG" android.permission.POST_NOTIFICATIONS || true
"$ADB" -s "$DEV" shell am start -n "$PKG/.MainActivity" >/dev/null
echo "installé et lancé"
