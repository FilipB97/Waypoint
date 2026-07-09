# Plan refaktoryzacji `MainWindow.xaml.cs` (god object → kontrolery/serwisy)

## Kontekst

`src/RdpManager/MainWindow.xaml.cs` to **6016 linii, ~260 metod, ~70 pól** w jednej klasie
`partial class MainWindow` (bez `#region`, jedynie komentarzowe banery). Skupia w sobie wszystkie
odpowiedzialności aplikacji: update/download, reachability/WOL, drzewo serwerów (render + drag&drop +
multiselect), pasek kart (karty + grupy + split), tryby fullscreen/focus, oraz cykl życia sesji dla 8
protokołów. To udokumentowany dług: [`REVIEW.md`](REVIEW.md) **B1/B2** (god object + „MVVM tylko z
nazwy"), **B3/B4** (zduplikowany cykl życia RDP), oraz [`ROADMAP.md`](ROADMAP.md) „Known issue —
deliberately deferred".

**Cel:** rozbić monolit na nazwane kontrolery/serwisy, przyrostowo i bezpiecznie. Środowisko CI/dev na
Linuksie **nie zbuduje** projektu (`net8.0-windows` + WPF + ActiveX RDP), a testów UI nie ma — więc
każdy krok musi być czysto mechaniczny, weryfikowany buildem CI (Windows) i **ręczną regresją** przed
scaleniem. Zakres to **B1** (ekstrakcja) + **B3/B4** (dedup cyklu życia). **B2 (przepisanie na MVVM)
jest poza zakresem.**

> Uwaga: numery linii z pierwotnego zarysu w review były nieaktualne (plik urósł z 5096 → 6016).
> Aktualne: update = **L297-541** (nie 288-497); `MainWindow.WireEvents` = **L4417-4476** (nie
> 3612-3671); `SessionWindow.WireEvents` = **L160-189** (bez zmian). `REVIEW.md`/`ROADMAP.md` warto
> przy okazji zaktualizować (osobny drobny commit).

## Podejście — „back-reference move-method"

Każda ekstrakcja to **jeden PR**. Wzorzec dla każdego kroku:

1. **Nowa klasa** w `Services/` lub `Controllers/` (namespace `RdpManager.Services` /
   `RdpManager.Controllers`), np.:
   ```csharp
   namespace RdpManager.Services
   {
       internal sealed class UpdateService
       {
           private readonly MainWindow _owner;
           public UpdateService(MainWindow owner) { _owner = owner; }
           // przeniesione metody 1:1
       }
   }
   ```
2. **Move-method 1:1.** Przenoszone metody idą bez zmiany logiki. Jedyne dozwolone transformacje:
   - odwołania do składowych MainWindow → prefiks `_owner.` (te składowe zmieniają `private` →
     **`internal`**; ten sam assembly, więc `internal` wystarcza — **nie** `public`),
   - pola należące **wyłącznie** do danej odpowiedzialności przenosimy **do** kontrolera,
   - pola współdzielone (`_settings`, `_active`, `_sessions`, `_vm`) **zostają** w MainWindow
     (dostęp `_owner.`).
3. **Shimy dla handlerów z XAML.** Handlery podłączone w `MainWindow.xaml` (`Click=`, `TextChanged=`, …)
   **muszą** dalej istnieć na `MainWindow`. Zamiast edytować XAML zostawiamy 1-linijkowy shim:
   ```csharp
   private void Update_Click(object s, RoutedEventArgs e) => _update.Update_Click(s, e);
   ```
   Handlery podłączane runtime'owo (`+=`, domknięcia) przenosimy w całości do kontrolera.
4. **Instancjacja** w `Window_Loaded` (po `InitializeComponent`, gdy kontrolki `x:Name` istnieją):
   `_update = new UpdateService(this);`. Kolejność tworzenia = kolejność zależności (patrz przypisy).
5. **Build w CI + ręczna regresja tej jednej odpowiedzialności + merge.** Dopiero potem następny PR
   (każdy PR odgałęziany od aktualnego `master`, mergowany sekwencyjnie).

**Reguła nadrzędna:** żadnych zmian logiki, nazw ani dedupu poza jawnym B3/B4 w PR 6. Bloki
podłączania eventów (`+=`) przenosimy w całości, nie zmieniając ich kolejności (to główne źródło
błędów, których nie złapie ani build, ani testy).

## Sekwencja (od najbardziej izolowanego)

Kolejność jak w zarysie; zwalidowana względem sprzężeń. Sprzężenia współdzielone (`_settings`
~150 ref, `_active` 83, `_sessions` 54, `_vm` 50) zostają w MainWindow aż do PR 6.

### PR 1 — `Services/UpdateService.cs`  ⟵ pierwszy, najmniej powiązań
- **Metody:** update core **L297-541** (`CurrentVersion`, `CheckForUpdatesAsync`,
  `FetchLatestReleaseAsync`, `CheckUpdatesNow_Click`, `Update_Click`, `OpenReleasePage`,
  `DownloadFileAsync`, `IsTransientDownloadError`, `DownloadOnceAsync`, `IsValidExe`) + panel
  About/changelog **L968-1130** (`ShowAboutUpdateAvailable`, `SetAboutUpToDate`, `LoadChangelogAsync`,
  `BuildChangelog`, `ChangeKindLabel/Brush`, `MakePill`, `AboutWhatsNew_Click`).
- **Pola własne (przenieść):** `_updateTimer` (L64), `_updateChecking` (L65), `_update` (L122),
  `_updating` (L123), `_prevRunVersion` (L297), `_changelog` (L1001), `_changelogLoading` (L1002).
- **Współdzielone przez `_owner.`:** `_settings`, kontrolki `UpdateBtn`/`AboutUpdateCard`/`ChangelogList`,
  alias `L(...)`. **Uwaga:** `Window_Closing` (L1273) czyta `_updating` żeby pominąć potwierdzenie —
  wystawić `internal bool IsUpdating` lub czytać pole przez `_owner._update`.
- **Shimy XAML:** `CheckUpdatesNow_Click`, `Update_Click`, `AboutWhatsNew_Click` (linki
  `AboutRepo/License/ReportIssue_Click` mogą zostać w MainWindow).
- **Reuse:** `Core/UpdateCheck.cs` (`ReleaseInfo`, sprawdzenie GitHub), `Core/CodeSign.cs` (pinning
  thumbprintu), bootstrap w `App.xaml.cs`. UpdateService to głównie orkiestracja UI wokół `Core`.

### PR 2 — `Services/ReachabilityService.cs`
- **Metody:** **L5861-5977** (`CheckReachabilityAsync`, `WakeServer`, `DiagnoseServer`, `ProbeAsync`).
- **Pola własne:** `_reachTimer` (L62), `_reachBusy` (L63), `_latencySamples` (L46),
  `ProbeConcurrency` (L5949, static), `_probeTimeoutMs` (L5954, static).
- **Sprzężenie do rozwiązania:** pętla wpisuje wyniki do słowników drzewa `_serverStatusDot`/
  `_serverLatency`/`_serverActivate` i czyta `_vm.Servers`. Drzewo jest dopiero w PR 3 — więc **na
  razie** Reachability sięga po nie przez `_owner.` (`internal`). W PR 3, gdy słowniki przejdą do
  `ServerTreeController`, zamiast bezpośredniego dostępu wystawić na kontrolerze drzewa metodę-szew
  `internal void SetRowStatus(ServerInfo, ServerStatus, double rttMs)` i wołać ją z Reachability.
- **Reuse:** `Core/WakeOnLan.cs` (`Send`, walidacja MAC). `ProbeAsync` jest już async + `SemaphoreSlim`
  (A4 naprawione wcześniej) — **nie** zmieniać.

### PR 3 — `Controllers/ServerTreeController.cs`
- **Metody:** pasmo drzewa **L2292-2925** (`BuildServerTree`, `RenderTree`, `SearchBox_TextChanged`,
  `BuildGroupHeader`, `ToggleGroupCollapse`, `RenameGroup`, `TogglePin`, `BuildServerRow/Default/Minimal`,
  `AddLatencyLabel`, `WireServerRow`, `RowRestBackground`, multiselect `ToggleSelect`/`RangeSelect`/
  `ClearMultiSelect`/`RefreshSelectionVisuals`, `BuildServerContextMenu`, `UpdateActiveRows`, drag&drop
  `ReorderServer`/`ShowDropIndicator`/`ClearDropIndicator`/`FlashRow`) + `WireTreeFileDrop` (L5451).
- **Pola własne:** słowniki wierszy `_serverRows`/`_serverActivate`/`_serverStatusDot`/`_serverLatency`
  (L37-43), stan drag/multiselect `_dragStartPoint`/`_dragCandidate`/`_didDrag`/`_multiSelect`/
  `_selectAnchor`/`_visibleOrder`/`_dropAdorner`/`_dropRow` (L87-98).
- **Współdzielone przez `_owner.`:** `_vm` (lista serwerów — zostaje), `_settings`, `_tabGroups`
  (przynależność), kontrolki `ServerTree`/`SearchBox`/`TreeEmptyHint`/`ProtoFilterBar`, oraz wywołania
  CRUD/connect (`_owner.OpenServer`, `_owner.EditServer`, `_owner.DeleteServer`). **CRUD serwerów
  (L5234+) zostaje w MainWindow** — drzewo je tylko woła.
- **Handoff z PR 2:** dodać `SetRowStatus(...)`; Reachability zmienia dostęp z `_owner._serverStatusDot`
  na `_owner.Tree.SetRowStatus(...)`. W `Window_Loaded` tworzyć `_tree` **przed** `_reach`.

### PR 4 — `Controllers/TabStripController.cs`
- **Metody:** pasmo kart **L3261-3963** (`BuildTab/Default/Minimal`, `BuildTabClose`, `WireTab`,
  `RefreshTabStyles`, `RefreshTabTitles`, `CloseOtherSessions`, `DuplicateSession`, `MoveTabTo`/`MoveTab`,
  grupy: `GroupOf`, `NextGroupColor`, `DetachServerFromGroups`, `CreateGroupFromTab`, `AddToGroup`,
  `GroupTabs`, `RemoveFromGroup`, `Ungroup`, `SaveTabGroups`, `LoadTabGroups`, `DetachTab`,
  `ApplyTabStripStyle`, `ShowTabDropIndicator`/`ClearTabDropIndicator`, `NormalizeGroupOrder`,
  `RebuildTabStrip`, `BuildGroupContainer`, `BuildGroupMenu`, `PopulateTabGroupItems`).
- **Pola własne:** `_tabUnderline`/`_tabStatus`/`_tabName`/`_tabClose` (L50-53), **`_tabGroups`** (L56 —
  właścicielem zostaje TabStrip; drzewo czyta przez `_owner.Tabs`), pulse `_tabPulseOn`/`_tabPulseCooldown`
  (L691-692), drag `_tabDragStart`/`_tabDragSession`/`_tabDidDrag` (L3588-90), `_tabDropTarget` (L3768).
- **Współdzielone przez `_owner.`:** `_sessions`/`_active` (zostają do PR 6), kontrolki
  `TabStrip`/`TabStripHost`/`TabScroller`/`SessionContainer`, wywołania `_owner.Activate`,
  `_owner.RequestCloseSession`.
- **Uwaga:** `BroadcastToSsh` (L3509) i **split view** (`EnterSplit`/`ExitSplit`/`OnPaneFocused`/
  `ShowSplitDropZone`/`HideSplitDropZone`, L3114-3176) **nie** idą tu — to I/O sesji i kanwa; trafiają
  do SessionManager (PR 6).

### PR 5 — `Controllers/FullscreenController.cs` + `Controllers/FocusModeController.cs` (jeden PR, dwie klasy)
- **Fullscreen — metody:** **L4572-4835** (`ToggleFullscreen`, `EnterFullscreen`, `ExitFullscreen`,
  `TryGetFullscreenRectDip`, `MonitorCount`, `Minimize_Click`, `FsCursorPollTick`, `FsPopup_MouseLeave`,
  `ToggleFlyout_Click`, `TogglePin_Click`, `TabScroller_PreviewMouseWheel`, `CollapseFlyout`,
  `FsFlyoutSearch_TextChanged`) + `PlaceFsPopup` (L2274), `FsBarThumb_DragDelta` (L2283) + budowniczowie
  flyoutu `BuildFlyoutLists`/`BuildFlyoutRow`/`HandleFlyoutClick` (L5008-5102).
  - **Pola własne:** `_prevStyle…_prevScale`/`_isFullscreen` (L76-82), `_fsPinned`/`_fsBarOffset`
    (L83-84), `_fsBarDelay`/`_fsCursorPoll` (L116-117), `_fsMonRect` (L124).
- **Focus — metody:** **L654-833** (`IsImmersive`, `UpdateImmersive`, `StartTabStripRepaintPulse`,
  `TabStripRepaintPulse`, `ToggleFocus_Click`, `FocusPeekBackground`, `ShowFocusPeek`, `HideFocusPeek`,
  `FocusPeekPollTick`, `FocusMinimize_Click`, `FocusRestore_Click`, `FocusClose_Click`).
  - **Pola własne:** `_focusPeekPoll`/`_focusPeekDelay`/`_focusPeeking`/`_focusOverride` (L118-121).
- **Sprzężenia:** deklaracje **P/Invoke (L4697-4734)** są współdzielone z `SendCtrlAltDel` (VK_*,
  keybd_event) → **zostają w MainWindow** (dostęp `_owner.`), by nie dublować `DllImport`.
  `IsImmersive`/`UpdateImmersive` (immersive = fullscreen **lub** focus) to punkt koordynacji →
  **zostaje w MainWindow**; oba kontrolery wołają `_owner.UpdateImmersive()`. Oba czytają `_active` i
  kontrolki chrome (`Sidebar`/`Rail`/`FsPopup`/`FocusPeekPopup`/`RootScale`) przez `_owner.`.

### PR 6 — `Controllers/SessionManager.cs` (+ dedup B3/B4)  ⟵ największy, ostatni
- **Metody:** cały cykl życia **L2927-4234** (`LaunchServer`, `OpenUrl`, `OpenServer` (fabryka, L2953),
  `CanAuto`, `Activate`, `UpdateCanvas`, `OverlayAction_Click`, `LoadToolbar`, `RequestCloseSession`,
  `CloseSession`, `Connect_Click`, `BeginConnect`, `PromptAndConnect`, `PassBox_KeyDown`,
  `ConnectSession`, `WarnUnencrypted`, VNC `ConnectVnc`/`OnVncConnected`/`OnVncEnded`, `Files_Click`,
  `Disconnect_Click`, `SendCtrlAltDel_Click`/`SendCtrlAltDel`, `ConnectTerm`, `WireTermEvents` (L4323),
  `AskTrustHostKey`/`AskKeyPassphrase`/`AskTrustFtpsCert`, `ConnectFiles`, `WireFilesEvents` (L4393),
  `WireEvents` RDP (L4417), `SetTabStatus`, tear-off `OpenInNewWindow`/`TearOffToWindow`/
  `DockSessionFromWindow`) + split view (L3114-3176) + toolbar (`WinAuth_Changed`, `ActiveHasNoCreds`,
  `UpdatePassVisibility`, `UpdateToolbarMode`, `UpdateToolbarEnabled`, L4485-4530) + status
  (`SetSessionStatus`, `SetStatus`, `DescribeDisconnect`, `KindBrush`, L5979-6016) + `BroadcastToSsh`
  (L3509).
- **Pola własne (rdzeń współdzielony — dlatego ostatni):** `_sessions` (L30), `_active` (L31),
  `_paneLeft`/`_paneRight` (L34), `_splitDropSession` (L35), `_sessionWindows` (L113). Po przeniesieniu
  wszystkie wcześniejsze `_owner._active`/`_owner._sessions` zmieniają się na `_owner.Sessions.Active`
  itd. (spodziewany, mechaniczny churn w PR 3-5 — zaktualizować odwołania).
- **Dedup B3/B4 (jedyny zamierzony nie-mechaniczny fragment):**
  1. Zwinąć powtórzony epilog `if (s==_active){ UpdateToolbarMode(); UpdateCanvas(); }` (~5×,
     L4423/4432/4439/4462/4469) w jeden prywatny `RefreshIfActive(Session s)`.
  2. Wspólne ciało connected/disconnected wydzielić tak, by wołały je **oba** `WireEvents`
     (`SessionManager` i `SessionWindow` L160-189). Ponieważ to różne klasy, szew statyczny:
     np. metody instancyjne na `Session` (`MarkConnected()`, `MarkDisconnected(bool wasLoggedIn)`) +
     statyczny formatter komunikatu w `Core` — ustawiają `Connected`/`LoggedIn`, robią
     `ConnectionLog.Append("CONNECTED"/"DISCONNECTED"/"FAILED")`, czyszczą hasło gdy `!SavePassword`,
     formatują powód przez `RdpUtils.FormatDisconnectReason` i doklejają podpowiedź winauth/creds.
     Powierzchnia UI (tab+status bar vs overlay okna) zostaje osobno w każdym wywołującym.
- **Reuse:** `Session.cs` (polimorficzna klasa sesji), `RdpUtils.FormatDisconnectReason`,
  `ConnectionLog`/`Core.PersistLog`, `SshConnectionFactory`, `FtpConnector`/`RemoteFs`/`IFileConnector`,
  `DualFilePanel`, `RdpDynamicResolution`; `SessionWindow` ctor (L49-50) jako wzorzec wstrzykiwania.

## Mechanika przekrojowa (dotyczy każdego PR)

- **`internal`, nie `public`:** ten sam assembly `RdpManager` → do przełamania hermetyzacji wystarcza
  `internal`. W miarę przenoszenia pól w kolejnych PR powierzchnia `internal` znów się kurczy.
- **Zero edycji XAML** tam, gdzie da się shimem (mniejszy diff = mniejsze ryzyko). ~66 atrybutów
  eventów w `MainWindow.xaml` — każdy PR dotyka tylko swojego podzbioru przez shimy.
- **Kolejność w `Window_Loaded`** = zależności: `_tree` przed `_reach` (SetRowStatus); SessionManager
  ostatni. Nie przenosić samego `Window_Loaded` — pozostaje orkiestratorem startu.
- **Refresh docs:** przy okazji zaktualizować nieaktualne numery linii w `REVIEW.md` (B3/B4) i
  `ROADMAP.md` (rozmiar pliku, status B1) — drobny osobny commit w PR 1 lub PR 6.
- **Branch/PR:** każda ekstrakcja na własnej gałęzi od aktualnego `master`, mergowana **sekwencyjnie**
  (build + ręczna regresja gate'em przed każdym mergem). PR jako draft.
- **Konwencje do naśladowania:** komentarze/XML-doc po polsku; wstrzykiwanie przez ctor + delegaty
  `Action`/`Func` (jak `SessionWindow`); statyczne store'y `Load/Save`.

## Weryfikacja

Środowisko Linux **nie zbuduje** WPF — realną weryfikacją jest CI + ręczna regresja.

1. **Build/testy w CI (bramka twarda).** Po każdym push CI (`.github/workflows/ci.yml`,
   `windows-latest`) uruchamia `dotnet restore/build/test RdpManager.sln -c Release`. Zielone =
   kompiluje się i 233 testy domenowe przechodzą. **CI nie klika UI** — nie wychwyci błędów
   wiringu/domknięć.
2. **Ręczna regresja przed mergem (bramka realna)** — po jednej liście per PR, na Windows:
   - **PR 1 (Update):** „Sprawdź aktualizacje" → stany „dostępna"/„aktualna"; pełna ścieżka
     download→weryfikacja podpisu→relaunch; panel About + changelog; brak podwójnego potwierdzenia
     przy zamykaniu w trakcie update (`_updating`).
   - **PR 2 (Reachability):** kropki statusu i etykiety latencji odświeżają się w tle; WOL budzi
     uśpiony host; „Diagnozuj" zwraca RTT/status.
   - **PR 3 (Tree):** render grup/wierszy (default i minimal), szukanie + puste stany,
     pin/rename/collapse, multiselect (klik/Shift/Ctrl), drag&drop reorder + wskaźnik wstawiania,
     menu kontekstowe, drop plików.
   - **PR 4 (Tabs):** otwieranie/zamykanie/reorder kart, tworzenie/rozbijanie grup, „zamknij inne",
     duplikuj, tytuły/style po zmianie ustawień, przywracanie grup po restarcie.
   - **PR 5 (Fullscreen/Focus):** wejście/wyjście fullscreen (multimon), pin/auto-hide paska, flyout +
     szukanie; toggle focus, peek (poll/delay), min/restore/close; poprawny stan `IsImmersive` przy
     przełączaniu fullscreen↔focus.
   - **PR 6 (SessionManager):** połączenie i rozłączenie **każdego** z 8 protokołów (RDP, SSH, Telnet,
     Serial, VNC, SFTP, FTP, REST) + Http (otwarcie w przeglądarce); split (2× RDP); tear-off do
     `SessionWindow` i dokowanie z powrotem; Ctrl+Alt+Del; **B3/B4:** identyczne komunikaty
     connected/disconnected w oknie głównym i w `SessionWindow` (ta sama ścieżka `RdpUtils`).
3. **Testy jednostkowe:** dodawać w `tests/RdpManager.Tests` **tylko** gdy ekstrakcja odsłoni logikę
   wolną od WPF (wzorzec: `MainViewModel` + `MainViewModelTests`). Większość tego kodu jest UI-bound —
   nie forsować testów na siłę.

## Poza zakresem
- **B2** — przepisanie na MVVM (`SessionsViewModel` + `DataTemplate`). Świadomie pomijane.
- Jakiekolwiek zmiany logiki, nazw, dedup lub „przy okazji" poprawki — **poza jawnym B3/B4 w PR 6**.
- Zaciskanie back-reference do pełnego wstrzykiwania zależności — możliwe jako osobny, późniejszy etap
  po ustabilizowaniu podziału (nie w tych 6 PR).
