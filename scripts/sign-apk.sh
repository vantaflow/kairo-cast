#!/usr/bin/env bash
# Signe un APK avec la clé décrite dans keystore.properties (non versionné).
# apksigner local (ANDROID_HOME/build-tools) si présent ; sinon, si KAIRO_SIGN_HOST=user@hôte est défini,
# signature sur cette machine par SSH (clé SSH requise, android-sdk/build-tools dans le HOME distant ;
# KAIRO_SIGN_JAVA_HOME = JDK distant si java n'est pas dans le PATH).
# Usage : scripts/sign-apk.sh <entrée.apk> <sortie.apk>
set -euo pipefail

IN="${1:?usage: scripts/sign-apk.sh <entrée.apk> <sortie.apk>}"
OUT="${2:?usage: scripts/sign-apk.sh <entrée.apk> <sortie.apk>}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROPS="$ROOT/keystore.properties"
[ -f "$PROPS" ] || { echo "keystore.properties introuvable" >&2; exit 1; }
prop() { sed -n "s/^$1=//p" "$PROPS" | tr -d '\r'; }
STORE="$(prop storeFile)"; STOREPASS="$(prop storePassword)"; ALIAS="$(prop keyAlias)"; KEYPASS="$(prop keyPassword)"
[ -f "$STORE" ] || STORE="$ROOT/$STORE"
[ -f "$STORE" ] || { echo "clé introuvable : $(prop storeFile)" >&2; exit 1; }

local_apksigner() {
  local sdk="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-$HOME/Library/Android/sdk}}"
  ls -d "$sdk"/build-tools/*/apksigner 2>/dev/null | sort -V | tail -1
}

SIGNER="$(local_apksigner || true)"
if [ -n "$SIGNER" ]; then
  KS_PASS="$STOREPASS" KEY_PASS="$KEYPASS" "$SIGNER" sign --ks "$STORE" --ks-key-alias "$ALIAS" \
    --ks-pass env:KS_PASS --key-pass env:KEY_PASS --out "$OUT" "$IN"
  "$SIGNER" verify --print-certs "$OUT" | grep -m1 SHA-256
  exit 0
fi

HOST="${KAIRO_SIGN_HOST:?apksigner introuvable : définir ANDROID_HOME ou KAIRO_SIGN_HOST=user@hôte}"
TMP="kairo-sign-$$"
JAVA_PREFIX=""
[ -n "${KAIRO_SIGN_JAVA_HOME:-}" ] && JAVA_PREFIX="export PATH=$KAIRO_SIGN_JAVA_HOME/bin:\$PATH; "
ssh -o BatchMode=yes "$HOST" "mkdir -p ~/$TMP"
trap 'ssh -o BatchMode=yes "$HOST" "rm -rf ~/$TMP" >/dev/null 2>&1 || true' EXIT
scp -q "$IN" "$HOST:$TMP/in.apk"
scp -q "$STORE" "$HOST:$TMP/key.jks"
# Mots de passe par l'entrée standard : jamais dans la ligne de commande distante.
REMOTE="${JAVA_PREFIX}cd ~/$TMP && read -r KS_PASS && read -r KEY_PASS && export KS_PASS KEY_PASS"
REMOTE="$REMOTE && S=\$(ls -d ~/android-sdk/build-tools/*/apksigner | sort -V | tail -1)"
REMOTE="$REMOTE && \$S sign --ks key.jks --ks-key-alias '$ALIAS' --ks-pass env:KS_PASS --key-pass env:KEY_PASS --out out.apk in.apk"
REMOTE="$REMOTE && \$S verify --print-certs out.apk | grep -m1 SHA-256"
printf '%s\n%s\n' "$STOREPASS" "$KEYPASS" | ssh -o BatchMode=yes "$HOST" "$REMOTE"
scp -q "$HOST:$TMP/out.apk" "$OUT"
