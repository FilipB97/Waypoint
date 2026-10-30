# Waypoint dla macOS

Natywna aplikacja (SwiftUI) obok wersji Windows. Ten sam format danych: lista serwerów to ten sam
JSON co `servers.json` w Windows, a profil wyeksportowany z Windows (Ustawienia → Eksportuj profil…)
importuje się na Macu bez konwersji. Pola, których Mac nie używa, są przechowywane i zapisywane
z powrotem bez zmian.

## Układ

| Katalog | Co | Gdzie się buduje |
|---|---|---|
| `WaypointCore/` | logika: model serwera, magazyn, import, SSH/askpass, SFTP, FTP (libcurl), edytor, teksty pl/en | macOS **i Linux** (`swift test`) |
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
| 3 | RDP przez aplikację Microsoft **Windows App** (plik `.rdp` jak w wersji Windows) | ✅ |
| 3b | Wbudowany klient RDP (FreeRDP) w karcie: hasło z Pęku kluczy, certyfikat, rozdzielczość okna, klawiatura, mysz | ✅ |
| 3c | RDP: schowek tekstowy, pełny ekran, dyski i dźwięk | w toku |
| 4 | Panel plików SFTP: przeglądanie, wysyłanie/pobieranie (także folderów), zmiana nazwy, usuwanie | ✅ |
| 4b | FTP/FTPS: ten sam panel plików i edytor | ✅ |
| 5 | Edytor plików (Monaco) z bezpiecznym zapisem — jak w wersji Windows | ✅ |

## Funkcje przeniesione z wersji Windows

| Funkcja | Windows | macOS |
|---|---|---|
| Zapis przez sudo w edytorze | ✅ | ✅ |
| Snippety komend (zmienne serwera, ten sam `snippets.json`) | Ctrl+Shift+K, Ctrl+Shift+1…9 | ⇧⌘K, ⌥⌘1…9 (⇧⌘3/4/5 to zrzuty ekranu w macOS) |
| Paleta poleceń | Ctrl+P | ⌘K |
| Szybkie połączenie (`user@host:port`, `DOMENA\user@host`) | ✅ | w palecie (⇧⌘N) |
| Szukanie w terminalu, rozmiar czcionki | ✅ | ⌘F, ⌘+ / ⌘− / ⌘0 |
| Duplikowanie i przestawianie kart | ✅ | ✅ (menu karty, przeciąganie) |
| Kropki osiągalności + opóźnienie (sonda TCP w tle) | ✅ | ✅ (Ustawienia ⌘,) |
| Zwijanie grup, zmiana nazwy grupy, przenoszenie do grupy | ✅ | ✅ (menu nagłówka i serwera) |
| Kolejność serwerów przeciąganiem | ✅ | ✅ (w obrębie sekcji) |
| Pulpit: liczniki, ostatnio używane, szybkie połączenie, aktywność | ✅ | ✅ (⇧⌘D, ikona domku) |
| Dziennik połączeń (`connections.log`, ten sam format) | ✅ | ✅ |
| Profile poświadczeń (ten sam `credprofiles.json`, hasło w Pęku kluczy) | ✅ | ✅ (ikona klucza na pasku, Narzędzia → Profile poświadczeń, edytor serwera) |
| Hasło serwera w edytorze | ✅ | ✅ (Pęk kluczy) |
| RDP w karcie | mstscax | FreeRDP (albo Microsoft Windows App — wybór w edycji serwera) |
| „Połącz jako…" | ✅ | ✅ |
| Generator haseł / tokenów / GUID | ✅ | ✅ (⌥⌘G) |
| Telnet, port szeregowy | ✅ | ✅ (terminal karty; `/dev/cu.*`) |
| VNC | wbudowany | Udostępnianie ekranu macOS (`vnc://`) |
| Strony WWW | przeglądarka | przeglądarka |
| Import: mRemoteNG, RDCMan, RDM, FileZilla | ✅ | ✅ (Plik → Importuj z innego programu) |
| Eksport profilu | ✅ | ✅ (format Windows, bez haseł) |
| Dwa panele plików (lokalny + zdalny) | ✅ | ✅ |
| Wake-on-LAN | ✅ | ✅ |
| Sprawdzanie aktualizacji | ✅ | ✅ (wydania z plikiem `-mac.zip`) |
| Motywy (jasny/ciemny, presety terminala, akcent) | ✅ | ✅ |
| Karta w osobnym oknie | ✅ | ✅ |
| Klient REST (kolekcje, foldery, środowiska, Bearer/Basic z dziedziczeniem, skrypty pm.*, historia) | ✅ | ✅ (ten sam `rest.json` i `environments.json`) |
| Import kolekcji i środowisk Postmana | ✅ | ✅ (Plik → Importuj kolekcję Postman) |

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

## Lista serwerów i pulpit

Osiągalność: co `ReachabilityIntervalSec` (5–3600 s, domyślnie 30) nieblokujące TCP connect do
host:port każdego serwera (najwyżej 32 naraz, limit czasu 1–60 s) — kropka na awatarze i, po
włączeniu, opóźnienie w ms. Serwery COM/WWW/REST są pomijane, jak w Windows. Grupy zwija się
strzałką nagłówka (stan zapamiętany), menu nagłówka zmienia nazwę grupy we wszystkich jej
serwerach, menu serwera przenosi go do innej grupy. Kolejność zmienia się przeciąganiem w obrębie
sekcji. Pulpit (bez zaznaczonego serwera, ⇧⌘D) pokazuje liczniki, ostatnio używane serwery,
pole szybkiego połączenia i aktywność z `connections.log` (ten sam format linii co w Windows,
tylko metadane — bez haseł; wyłączalny w Ustawieniach). Ustawienia zapisują się w `settings.json`
pod tymi samymi nazwami pól co w Windows.

## Telnet, port szeregowy, VNC, WWW

Telnet i port szeregowy działają w tym samym terminalu co SSH: karta uruchamia plik wykonywalny
Waypointa w trybie pomocniczym (`--waypoint-helper telnet|serial …`) w pseudo-terminalu, więc
karty, szukanie, czcionka i snippety działają tak samo. Telnet negocjuje ECHO i SGA, resztę opcji
odrzuca (port maszyny stanów z Windows); port szeregowy to 8N1 bez kontroli przepływu, urządzenie
`/dev/cu.*` wybiera się w edycji serwera, prędkość jest w polu portu (jak w Windows). VNC otwiera
systemowe Udostępnianie ekranu, strony WWW — domyślną przeglądarkę (tylko http/https).

## Klient REST

Wpis z protokołem REST otwiera konsolę: drzewo żądań i folderów, metoda, adres, parametry,
nagłówki, treść (JSON/tekst/formularz), uwierzytelnianie Bearer/Basic dziedziczone po folderach
i kolekcji, skrypty pre-request i testy w API Postmana (`pm.*`, JavaScriptCore, limit 5 s),
odpowiedź (treść sformatowana, nagłówki, „Wysłane", wyniki testów) i historia. Kolekcje leżą
w `rest.json`, środowiska w `environments.json` — te same pliki co w Windows; sekrety
uwierzytelniania w Pęku kluczy.

## Pliki (SFTP)

Klient SFTP v3 jest w `WaypointCore` i rozmawia przez systemowe `ssh -s … sftp` — logowanie jest
więc identyczne jak w terminalu (config, agent, Pęk kluczy przez askpass). Pobieranie i wysyłanie
są potokowe (16 bloków po 64 KB w locie), foldery przenoszą się rekurencyjnie, nazwy z serwera są
sprawdzane (`../` z wrogiego serwera nie zapisze pliku poza wybranym katalogiem), przerwany
transfer nie zostawia połowy pliku. Karta „Pliki" otwiera się dla serwerów SFTP i — z menu
„Pliki (SFTP)" — dla serwerów SSH. Wysyłanie: przycisk albo przeciągnięcie z Findera.

Testy klienta na prawdziwym OpenSSH: `WAYPOINT_SFTP_TEST=host:port:user:klucz[:known_hosts] swift test`.

## FTP / FTPS

Klient FTP (`FtpClient`) stoi na libcurl — systemowej bibliotece macOS — bo FTPS wymaga przełączenia
gotowego połączenia na TLS (AUTH TLS) i wznowienia sesji TLS na kanale danych, czego Network.framework
nie daje. Jeden uchwyt curl na kartę (połączenie sterujące trwa między operacjami), lista z `LIST`
(format uniksowy i IIS — vsftpd nie ma `MLSD`), TLS 1.2 (serwery wymagające wznowienia sesji losowo
odrzucają transfery przy TLS 1.3). Hasło z Pęku kluczy albo pytanie w karcie; certyfikat
samopodpisany wymaga świadomej zgody, zapamiętanej dla serwera. Edycja przez FTP zapisuje w miejscu
(FTP nie ma atomowej podmiany ani odczytu uprawnień) i mówi o tym.

Testy na prawdziwym vsftpd (zwykły FTP, FTPS jawne i niejawne): `scripts/core-integration-linux.sh`
w CI albo lokalnie `WAYPOINT_FTP_TEST=host:user:hasło:portFTP:portFTPS:portFTPSniejawny swift test`.

## Edytor plików

Pliki → menu „Edytuj w Waypoint" (⌘E). Pliki roota (i inne bez prawa zapisu) można zapisać przez
**sudo** (`SudoWrite`): treść idzie przez SFTP do pliku 0600 w /tmp, a jedno polecenie `sudo -S sh -c`
na serwerze kopiuje właściciela i uprawnienia (`--reference`, GNU) i podmienia plik atomowo; bez GNU
coreutils — zapis w miejscu z zachowaniem właściciela. Hasło sudo idzie na stdin (nigdy w linii
poleceń): zapisane dla sudo, potem hasło logowania, a gdy sudo je odrzuci — pytanie z zapisem. Ta sama strona edytora i ta sama przycięta paczka Monaco co
w wersji Windows (`src/RdpManager/Assets/`), rozpakowane do zasobów `.app` i serwowane przez
WKURLSchemeHandler (`wpeditor://app`), z nakładką emulującą `window.chrome.webview`. Zapis jak
w Windows: format pliku zachowany, wykrywanie zmian na serwerze, plik tymczasowy + atomowa
podmiana z zachowaniem uprawnień, właściciela, grupy i dowiązań (`SafeWrite` — macierz przypadków
sprawdzona na OpenSSH), pliki roota tylko do odczytu. Edytor ma własne połączenie.

## RDP

Domyślnie pulpit otwiera się **w karcie Waypointa** — wbudowany klient na FreeRDP 3
(`WaypointCore/Sources/RdpShim` + `WaypointRDP`), statycznie wlinkowany (FreeRDP + OpenSSL budowane
przez `scripts/build-freerdp.sh`, w CI z pamięci podręcznej). Hasło bierze z Pęku kluczy (serwera
albo profilu poświadczeń) — pytanie nad kartą pojawia się tylko, gdy go brak albo serwer je odrzuci
(z opcją zapisu). Certyfikat serwera: przy pierwszym połączeniu karta z odciskiem
(„Zaufaj zawsze / Tylko teraz / Nie łącz"), zmiana certyfikatu to wyraźne ostrzeżenie; poziom
„nie sprawdzaj" z edycji serwera pomija pytanie. Rozdzielczość sesji = rozmiar karty (Retina 200%),
zmiana rozmiaru okna zmienia ją w locie (Display Control). Klawiatura: ⌘ działa jak Ctrl (⌘C/⌘V
w sesji), polskie znaki z Option idą jako Unicode, Option z klawiszami specjalnymi = Alt; ⌘W, ⌘Q,
⌘1–9 zostają przy Waypoincie. Brama RD (te same dane logowania), sesja administracyjna, schowek.

W edycji serwera można zamiast tego wybrać Microsoft **Windows App**: „Połącz" zapisuje wtedy plik
`.rdp` (te same pola co w wersji Windows) do `~/Library/Caches/Waypoint/rdp/` i otwiera go tam.
RemoteApp zawsze idzie tą drogą. Plik `.rdp` z portalu firmy da się zaimportować
(Plik → Importuj plik .rdp…), a serwer RDP wyeksportować do pliku.

Testy: na Linuksie w CI prawdziwy serwer xrdp — połączenie, TLS, kolory pikseli, zmiana
rozdzielczości, wejście, odrzucony certyfikat, zamknięty port. Budowa lokalna z klientem:
`mac/scripts/build-freerdp.sh ~/freerdp && WAYPOINT_FREERDP=~/freerdp mac/scripts/bundle-app.sh`
(bez `WAYPOINT_FREERDP` RDP otwiera Windows App).

## Test end-to-end w CI

`scripts/smoke-test.sh` (tylko runner CI — tworzy konto): konto testowe, własny `sshd` na
127.0.0.1:2222, hasło w Pęku kluczy; aplikacja w trybie testu (`WAYPOINT_SMOKE_DIR`, `Smoke.swift`)
loguje się raz z Pęku kluczy, raz przez pytanie w karcie, wykonuje komendy i zapisuje zrzuty okna.
Zrzuty i logi są w artefakcie **Waypoint-mac-smoke**.
