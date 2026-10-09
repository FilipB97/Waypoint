#!/bin/bash
# Test end-to-end na runnerze macOS (CI): prawdziwe logowanie SSH z aplikacji i zrzuty ekranu.
#
#  * konto testowe + własny sshd na 127.0.0.1:2222 (hasło przez PAM, jak na zwykłym Macu/serwerze);
#  * serwer A: aplikacja zapisuje hasło w Pęku kluczy i ma się zalogować BEZ pytania (askpass + Keychain);
#  * serwer B nie ma hasła → w karcie ma się pojawić pytanie; test odpowiada i sprawdza logowanie;
#  * aplikacja sama robi zrzuty okna (Smoke.swift) do mac/dist/smoke — trafiają do artefaktu CI.
#
# Wymaga sudo (runner GitHub Actions ma je bez hasła). Nie uruchamiać na własnym Macu — tworzy konto.
set -euo pipefail

MAC_DIR="$(cd "$(dirname "$0")/.." && pwd)"
APP_BIN="$MAC_DIR/dist/Waypoint.app/Contents/MacOS/Waypoint"
WORK="$(mktemp -d /tmp/wpsmoke.XXXX)"
OUT="$MAC_DIR/dist/smoke"
USER_NAME=wptest
PW="Wp-$(openssl rand -hex 8)"
ID_KEYCHAIN=5a0e0000000000000000000000000001
ID_PROMPT=5a0e0000000000000000000000000002
mkdir -p "$OUT"

echo "== konto testowe"
sudo sysadminctl -addUser "$USER_NAME" -fullName "Waypoint Test" -password "$PW" -home "/Users/$USER_NAME" -shell /bin/zsh 2>&1 | tail -1 || true
sudo mkdir -p "/Users/$USER_NAME" && sudo chown "$USER_NAME:staff" "/Users/$USER_NAME"
# Zapis przez sudo: konto testowe w sudoers i plik roota w jego katalogu domowym.
echo "$USER_NAME ALL=(ALL) ALL" | sudo tee /etc/sudoers.d/wptest >/dev/null; sudo chmod 440 /etc/sudoers.d/wptest
printf 'listen 80;\n' | sudo tee "/Users/$USER_NAME/root-owned.conf" >/dev/null
sudo chown root:wheel "/Users/$USER_NAME/root-owned.conf"; sudo chmod 644 "/Users/$USER_NAME/root-owned.conf"
# Gdy włączone jest ograniczenie „Zdalne logowanie tylko dla…", sshd sprawdza tę grupę.
sudo dseditgroup -o edit -a "$USER_NAME" -t user com.apple.access_ssh 2>/dev/null || true

echo "== sshd na 127.0.0.1:2222"
ssh-keygen -q -t ed25519 -N '' -f "$WORK/hostkey"
cat > "$WORK/sshd_config" <<CFG
Port 2222
ListenAddress 127.0.0.1
HostKey $WORK/hostkey
PidFile $WORK/sshd.pid
UsePAM yes
PasswordAuthentication yes
KbdInteractiveAuthentication yes
PubkeyAuthentication no
AllowUsers $USER_NAME
PrintMotd no
Subsystem sftp /usr/libexec/sftp-server
CFG
sudo /usr/sbin/sshd -f "$WORK/sshd_config" -E "$WORK/sshd.log"
for i in $(seq 20); do nc -z 127.0.0.1 2222 && break; sleep 0.5; done

# Klucz hosta znany z góry — test sprawdza hasła, nie pytanie o klucz hosta.
mkdir -p ~/.ssh && chmod 700 ~/.ssh
ssh-keyscan -p 2222 127.0.0.1 2>/dev/null >> ~/.ssh/known_hosts

echo "== lista serwerów"
mkdir -p "$WORK/data"
cat > "$WORK/data/servers.json" <<JSON
[
  {"Id":"$ID_KEYCHAIN","Name":"Lokalny sshd (Pęk kluczy)","Host":"127.0.0.1","Port":2222,"Username":"$USER_NAME","Protocol":"Ssh","Group":"Testy","Pinned":true,"Tags":["ci"]},
  {"Id":"$ID_PROMPT","Name":"Lokalny sshd (pytanie)","Host":"127.0.0.1","Port":2222,"Username":"$USER_NAME","Protocol":"Ssh","Group":"Testy"},
  {"Id":"5a0e0000000000000000000000000003","Name":"Produkcja — web","Host":"web01.example.com","Username":"deploy","Protocol":"Ssh","Group":"Produkcja","Tags":["prod","web"],"Notes":"nginx + php-fpm","AvatarColor":"#3B82F6"},
  {"Id":"5a0e0000000000000000000000000004","Name":"Baza danych","Host":"db01.example.com","Port":2222,"Username":"postgres","Protocol":"Ssh","Group":"Produkcja"},
  {"Id":"5a0e0000000000000000000000000005","Name":"Serwer plików","Host":"files.example.com","Protocol":"Sftp","Username":"deploy","Group":"Produkcja"},
  {"Id":"5a0e0000000000000000000000000006","Name":"Biuro — pulpit","Host":"rdp.example.com","Protocol":"Rdp","Username":"filip","Domain":"FIRMA","Group":"Klienci"},
  {"Id":"5a0e0000000000000000000000000007","Name":"Router","Host":"192.168.1.1","Protocol":"Telnet","Port":23},
  {"Id":"5a0e0000000000000000000000000008","Name":"Telnet (nc)","Host":"127.0.0.1","Port":2323,"Protocol":"Telnet","Group":"Testy"},
  {"Id":"5a0e0000000000000000000000000009","Name":"Konsola COM","Host":"/dev/cu.waypoint-brak","Port":115200,"Protocol":"Serial","Group":"Sprzęt"},
  {"Id":"5a0e000000000000000000000000000a","Name":"Mac mini (VNC)","Host":"10.0.0.9","Port":5900,"Username":"admin","Protocol":"Vnc","Group":"Sprzęt"},
  {"Id":"5a0e000000000000000000000000000b","Name":"Grafana","Host":"grafana.example.com/d/abc","Protocol":"Http","Group":"Sprzęt"}
]
JSON

echo "== udawany serwer telnet (nc)"
# Baner na start; to, co aplikacja wyśle, ląduje w pliku.
( printf 'WAYPOINT_TELNET_BANNER\r\n'; sleep 120 ) | nc -l 127.0.0.1 2323 > "$WORK/telnet-in.txt" 2>/dev/null &

echo "== aplikacja w trybie testu"
set +e
WAYPOINT_DATA_DIR="$WORK/data" WAYPOINT_SMOKE_DIR="$OUT" \
WAYPOINT_SMOKE_KEYCHAIN_SERVER="$ID_KEYCHAIN" WAYPOINT_SMOKE_PROMPT_SERVER="$ID_PROMPT" \
WAYPOINT_SMOKE_PASSWORD="$PW" WAYPOINT_SMOKE_TELNET_IN="$WORK/telnet-in.txt" "$APP_BIN" > "$OUT/app-stdout.log" 2>&1 &
APP_PID=$!
# Zrzuty całego ekranu co kilka sekund — pokazują też okna systemowe, których zrzut okna aplikacji nie obejmie.
( for i in 1 2 3 4 5; do sleep 8; screencapture -x "$OUT/ekran-$i.png" 2>/dev/null; done ) &
for i in $(seq 120); do kill -0 $APP_PID 2>/dev/null || break; sleep 1; done
if kill -0 $APP_PID 2>/dev/null; then echo "przekroczony czas — zatrzymuję"; kill $APP_PID; fi
wait $APP_PID
CODE=$?
set -e

echo "== wynik"
cat "$OUT/smoke.log" 2>/dev/null || echo "(brak smoke.log)"
echo "--- sshd.log (koniec)"; sudo tail -20 "$WORK/sshd.log" || true
cp "$WORK/sshd.log" "$OUT/sshd.log" 2>/dev/null || sudo cat "$WORK/sshd.log" > "$OUT/sshd.log" || true
ls -la "$OUT"

echo "--- root-owned.conf po teście:"; sudo ls -l "/Users/$USER_NAME/root-owned.conf"; sudo cat "/Users/$USER_NAME/root-owned.conf"
sudo rm -f /etc/sudoers.d/wptest
sudo kill "$(cat "$WORK/sshd.pid")" 2>/dev/null || true
security delete-generic-password -s Waypoint -a "$ID_KEYCHAIN" >/dev/null 2>&1 || true   # gdyby test przerwano

[ "$CODE" = 0 ] && [ "$(cat "$OUT/result.txt" 2>/dev/null)" = OK ]
