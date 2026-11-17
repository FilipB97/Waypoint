# Waypoint na macOS: plan poprawek i funkcji

> **Dla Filipa:** Lista 14 kolejnych zmian w wersji na Maca, w ustalonej kolejności: najpierw trzy błędy RDP i błąd panelu plików, potem RDP (pełny ekran, dźwięk, pomiar wydajności), panel plików, sesje, SSH i edytor. Lista obejmuje tylko RDP, SSH i pliki z edytorem, bo tych funkcji używasz na co dzień. REST i rzadkie funkcje Windows trafiły na listę „później”.

Status: gotowy · Rozmiar: L (roadmapa, każda pozycja realizowana osobno) · Data: 2026-11-17 · Wersja bazowa: v3.1.5

## 1. Cel i kontekst

Wersja na Maca (`mac/App`, `mac/WaypointCore`, RDP przez FreeRDP w `mac/WaypointCore/Sources/RdpShim`) ma większość funkcji Windows. Na co dzień praca na niej jest jednak cięższa w trzech obszarach: w RDP (brak dźwięku i pełnego ekranu, znane błędy w shimie), w panelu plików (jedna operacja naraz, ubogie konflikty nazw, brak przeciągania do Findera) i przy pracy na wielu serwerach (brak przywracania sesji, podziału terminala i kart edytora).

Źródło faktów: statyczny przegląd kodu z 2026-11-17 (commit `c61043d`). Aplikacji nie uruchamiano. Notatka z przeglądu: `side-session-notes/…/mac-roadmap-findings.md` (poza repo).

## 2. Zakres

- **W zakresie:** 14 pozycji z sekcji 4, ich kolejność, rozmiar i kryteria akceptacji na poziomie roadmapy.
- **Poza zakresem:**
  - Czarny ekran i znikający pasek kart po rozłączeniu i ponownym wejściu do sesji RDP. Naprawia to osobna sesja.
  - Podpis Apple Developer ID i notaryzacja. Odrzucone, zostaje własny podpis (decyzja D2).
  - Szczegółowy projekt pozycji L. Każda dostaje własną specyfikację (decyzja D5).
  - Lista „później” (sekcja 5).

## 3. Stan obecny (stan na `c61043d`)

| Obszar | Stan | Wskaźnik |
|---|---|---|
| Układ klawiatury RDP | 4 reguły `contains()`, reszta = 0 (domyślny serwera); czytany raz przy połączeniu | `mac/WaypointCore/Sources/WaypointRDP/RdpConnection.swift:456-463`, `mac/App/Sources/Waypoint/RdpSession.swift:87` |
| Schowek przy zatrzymaniu | `clip_server_data_request` woła `clipboard_provide` na wątku kanału cliprdr bez sprawdzenia `stopping`; Swift czeka do 30 s na blok na wątku głównym. `freerdp_disconnect` czeka bez limitu na wątek cliprdr, więc `pthread_join` w `wp_rdp_stop` też czeka. Zamknięcie karty: bez zawieszenia (wątek główny wolny). Wyjście z aplikacji: ok. 5 s czekania (`afterStopping(timeout: 5)`), potem wyjście z żywym wątkiem FreeRDP, czyli dokładnie ta awaria przy `OPENSSL_cleanup`, przed którą chroni grupa `stopping` | `RdpShim.c:386-388, 869, 969`, `RdpSession.swift:141-150, 415-428`, `AppModel.swift:389`, `Entry.swift:29-32`; FreeRDP 3.32.1 `channels/client/addin.c:759` |
| `configure_log` | Przy zmianie trybu diagnostyki `WLog_SetLogAppenderType` zwalnia globalny appender bez blokady, a wątki innej żywej sesji wchodzą w jego `lock` w `WLog_Write` (use-after-free). Filtry dodawane przy każdym włączeniu przez `realloc(g_Filters)` bez synchronizacji z czytającymi (drugie okno UAF) i nigdy nieusuwane. Ścieżka `outputfilepath` jest bezpieczna (FreeRDP robi `_strdup`) | `RdpShim.c:237-256, 880`, `RdpSession.swift:88-91`; FreeRDP `winpr/libwinpr/utils/wlog/Appender.c:24-37, 156-166`, `wlog.c:228-257, 536-543`, `FileAppender.c:50-52` |
| Dźwięk RDP | Brak kanału rdpsnd; `wp_play_sound` to pusta funkcja; `AudioMode` zapisywany tylko w `.rdp` | `RdpShim.c:~229`, `RdpFile.swift:33-35`, `mac/scripts/build-freerdp.sh:61` |
| Pełny ekran RDP | Brak kodu; tylko zielony przycisk okna macOS | brak trafień `fullscreen` w `mac/App/Sources` |
| Potok klatek RDP | Cała klatka kopiowana do `Data` i nowy `CGImage` na wątku głównym przy każdym odświeżeniu; brudny prostokąt ignorowany; H.264 wyłączony (`WITH_FFMPEG=OFF`) | `RdpDisplayView.swift:146-161`, `RdpShim.c:174-185`, `build-freerdp.sh:55` |
| Panel plików | Jedna operacja naraz (`guard !busy` po cichu odrzuca drugą, a UI jej nie blokuje); konflikt tylko „Nadpisz/Anuluj” i tylko przy wysyłaniu; brak przeciągania na zewnątrz | `mac/App/Sources/Waypoint/FileSession.swift` (`run`, `overwriteQuestion`, `download`), `FilesView.swift` |
| Sesje | Brak przywracania kart po restarcie; jest tylko „Połącz na starcie” (`AutoConnectServerIds`) | `mac/WaypointCore/Sources/WaypointCore/MacSettings.swift:33`; Windows: `src/RdpManager/MainWindow.xaml.cs:202-230`, `SessionRestoreWindow.xaml` |
| Pasek menu / skrót | Brak `MenuBarExtra`/`NSStatusItem` i globalnego skrótu | Windows: `MainWindow.xaml.cs:335-339, 380-381` |
| Terminal | Brak podziału i broadcastu | Windows: `MainWindow.xaml.cs:143-149, 1954` |
| Edytor | Każdy plik to osobne okno i osobne połączenie (`FileSession(server:)`) | `mac/App/Sources/Waypoint/EditorDocument.swift:~57` |
| Dokumentacja | `docs/ROADMAP.md` opisuje stan v1.9.4 i RDP „przez Windows App”; `mac/README.md` krok 3d „pełny ekran, dźwięk: w toku” | `docs/ROADMAP.md`, `mac/README.md:66` |

## 4. Pozycje roadmapy

Każda pozycja to osobna gałąź, PR po polsku, CI (app-macos, build-and-test, core-linux) i wydanie: błąd `bump=patch`, usprawnienie lub funkcja `bump=minor` (decyzja D3). Testy pisze niezależny `harness:tester`. Kryteria ręczne sprawdza się na paczce `.app` z `mac/scripts/bundle-app.sh`, nie na gołym pliku wykonywalnym.

### 4.1 Błędy

| Id | Kol. | Pozycja | Rozm. | Kryterium akceptacji |
|----|---|---|---|---|
| B1 | 1 | **Układ klawiatury RDP.** Jawna tabela identyfikator `com.apple.keylayout.*` → KLID Windows zamiast `contains()`, obejmująca też warianty faktycznie obecne w macOS (Polish/PolishPro, Czech/Czech-QWERTY, Slovak-QWERTY, Hungarian-QWERTY, French/French-PC/French-numerical, British/British-PC, ABC-QWERTZ/ABC-AZERTY, Swedish-Pro, Italian-Pro, Spanish-ISO, Russian/RussianWin/Russian-Phonetic, Ukrainian-PC, SwissFrench/SwissGerman, Austrian, US International, Dvorak). Pełna tabela jest częścią PR i przeglądu. Nieznany układ daje 0 jak teraz. W PR opisane, czego FreeRDP 3 używa przy 0 (R5). W tym samym PR poprawka `docs/ROADMAP.md` i `mac/README.md` krok 3d (D4). | S | `swift test --package-path mac/WaypointCore --filter WaypointRDPTests` zawiera test tabeli, co najmniej: `PolishPro` → `0x415`, `Polish` → `0x10415`, `SwissGerman` → `0x807`, `French` → `0x40C`, `ABC-India` → `0x4009`, `British-PC` → `0x809`, nieznany → `0` (do potwierdzenia przy przeglądzie tabeli). Ręcznie: układ francuski na Macu, połączenie, w Notatniku sesji klawisz Q daje `a`. |
| B2 | 2 | **Zawieszenie schowka przy zatrzymaniu.** Oczekiwanie na dane schowka przenosi się z `RdpSession` (App) do `RdpConnection` (WaypointRDP): `stop()` je przerywa, a żądanie przy `stopping` kończy się od razu. Limit oczekiwania 5 s zamiast 30 s (do potwierdzenia). Wyjście z aplikacji czeka, aż wątek FreeRDP naprawdę się zakończy. | S | Test w `WaypointRDPTests` (core-linux z xrdp, obok testu schowka w `RdpConnectionTests.swift:288`): `onClipboardProvide` blokuje do przerwania, `stop()` wraca w < 2 s. Ręcznie, na paczce `.app`: skopiować tekst na Macu, Ctrl+V w sesji i od razu ⌘Q; aplikacja zamyka się w < 2 s i nie zostawia nowego raportu w `~/Library/Logs/DiagnosticReports`. |
| B3 | 3 | **`configure_log`.** Appender i filtry WLog zmieniane tylko wtedy, gdy nie żyje żadna sesja RDP (licznik żywych sesji w shimie). Zmiana trybu diagnostyki przy żywej sesji zaczyna działać od pierwszego połączenia po zamknięciu wszystkich sesji (podpowiedź w Ustawieniach). Filtry rejestrowane raz, zdejmowane przy wyłączeniu diagnostyki. Przy okazji zwolnienie starej ścieżki pliku (wyciek). | S | Test integracyjny w `RdpConnectionTests` (core-linux, xrdp): sesja A żywa z logowaniem, start sesji B z innym `log_dir`, 10 powtórzeń bez awarii. Ręcznie: sesja A z ruchem schowka, przełączenie diagnostyki RDP w Ustawieniach, połączenie sesji B; 10 powtórzeń bez awarii. Przegląd kodu potwierdza, że `WLog_SetLogAppenderType` i `WLog_AddStringLogFilters` nie są wołane przy licznikach > 0. |

| B4 | 4 | **Panel plików po cichu pomija operacje w trakcie przesyłania.** `run` odrzuca drugą operację (`FileSession.swift:208`), a menu kontekstowe, dwuklik, Delete, ⌘E, przeciąganie i nawigacja dalej działają (`FilesView.swift:101, 108, 170-206`). `startTransfer` przed `run` zmienia wspólny stan (`FileSession.swift:401-405`): kasuje anulowanie i podmienia opis pierwszego przesyłania. Poprawka: w trakcie przesyłania akcje modyfikujące są nieaktywne albo pokazują komunikat, a stan trwającego przesyłania jest nienaruszony. Nawigacja i otwieranie do edycji działają, jeśli nie wymagają kolejki (D12). | S | Ręcznie: w trakcie wysyłania dużego folderu przeciągnięcie pliku z Findera, Delete i zmiana nazwy dają komunikat albo są nieaktywne; „Anuluj” nadal przerywa pierwsze przesyłanie; powiadomienie po końcu ma właściwy opis. |

### 4.2 Usprawnienia

| Id | Kol. | Pozycja | Rozm. | Kryterium akceptacji |
|----|---|---|---|---|
| U1 | 5 | **Pełny ekran RDP z przechwyceniem klawiszy.** Polecenie w menu i skrót ⌃⌘F (wejście i wyjście; wyjście działa zawsze). W pełnym ekranie: ⌘ dalej jako Ctrl, ⌘Tab = Alt+Tab w Windows, ⌘Q i ⌘W idą do sesji jako Ctrl+Q/Ctrl+W (D9). Tylko to, co da się przechwycić bez zgody macOS na Dostępność/Monitorowanie wprowadzania (np. `presentationOptions` z `.disableProcessSwitching`); ⌘Spacja i Mission Control zostają na Macu (D10). Pasek kart chowa się i wysuwa przy górnej krawędzi. Rozdzielczość sesji idzie za ekranem tylko przy trybie „dopasuj”; stała rozdzielczość jest skalowana (do potwierdzenia). | M | Ręcznie: ⌃⌘F wchodzi w pełny ekran, ⌘Tab przełącza okna w Windows, nie na Macu; ⌘W zamyka kartę przeglądarki w sesji, nie kartę Waypointa; ⌃⌘F wraca do okna; najechanie na górną krawędź pokazuje pasek kart; brak prośby o uprawnienia systemu. |
| U2 | 7 | **Pomiar wydajności RDP.** Licznik FPS w trybie diagnostyki RDP (mała zmiana w kodzie, wydanie patch) i pomiar CPU (`top -pid` / Monitor aktywności) na realnej sesji: 1080p w oknie, pełny ekran (po U1, także 4K, jeśli jest monitor), przewijanie strony, wideo. Problem = przy przewijaniu w 1080p FPS < 30 albo CPU procesu > 60% jednego rdzenia (progi do potwierdzenia). Jeżeli tak, powstają nowe pozycje: przebudowa potoku klatek (M, tylko brudny prostokąt albo `IOSurface`) i dekoder H.264 (M) (D6). Wynik bez nazw serwerów i adresów. | S | Tabela CPU % i FPS dla każdego scenariusza dopisana do tego pliku i decyzja „przebudowa tak/nie” zatwierdzona przez Filipa. |
| U3 | 8 | **Konflikty nazw w plikach.** Przy wysyłaniu i pobieraniu: Nadpisz, Pomiń, Zmień nazwę, z opcją „Dla wszystkich” obowiązującą w jednej operacji. Zmień nazwę = automatycznie `nazwa (1).ext`, a gdy istnieje, kolejny numer; rozszerzenie liczone od ostatniej kropki, pliki z kropką na początku bez rozszerzenia. Folder, który istnieje: scalanie, a decyzja dotyczy plików w środku. Logika w osobnym typie w `WaypointCore`, tak aby kolejka (U4) i przeciąganie (U5) użyły jej bez przepisywania (R2). | M | Testy w `WaypointCoreTests` dla polityki: nadpisz, pomiń, `plik.txt` → `plik (1).txt`, istniejące `plik (1).txt` → `plik (2).txt`, `.bashrc` → `.bashrc (1)`, scalanie folderu. Ręcznie: wysłanie 3 plików, z których 2 istnieją, z „Pomiń” i „Dla wszystkich” wysyła 1 plik; pobranie pliku, który istnieje lokalnie, pyta o decyzję. |
| U4 | 9 | **Kolejka przesyłań.** Kilka operacji naraz albo w kolejce, widok postępu każdej, anulowanie pojedynczej. Panel nie blokuje się w trakcie przesyłania: przeglądanie, zmiana nazwy i nowy folder działają. Żadna operacja nie jest odrzucana po cichu. Wymaga własnej specyfikacji (`/harness:spec`). | L | Według specyfikacji pozycji. Minimum: w trakcie wysyłania dużego folderu można wejść do innego katalogu i utworzyć folder; druga operacja trafia do kolejki z widocznym stanem. |
| U5 | 10 | **Przeciąganie do Findera.** Z panelu zdalnego do Findera i z panelu zdalnego do lokalnego (`NSFilePromiseProvider`), z użyciem kolejki (U4) i konfliktów (U3). | M–L | Ręcznie: przeciągnięcie pliku i folderu do Findera tworzy je w miejscu upuszczenia, z postępem w kolejce. Konflikt nazwy w miejscu upuszczenia: najpierw sprawdzić, czy rozstrzyga go macOS; jeśli nie, przez politykę z U3. |
| U6 | 14 | **Edytor w kartach.** Wiele plików w jednym oknie edytora, jedno połączenie na serwer, współdzielone przez karty (wymaga U4, bo dziś `FileSession` wykonuje jedną operację naraz). Pytanie o niezapisane zmiany przy zamykaniu karty i okna. Wymaga własnej specyfikacji. | L | Według specyfikacji pozycji. Minimum dla serwera SFTP, bez otwartego panelu plików: otwarcie 5 plików daje jedno okno z 5 kartami i jeden proces `pgrep -fl 'ssh.*-s sftp'`. |

### 4.3 Nowe funkcje

| Id | Kol. | Pozycja | Rozm. | Kryterium akceptacji |
|----|---|---|---|---|
| F1 | 6 | **Dźwięk RDP: odtwarzanie.** Kanał rdpsnd z podsystemem `mac` (jest już w `libfreerdp-client3.a`, `Package.swift` linkuje AudioToolbox). `AudioMode` w znaczeniu modelu danych (`ServerInfo.cs:82`, `RdpFile.swift:32`: 0 = lokalnie, 1 = brak, 2 = na serwerze) i wybór w edytorze serwera, którego dziś na Macu nie ma (do potwierdzenia). Bez mikrofonu (D1). | S–M | Ręcznie: dźwięk systemowy Windows i wideo w przeglądarce sesji słychać na Macu; `AudioMode` = brak daje ciszę. Brak prośby o dostęp do mikrofonu. |
| F2 | 11 | **Przywracanie sesji po restarcie.** Zapis otwartych kart (serwer, typ karty) przy starcie i zamknięciu karty, nie tylko przy ⌘Q, w formacie rozszerzalnym o panele (F4) i karty edytora (U6). Przy starcie pytanie albo automatyczne otwarcie (wybór w Ustawieniach, wzór: Windows `SessionRestoreWindow`). Krótka specyfikacja rozstrzyga: karty szybkiego połączenia bez serwera, usunięte serwery, okna edytora, REST i VNC, nakładanie się z „Połącz na starcie”, pytania o hasło przy starcie. Hasła tylko w Pęku kluczy. | M | Ręcznie: 3 karty (RDP, SSH, pliki), ⌘Q, uruchomienie; te same 3 karty w tej samej kolejności po potwierdzeniu; serwer z „Połącz na starcie” otwarty tylko raz. |
| F3 | 12 | **Ikona w pasku menu i globalny skrót.** Ikona z listą przypiętych i ostatnich serwerów oraz szybkim połączeniem; globalny skrót otwierający szybkie połączenie spoza aplikacji. Oba wyłączalne w Ustawieniach; domyślna kombinacja i wartości domyślne w krótkiej specyfikacji. Gdy ikona jest włączona, zamknięcie ostatniego okna zostawia aplikację w pasku menu, a ⌘Q ją kończy; gdy wyłączona, jak teraz (`Entry.swift:37`) (D11). | M | Ręcznie: z innej aplikacji skrót otwiera szybkie połączenie Waypointa; wybór serwera z ikony otwiera kartę; po zamknięciu okna ikona zostaje, a wybór serwera otwiera okno z kartą. |
| F4 | 13 | **Panele i broadcast w SSH.** Podział karty terminala na kilka paneli i tryb wpisywania naraz do wybranych terminali. Wymaga własnej specyfikacji. | M–L | Według specyfikacji pozycji. Minimum: karta z 2 panelami SSH do różnych serwerów; w trybie broadcast `hostname` trafia do obu. |

### 4.4 Kolejność w skrócie

1 B1 → 2 B2 → 3 B3 → 4 B4 → 5 U1 → 6 F1 → 7 U2 → 8 U3 → 9 U4 → 10 U5 → 11 F2 → 12 F3 → 13 F4 → 14 U6

Zależności i miejsca styku:
- U5 wymaga U4 i U3. U4 używa typu konfliktów z U3.
- U6 wymaga U4 (współdzielone połączenie musi obsłużyć kilka operacji naraz).
- F2 zapisuje stan kart w formacie, który F4 i U6 rozszerzą.
- B4 jest tymczasowa: U4 zastąpi jej blokady kolejką.
- B1 i U1 zmieniają ścieżkę klawiatury w `RdpDisplayView.swift`; U1 zaczyna się po scaleniu B1.
- B2, B3 i F1 zmieniają `RdpShim.c`; zmiana `build-freerdp.sh` unieważnia cache FreeRDP w CI (`.github/workflows/mac.yml:64`), więc pierwszy przebieg CI jest dłuższy.
- Scenariusz 4K w U2 wymaga U1.

Kolejność wynika z priorytetu (RDP najczęściej używany) i z tych zależności.

## 5. Lista „później” (bez kolejności i rozmiaru)

- RDP: wiele monitorów, osobny login do RD Gateway i `GatewayUsageMethod`, RemoteApp we wbudowanym kliencie, drukarki, karta inteligentna, wybór folderu przekierowanego dysku (teraz cały katalog domowy), stan „ponowne łączenie” w karcie, zmiana układu klawiatury w trakcie sesji.
- SSH: automatyczne ponowne łączenie i skrót, zapis transkryptu, stan tuneli (zajęty port) oraz `-R`/`-D`, opcja Option jako Meta, zmiana nazwy karty.
- Pliki: skróty F2, ⌘R, ⌘N, filtr, ukryte pliki po stronie serwera, wpisanie ścieżki i zakładki, chmod, podgląd (Quick Look), przypinanie certyfikatu FTPS, ostrzeżenie o FTP/Telnet bez szyfrowania.
- Edytor: wykrywanie zmian pliku na serwerze, rozmiar czcionki z ustawień, większe pliki niż 10 MB w trybie tylko do odczytu.
- Lista serwerów: zaznaczanie wielu i operacje zbiorcze, przeciąganie między grupami, filtr tagów.
- Aplikacja: okno „Co nowego”, wybór języka w aplikacji, osadzony VNC.
- REST: anulowanie żądania, konfigurowalny limit czasu, multipart, duże odpowiedzi, ciasteczka, generowanie kodu.
- RDP (zależnie od U2): przebudowa potoku klatek, H.264.

## 6. Dane i bezpieczeństwo

- Repozytorium jest publiczne: w commitach, PR i notatkach z pomiarów (U2) nie podajemy nazw serwerów ani adresów IP.
- F2 zapisuje tylko identyfikatory serwerów i typy kart w `settings.json`. Hasła zostają w Pęku kluczy.
- F3: globalny skrót wymaga zgody macOS tylko wtedy, gdy wybrany mechanizm jej wymaga. Do sprawdzenia w specyfikacji pozycji.
- U1: przechwycenie klawiszy obowiązuje tylko w pełnym ekranie i tylko przy fokusie na sesji. Skrót wyjścia działa zawsze.
- Format plików wspólnych z Windows (`servers.json`, `credprofiles.json`) się nie zmienia. Nowe ustawienia mają nazwy z prefiksem `Mac` albo nowe pola, które Windows ignoruje.

## 7. Wdrożenie i wycofanie

Tak jak dotychczas: PR → CI → merge → `gh workflow run release.yml -f bump=patch|minor`. Wycofanie to wydanie poprzedniej wersji albo revert PR i nowe wydanie patch. Żadna pozycja nie zmienia formatu danych w sposób nieodwracalny.

## 8. Ryzyka i otwarte kwestie

| # | Ryzyko / pytanie | Właściciel | Decyzja |
|---|---|---|---|
| R1 | Pytanie o hasło Pęku kluczy po każdej aktualizacji zostaje (brak Developer ID). | Filip | Przyjęte (D2) |
| R2 | U3 powstaje przed U4: kolejka musi przejąć logikę konfliktów bez przepisywania. | implementacja U3 | Logika konfliktów w osobnym typie w `WaypointCore` (wymóg w U3) |
| R3 | U1: czy `presentationOptions` z `.disableProcessSwitching` naprawdę oddaje ⌘Tab aplikacji bez zgody systemu. | implementacja U1 | Sprawdzić na początku U1; jeśli nie, pokazać Filipowi przed dalszą pracą (bez proszenia o uprawnienia, D10) |
| R4 | F1: czy dźwięk wymaga przebudowy FreeRDP. | critic | Nie: `mac_freerdp_rdpsnd_client_subsystem_entry` jest w `libfreerdp-client3.a`, AudioToolbox już linkowany |
| R7 | Windows: `RdpConnect.cs:36` przekazuje `AudioMode` wprost do `AudioRedirectionMode` MSTSC (1 = zdalnie), a model danych mówi 1 = brak (`ServerInfo.cs:82`). Prawdopodobny błąd po stronie Windows. | Filip | Poza tym planem (wersja Windows), do osobnego zgłoszenia |
| R5 | B1: nie sprawdzono, jakiego układu FreeRDP 3 używa, gdy dostaje 0. | implementacja B1 | Sprawdzić i opisać w PR |
| R6 | B3: czy appender plikowy FreeRDP kopiuje `outputfilepath`. | skeptic | Kopiuje (`FileAppender.c:50-52`), nie jest problemem |

## 9. Decyzje

| Id | Decyzja | Kto | Data |
|----|---|---|---|
| D1 | Dźwięk RDP: tylko odtwarzanie, bez mikrofonu | Filip | 2026-11-17 |
| D2 | Bez Apple Developer ID, zostaje własny podpis | Filip | 2026-11-17 |
| D3 | Jeden PR na pozycję, wydanie po każdej (błąd patch, reszta minor) | Filip | 2026-11-17 |
| D4 | Nieaktualne `docs/ROADMAP.md` i `mac/README.md` poprawiane w pierwszym PR (B1) | Filip | 2026-11-17 |
| D5 | Pozycje L (U4, U6, F4) dostają własną specyfikację przed implementacją, pozycje M krótką | Claude, zatwierdził Filip | 2026-11-17 |
| D6 | Przebudowa potoku klatek i H.264 tylko wtedy, gdy pomiar U2 pokaże problem | Claude, zatwierdził Filip | 2026-11-17 |
| D7 | Czarny ekran po ponownym połączeniu RDP poza tym planem (osobna sesja) | Claude, zatwierdził Filip | 2026-11-17 |
| D8 | Horyzont: 14 pozycji (13 z wywiadu + B4), reszta na liście „później”; cel: domknąć to, czego Filip używa, a nie zgodność 1:1 z Windows | Filip | 2026-11-17 |
| D9 | Pełny ekran RDP: ⌘ jako Ctrl, ⌘Tab = Alt+Tab, ⌘Q/⌘W do sesji, wyjście ⌃⌘F | Filip (skrót ⌃⌘F: Claude) | 2026-11-17 |
| D10 | Bez zgody macOS na Dostępność/Monitorowanie wprowadzania; ⌘Spacja i Mission Control zostają na Macu | Filip | 2026-11-17 |
| D11 | Z włączoną ikoną w pasku menu aplikacja działa dalej po zamknięciu ostatniego okna | Filip | 2026-11-17 |
| D12 | Błąd cichego pomijania operacji w panelu plików jako B4 (S, patch) po B3 | Filip | 2026-11-17 |

## Weryfikacja

- 2026-11-17, baza `c61043d`, przebieg 1: `harness:critic` (12 uwag: 1 blokująca, 9 istotnych, 2 drobne) i `harness:skeptic` (3 twierdzenia o kodzie: B2 i B3 częściowo błędne w szczegółach, B4 potwierdzony z dodatkowym błędem).
- Naniesione: kryteria B2 i B3 możliwe do sprawdzenia (bez ASan, testy na core-linux z xrdp, właściwy scenariusz wklejania), poprawione przyczyny B2/B3, jawna tabela B1, progi U2, definicje U3, zależności (U6 wymaga U4, F2 rozszerzalny), znaczenie `AudioMode` w F1 (R4 zamknięte), R6 zamknięte, nowa pozycja B4, decyzje D9–D12 z rundy 5.
- Werdykt: **gotowy**. Otwarte punkty oznaczone „do potwierdzenia” (limit 5 s w B2, tabela KLID w B1, progi U2, wybór `AudioMode` w edytorze w F1, skalowanie stałej rozdzielczości w U1) rozstrzyga przegląd PR danej pozycji.
