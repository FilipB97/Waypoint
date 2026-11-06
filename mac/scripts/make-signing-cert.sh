#!/bin/bash
# Jednorazowo: własny certyfikat do podpisu Waypoint.app (samopodpisany, ważny 10 lat).
#
# Po co: podpis ad-hoc zmienia się z każdą wersją, więc macOS po aktualizacji pyta o hasło systemu,
# zanim Waypoint odczyta hasła z Pęku kluczy. Wpis w Pęku kluczy pamięta „wymaganie podpisu" — przy
# podpisie tym samym certyfikatem jest ono stałe. Jedno pytanie po aktualizacji zostaje i tak: bez Team ID
# od Apple macOS przypina wpis także do skrótu kompilacji (partition_id: cdhash:…) — usuwa to tylko Developer ID.
# (Pierwszego uruchomienia pobranej paczki to nie zmienia — do tego potrzebna jest notaryzacja Apple.)
#
# Użycie (macOS albo Linux z openssl):   mac/scripts/make-signing-cert.sh [katalog-wyjściowy]
# Potem w repozytorium: Settings → Secrets and variables → Actions → New repository secret:
#   MAC_SIGN_P12       = zawartość pliku waypoint-sign.p12.base64
#   MAC_SIGN_PASSWORD  = hasło wypisane przez skrypt
# Klucz prywatny trzymaj tylko tam (i ewentualnie w kopii zapasowej) — nie dodawaj go do repozytorium.
set -euo pipefail
OUT="${1:-.}"
mkdir -p "$OUT"
W="$(mktemp -d)"
trap 'rm -rf "$W"' EXIT
PW="$(openssl rand -hex 16)"

cat > "$W/cert.cnf" <<CNF
[req]
distinguished_name = dn
x509_extensions = ext
prompt = no
[dn]
CN = Waypoint (self-signed code signing)
[ext]
basicConstraints = critical, CA:false
keyUsage = critical, digitalSignature
extendedKeyUsage = critical, codeSigning
subjectKeyIdentifier = hash
CNF
openssl req -x509 -newkey rsa:2048 -nodes -days 3650 -keyout "$W/key.pem" -out "$W/cert.pem" -config "$W/cert.cnf" 2>/dev/null
# 3DES/SHA1 — format, który `security import` na macOS czyta niezależnie od wersji OpenSSL.
openssl pkcs12 -export -inkey "$W/key.pem" -in "$W/cert.pem" -name "Waypoint" -out "$OUT/waypoint-sign.p12" \
    -passout "pass:$PW" -keypbe PBE-SHA1-3DES -certpbe PBE-SHA1-3DES -macalg sha1
base64 < "$OUT/waypoint-sign.p12" | tr -d '\n' > "$OUT/waypoint-sign.p12.base64"

echo "Certyfikat: $OUT/waypoint-sign.p12 (base64: $OUT/waypoint-sign.p12.base64)"
echo "MAC_SIGN_PASSWORD = $PW"
if command -v gh >/dev/null; then
    echo
    echo "Albo przez gh (w katalogu repozytorium):"
    echo "  gh secret set MAC_SIGN_P12 < \"$OUT/waypoint-sign.p12.base64\""
    echo "  gh secret set MAC_SIGN_PASSWORD --body '$PW'"
fi
