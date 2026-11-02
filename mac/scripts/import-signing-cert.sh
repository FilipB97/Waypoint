#!/bin/bash
# CI (macOS): certyfikat z sekretów MAC_SIGN_P12 / MAC_SIGN_PASSWORD do tymczasowego pęku kluczy
# i WAYPOINT_SIGN_IDENTITY dla bundle-app.sh. Bez sekretu: z --ephemeral jednorazowy certyfikat
# (CI sprawdza całą ścieżkę podpisu), inaczej podpis ad-hoc.
set -euo pipefail
MODE="${1:-}"
W="${RUNNER_TEMP:-$(mktemp -d)}/wpsign"
rm -rf "$W"; mkdir -p "$W"
if [ -n "${MAC_SIGN_P12:-}" ]; then
    printf '%s' "$MAC_SIGN_P12" | base64 --decode > "$W/sign.p12"
    PW="${MAC_SIGN_PASSWORD:-}"
elif [ "$MODE" = "--ephemeral" ]; then
    echo "Brak MAC_SIGN_P12 — jednorazowy certyfikat (tylko test ścieżki podpisu)."
    "$(dirname "$0")/make-signing-cert.sh" "$W" > "$W/out.txt"
    PW="$(sed -n 's/^MAC_SIGN_PASSWORD = //p' "$W/out.txt")"
else
    echo "Brak MAC_SIGN_P12 — podpis ad-hoc (macOS zapyta o dostęp do haseł po każdej aktualizacji)."
    exit 0
fi

KC="$W/sign.keychain-db"
KCPW="$(openssl rand -hex 12)"
security create-keychain -p "$KCPW" "$KC"
security set-keychain-settings -lut 21600 "$KC"
security unlock-keychain -p "$KCPW" "$KC"
security import "$W/sign.p12" -k "$KC" -P "$PW" -T /usr/bin/codesign >/dev/null
security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$KCPW" "$KC" >/dev/null
# Pęk dopisany do listy wyszukiwania (codesign szuka tożsamości tylko tam).
security list-keychains -d user -s "$KC" $(security list-keychains -d user | tr -d '"')
# Certyfikat samopodpisany nie jest „zaufany", więc bez -v; bierzemy skrót SHA-1 tożsamości.
ID="$(security find-identity -p codesigning "$KC" | awk '/\)/ {print $2; exit}')"
[ -n "$ID" ] || { echo "Nie znaleziono tożsamości podpisu w certyfikacie"; security find-identity -p codesigning "$KC"; exit 1; }
echo "Tożsamość podpisu: $ID"
[ -n "${GITHUB_ENV:-}" ] && echo "WAYPOINT_SIGN_IDENTITY=$ID" >> "$GITHUB_ENV"
