# Waypoint dla macOS

Natywna aplikacja (SwiftUI) obok wersji Windows. Ten sam format danych: lista serwerów to ten sam
JSON co `servers.json` w Windows, a profil wyeksportowany z Windows (Ustawienia → Eksportuj profil…)
importuje się na Macu bez konwersji. Pola, których Mac nie używa, są przechowywane i zapisywane
z powrotem bez zmian.

## Układ

| Katalog | Co | Gdzie się buduje |
|---|---|---|
| `WaypointCore/` | logika: model serwera, magazyn, import, wyszukiwanie, teksty pl/en | macOS **i Linux** (`swift test`) |
| `App/` | interfejs SwiftUI, zależy od `WaypointCore` | tylko macOS |
| `scripts/bundle-app.sh` | składa `dist/Waypoint.app` (uniwersalna, podpis ad-hoc) i `Waypoint-mac.zip` | macOS |
| `Resources/` | ikona (`AppIcon.svg` → `AppIcon-1024.png`) | — |

## Budowanie

```
swift test --package-path mac/WaypointCore     # testy logiki (macOS albo Linux)
mac/scripts/bundle-app.sh                      # Waypoint.app (wymaga Xcode albo Command Line Tools)
open mac/dist/Waypoint.app
```

CI (`.github/workflows/mac.yml`) uruchamia się przy zmianach w `mac/`: testy logiki na Linuksie
i macOS, potem budowa `.app`. Gotowa paczka jest w artefakcie **Waypoint-mac** przebiegu.

**Pierwsze uruchomienie pobranej paczki:** aplikacja nie ma jeszcze podpisu Developer ID ani
notaryzacji, więc Gatekeeper ją blokuje. Prawy klik na `Waypoint.app` → **Otwórz** → Otwórz
(albo `xattr -dr com.apple.quarantine Waypoint.app`).

## Plan

| Krok | Zakres | Stan |
|---|---|---|
| 1 | Szkielet, lista serwerów (grupy, przypięte, wyszukiwanie), edytor serwera, import z Windows, CI | ✅ |
| 2 | Terminal SSH w kartach (SwiftTerm + systemowe `ssh`: agent, `~/.ssh/config`), hasła w Pęku kluczy | ✅ |
| 3 | RDP przez aplikację Microsoft **Windows App** (plik `.rdp` jak w wersji Windows) | ⏳ |
| 4 | Panel plików SFTP/FTP: przeglądanie, wysyłanie/pobieranie | — |
| 5 | Edytor plików (Monaco) z bezpiecznym zapisem — jak w wersji Windows | — |

## Terminal SSH

Karta uruchamia systemowe `/usr/bin/ssh` w pseudo-terminalu (SwiftTerm). Dzięki temu działa
wszystko, co działa w Terminalu: `~/.ssh/config` (aliasy, ProxyJump), agent i klucze, `known_hosts`.

Hasła: ssh pyta o nie Waypointa przez `SSH_ASKPASS` (ten sam plik wykonywalny w trybie askpass,
gniazdo Unix 0600 w prywatnym katalogu + losowy token). Pierwsza prośba o hasło dostaje hasło
z Pęku kluczy bez okna; kolejna (odrzucone hasło) albo brak zapisanego → pytanie nad kartą
z opcją „Zapisz w Pęku kluczy". Ten sam kanał obsługuje passphrase klucza, potwierdzenie
nieznanego klucza hosta i pytania 2FA. Monit wypisany przez serwer po zalogowaniu (np. `sudo`)
nigdy nie trafia do askpass, więc zapisanego hasła nie da się wyłudzić.

Skróty: Enter / dwuklik na serwerze — połącz, ⌘W — zamknij kartę, ⌘1…⌘9 — karta, ⌘⇧[ / ⌘⇧] —
poprzednia/następna.

## Test end-to-end w CI

`scripts/smoke-test.sh` (tylko runner CI — tworzy konto): konto testowe, własny `sshd` na
127.0.0.1:2222, hasło w Pęku kluczy; aplikacja w trybie testu (`WAYPOINT_SMOKE_DIR`, `Smoke.swift`)
loguje się raz z Pęku kluczy, raz przez pytanie w karcie, wykonuje komendy i zapisuje zrzuty okna.
Zrzuty i logi są w artefakcie **Waypoint-mac-smoke**.
