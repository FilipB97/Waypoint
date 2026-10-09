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
| 2 | Terminal SSH w kartach (SwiftTerm + systemowe `ssh`: agent, `~/.ssh/config`), hasła w Pęku kluczy | ⏳ |
| 3 | RDP przez aplikację Microsoft **Windows App** (plik `.rdp` jak w wersji Windows) | — |
| 4 | Panel plików SFTP/FTP: przeglądanie, wysyłanie/pobieranie | — |
| 5 | Edytor plików (Monaco) z bezpiecznym zapisem — jak w wersji Windows | — |

Do czasu kroku 2 „Połącz" dla SSH otwiera połączenie w systemowym Terminalu (`ssh://`).
