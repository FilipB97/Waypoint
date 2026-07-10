using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Forms.Integration;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using AxMSTSCLib;
using MSTSCLib;
using RdpManager.Core;
using RdpManager.Models;
using RdpManager.ViewModels;

namespace RdpManager
{
    public partial class MainWindow
    {
        internal readonly List<Session> _sessions = new List<Session>();
        internal Session _active;
        // Podział ekranu (split-screen): dwie sesje RDP obok siebie. null/null = brak podziału.
        // _active wskazuje panel z fokusem (podświetlenie karty / toolbar).
        internal Session _paneLeft, _paneRight;
        internal Session _splitDropSession;   // sesja przeciągana nad strefę upuszczenia podziału (fallback dla Drop)

        // Drzewo serwerów (render + drag&drop + multiselect + menu) — logika w Controllers/ServerTreeController (PR 3).
        // Wiersze/kropki/opóźnienia/filtr protokołów żyją TAM; Reachability aktualizuje je przez _tree.SetRowStatus.
        // Pasek kart + grupy (elementy karty per sesja, grupy „jak w Vivaldi") — logika w Controllers/TabStripController (PR 4).
        // Kropkę statusu karty aktualizuje _tabs.SetTabStatus; sprzątanie przy zamknięciu sesji — _tabs.OnSessionClosed.
        // internal: czytany też przez serwisy wyniesione z tej klasy (Services/ReachabilityService — PR 2 refaktoru).
        internal readonly MainViewModel _vm = new MainViewModel();

        // internal: czytany też przez serwisy wyniesione z tej klasy (Services/UpdateService — PR 1 refaktoru).
        internal AppSettings _settings = new AppSettings();

        // Osiągalność serwerów w tle (sonda TCP → kropki statusu) — logika w Services/ReachabilityService (PR 2).
        private Services.ReachabilityService _reach;

        // Drzewo serwerów (render/drag&drop/multiselect/menu) — logika w Controllers/ServerTreeController (PR 3).
        // internal: ReachabilityService woła _owner._tree.SetRowStatus po sondzie.
        internal Controllers.ServerTreeController _tree;

        // Pasek kart + grupy (build/wire/rebuild/grupy/drag&drop) — logika w Controllers/TabStripController (PR 4).
        internal Controllers.TabStripController _tabs;

        // Stabilne, odrębne kolory awatarów dla dowolnych (także własnych) grup.
        private readonly Dictionary<string, LinearGradientBrush> _avatarCache = new Dictionary<string, LinearGradientBrush>();
        private static readonly string[][] GroupPalette =
        {
            new[]{"#7C6CFB","#4F3FD1"}, new[]{"#FFB454","#D98F2E"}, new[]{"#36C4CF","#1F8B94"},
            new[]{"#3DDC97","#1F9E6B"}, new[]{"#FB6C9C","#D13F6E"}, new[]{"#6C9CFB","#3F5FD1"},
            new[]{"#C06CFB","#7A3FD1"}, new[]{"#F0C05A","#C79030"}
        };

        // Pełny ekran + tryb skupienia (maszyny stanu) — logika w Controllers/FullscreenController (PR 5).
        // _isFullscreen ZOSTAJE tu (flaga czytana w wielu miejscach: IsImmersive, zasobnik, skróty, status sesji);
        // kontroler czyta/pisze ją przez _owner._isFullscreen.
        internal Controllers.FullscreenController _fs;
        internal bool _isFullscreen;

        // Cykl życia sesji 8 protokołów (fabryka/łączenie/rozłączanie/kanwa/toolbar/split/tear-off/status)
        // — logika w Controllers/SessionManager (PR 6). Współdzielony rdzeń (_sessions/_active/panele) ZOSTAJE tu.
        private Controllers.SessionManager _sm;

        // Współdzielone profile poświadczeń (login/domena + hasło w Credential Manager), wskazywane przez serwery.
        private List<CredentialProfile> _credProfiles = new List<CredentialProfile>();

        // Skrót do lokalizowanego tekstu (dla UI budowanego w kodzie: menu, komunikaty).
        private static string L(string key) => LocalizationManager.S(key);

        /// <summary>Skrót do pędzla z zasobów motywu; null gdy brak (te same semantyki co dotychczasowy rzut).
        /// internal: używany też przez serwisy wyniesione z tej klasy (Services/UpdateService).</summary>
        internal Brush Res(string key) => TryFindResource(key) as Brush;

        // Otwarte, samodzielne okna sesji (model wielookienny).
        internal readonly System.Collections.Generic.List<SessionWindow> _sessionWindows = new System.Collections.Generic.List<SessionWindow>();

        private Services.UpdateService _update;   // aktualizacje + karta „O aplikacji" (PR 1 refaktoru), tworzony w Window_Loaded

        public MainWindow()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _settings = SettingsStore.Load();
            _settings = SettingsStore.ConsumeUpdateSnapshot(_settings);   // po aktualizacji: przywróć stan sprzed update (migawka)
            ConnectionLog.Enabled = _settings.ConnectionLogEnabled;
            _tree = new Controllers.ServerTreeController(this);   // drzewo serwerów (PR 3); przed _reach — SetRowStatus
            _tabs = new Controllers.TabStripController(this);   // pasek kart + grupy (PR 4)
            _fs = new Controllers.FullscreenController(this);   // pełny ekran + tryb skupienia (PR 5)
            _sm = new Controllers.SessionManager(this);   // cykl życia sesji 8 protokołów (PR 6)
            _reach = new Services.ReachabilityService(this);   // sonda osiągalności (limit czasu ustawia w Start()/ApplySettings)
            // Serwis aktualizacji dostaje wersję sprzed tego startu, ZANIM niżej nadpiszemy LastRunVersion.
            _update = new Services.UpdateService(this, _settings.LastRunVersion ?? "");
            var curVer = Services.UpdateService.CurrentVersion().ToString();
            if (_settings.LastRunVersion != curVer) { _settings.LastRunVersion = curVer; SettingsStore.Save(_settings); }
            _credProfiles = CredentialProfileRepository.Load();
            _vm.UseRecentIds(_settings.RecentIds);   // współdziel listę „ostatnich" z ustawieniami
            _tabs.LoadTabGroups();                    // grupy kart z poprzedniej sesji (przypisanie po Id serwera)
            ApplyUiScale(_settings.UiScale);

            _fs.Init();   // pasek pełnoekranowy (opóźnienie + polling) + peek trybu skupienia (PR 5)

            _tree.BuildServerTree();
            BuildEmptyRecent();     // chipy „ostatnie" w pustym stanie kanwy — od startu (UpdateCanvas odświeży później)
            _tree.WireTreeFileDrop();   // import .rdp: upuść pliki z Eksploratora na drzewo serwerów
            _tabs.ApplyTabStripStyle();   // margines paska / rozmiar ikon wg stylu (Domyślny/Minimal) — zanim wejdą karty
            UpdateToolbarEnabled();
            UpdateToolbarMode();

            _reach.Start();   // sonda osiągalności: interwał + limit czasu z ustawień, pierwsza sonda gdy włączone

            ShowView("Sessions");
            Core.KnownHosts.Load(SettingsStore.Dir);   // wykryj/oddziel uszkodzony known_hosts.json ZANIM opróżnimy notki
            ShowHealthNotices();   // nieblokujący sygnał, jeśli przy ładowaniu zadziałała samonaprawa/kwarantanna

            InitTray();
            ApplyHotkey();   // hook WndProc instalujemy już w OnSourceInitialized (patrz niżej) — przed pierwszą klatką
            // Podświetlanie ikon paska kart w trybie skupienia (patrz StartTabStripRepaintPulse): przy ruchu
            // myszy nad paskiem wymuszamy przerysowanie, bo WPF sam go w tym trybie nie maluje.
            TabStripHost.MouseMove += (_, __) => _fs.StartTabStripRepaintPulse();
            // Strefa upuszczenia podziału ekranu (pokazywana przy przeciąganiu karty w obszar sesji).
            SplitDropBorder.DragOver += (s, e) => { e.Effects = DragDropEffects.Move; e.Handled = true; };
            SplitDropBorder.Drop += (s, e) =>
            {
                e.Handled = true;
                var dragged = (e.Data.GetData(typeof(Session)) as Session) ?? _splitDropSession;
                HideSplitDropZone();
                if (dragged != null) EnterSplit(dragged);
            };

            _update.Start();   // sprawdzenie aktualizacji przy starcie + cykliczny timer (co 6 h)
            // Po wyrenderowaniu okna (modal przywracania potrzebuje widocznego właściciela).
            Dispatcher.BeginInvoke(new Action(StartupConnect), DispatcherPriority.Loaded);
        }

        // Nieblokujący sygnał, że przy ładowaniu zadziałała samonaprawa konfiguracji (przywrócenie z kopii
        // .bak / z migawki sprzed aktualizacji / kwarantanna uszkodzonego pliku jako .corrupt). Dotąd trafiało
        // to WYŁĄCZNIE do persist.log — użytkownik nic nie widział. Pokazujemy raz, w InfoBar w stopce panelu:
        // Success = przywrócono dane, Warning = wykryto uszkodzony plik. [[waypoint-persistence-version-mixing]]
        private void ShowHealthNotices()
        {
            var notices = Core.HealthNotices.Drain();
            if (notices.Count == 0) return;

            bool anyCorrupt = notices.Any(n => n.Kind == Core.HealthNoticeKind.FileQuarantined);
            var lines = new List<string>();
            foreach (var n in notices)
            {
                switch (n.Kind)
                {
                    case Core.HealthNoticeKind.SettingsRestored:            lines.Add(L("S.heal.settingsRestored")); break;
                    case Core.HealthNoticeKind.ServersRestored:             lines.Add(L("S.heal.serversRestored")); break;
                    case Core.HealthNoticeKind.FileQuarantined:             lines.Add(string.Format(L("S.heal.quarantined"), n.Detail)); break;
                }
            }

            HealthInfoBar.Severity = anyCorrupt
                ? Wpf.Ui.Controls.InfoBarSeverity.Warning
                : Wpf.Ui.Controls.InfoBarSeverity.Success;
            HealthInfoBar.Title = L(anyCorrupt ? "S.heal.title.warn" : "S.heal.title.ok");
            HealthInfoBar.Message = string.Join("\n", lines);
            HealthInfoBar.IsOpen = true;
        }

        // Start: zdefiniowane serwery „Połącz na starcie" mają priorytet — łączymy z nimi i pomijamy
        // popup przywracania. Gdy ich brak, wraca stare zachowanie (popup ostatniej sesji).
        private void StartupConnect()
        {
            var ids = _settings.AutoConnectServerIds;
            if (ids != null && ids.Count > 0)
            {
                var toOpen = ids
                    .Select(id => _vm.Servers.FirstOrDefault(v => v.Id == id))
                    .Where(s => s != null).Distinct().ToList();
                if (toOpen.Count > 0)
                {
                    foreach (var s in toOpen) OpenServer(s, autoConnect: true);
                    return;
                }
            }
            PromptRestoreLastSession();
        }

        // Na starcie: zaproponuj otwarcie połączeń, które były aktywne przy ostatnim zamknięciu.
        private void PromptRestoreLastSession()
        {
            if (!_settings.RestorePrompt) return;
            var ids = _settings.LastOpenServerIds;
            if (ids == null || ids.Count == 0) return;

            var servers = new List<ServerInfo>();
            foreach (var id in ids)
            {
                var s = _vm.Servers.FirstOrDefault(v => v.Id == id);
                if (s != null) servers.Add(s);
            }
            if (servers.Count == 0) return;

            var dlg = new SessionRestoreWindow(servers) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            if (dlg.DontAskAgain) { _settings.RestorePrompt = false; SettingsStore.Save(_settings); }
            foreach (var s in dlg.SelectedServers) OpenServer(s, autoConnect: true);
        }

        // Zapisz aktualnie otwarte karty (tylko zapisane serwery — nie Quick Connect) do przywrócenia na starcie.
        // Wołane przy każdym otwarciu/zamknięciu karty, więc lista przetrwa też ubicie procesu, nie tylko czyste zamknięcie.
        internal void PersistOpenSessions()
        {
            _settings.LastOpenServerIds = _sessions
                .Select(s => s.Server.Id)
                .Where(id => _vm.Servers.Any(v => v.Id == id))
                .Distinct().ToList();
            SettingsStore.Save(_settings);
        }

        // ---------- Aktualizacje (logika w Services/UpdateService — PR 1 refaktoru) ----------
        // Tu zostają wyłącznie 1-linijkowe shimy dla handlerów podpiętych w MainWindow.xaml
        // (zero edycji XAML — patrz docs/REFACTOR-MAINWINDOW.md).

        private void CheckUpdatesNow_Click(object sender, RoutedEventArgs e) => _update?.CheckUpdatesNow_Click(sender, e);

        private void Update_Click(object sender, RoutedEventArgs e) => _update?.Update_Click(sender, e);

        private void AboutWhatsNew_Click(object sender, RoutedEventArgs e) => _update?.AboutWhatsNew_Click(sender, e);

        // ---------- Nawigacja (rail) ----------

        internal string _currentView = "Dashboard";
        internal bool _sidebarCollapsed;   // ręczne zwinięcie panelu bocznego (klik w aktywną ikonę nawigacji)

        private void Nav_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button b) || !(b.Tag is string v)) return;
            if (v == _currentView) { _sidebarCollapsed = !_sidebarCollapsed; UpdateImmersive(); }   // ta sama ikona → zwiń/rozwiń panel
            else ShowView(v);
        }

        // „Rest" to PEŁNOPRAWNY widok (jak Pulpit/Ostatnie/Ustawienia), a nie osobna flaga obok _currentView —
        // dlatego stan nie może się rozjechać (dawniej _restMode zostawał włączony po przejściu na inny widok,
        // a klik „Połączenia" cicho wracał do listy serwerów). Treść REST to karty-konsole w kontenerze sesji,
        // więc widok „Rest" pokazuje ten sam SessionsView co „Połączenia" — różni je wyłącznie sidebar.
        internal void ShowView(string view)
        {
            bool wasRest = _currentView == "Rest";
            _currentView = view;
            bool rest = view == "Rest";
            _restMode = rest;

            // REST i „Połączenia" dzielą kontener sesji (konsole = karty sesji); reszta widoków ma własne panele.
            SessionsView.Visibility = (view == "Sessions" || rest) ? Visibility.Visible : Visibility.Collapsed;
            DashboardView.Visibility = view == "Dashboard" ? Visibility.Visible : Visibility.Collapsed;
            RecentView.Visibility = view == "Recent" ? Visibility.Visible : Visibility.Collapsed;
            SettingsView.Visibility = view == "Settings" ? Visibility.Visible : Visibility.Collapsed;

            // Sidebar przełączamy TYLKO przy wejściu/wyjściu z REST (drzewo serwerów jest trwałe między widokami).
            if (rest != wasRest)
            {
                SearchBox.Visibility = rest ? Visibility.Collapsed : Visibility.Visible;
                ServerScroll.Visibility = rest ? Visibility.Collapsed : Visibility.Visible;
                RestModule.Visibility = rest ? Visibility.Visible : Visibility.Collapsed;
                if (rest) { ProtoFilterBar.Visibility = Visibility.Collapsed; TreeEmptyHint.Visibility = Visibility.Collapsed; BuildRestModule(); }
                else RenderTree(SearchBox.Text);   // przywróć drzewo serwerów + chipy + hint
            }

            SetNav(NavDashboard, IcoDashboard, view == "Dashboard");
            SetNav(NavSessions, IcoSessions, view == "Sessions");
            SetNav(NavRecent, IcoRecent, view == "Recent");
            SetNav(NavRest, IcoRest, rest);
            SetNav(NavSettings, IcoSettings, view == "Settings");

            if (view == "Dashboard") BuildDashboard();
            else if (view == "Recent") BuildRecent();
            else if (view == "Settings") { LoadSettingsForm(); SettingsSearch.Text = ""; }   // wejście = wyczyść filtr (pokaż wszystkie karty)

            UpdateImmersive();
        }

        private void Window_StateChanged(object sender, System.EventArgs e)
        {
            // Powrót do okna (un-maximize) kasuje ręczny override — następna maksymalizacja wg ustawienia.
            if (WindowState == WindowState.Normal) _fs.OnWindowRestored();
            // Minimalizacja do zasobnika (opcjonalna): chowamy okno; powrót przez ikonę/skrót.
            if (WindowState == WindowState.Minimized && _settings != null
                && _settings.MinimizeToTray && !_isFullscreen)
            {
                Hide();
                return;
            }
            UpdateImmersive();
        }

        // ---------- Zasobnik + globalny skrót ----------

        private System.Windows.Forms.NotifyIcon _tray;

        private void InitTray()
        {
            _tray = new System.Windows.Forms.NotifyIcon { Text = "Waypoint", Visible = true };
            using (var s = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico")).Stream)
                _tray.Icon = new System.Drawing.Icon(s);
            _tray.MouseClick += (o, e) =>
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Left) RestoreFromTray();
            };
            BuildTrayMenu();
        }

        // Osobno, bo etykiety zależą od języka — przebudowywane też po zmianie ustawień.
        private void BuildTrayMenu()
        {
            if (_tray == null) return;
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add(L("S.tray.show"), null, (o, e) => RestoreFromTray());
            menu.Items.Add(L("S.quickConnect"), null, (o, e) => { RestoreFromTray(); QuickConnect_Click(this, null); });
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add(L("S.tray.exit"), null, (o, e) => Close());
            _tray.ContextMenuStrip = menu;
        }

        internal void RestoreFromTray()   // internal: woła też App (druga instancja przez nazwany potok)
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        private const int WM_HOTKEY = 0x0312;
        private const int WM_NCACTIVATE = 0x0086;   // krawędź (non-client) jest przemalowywana przy (de)aktywacji
        private const int HotkeyId = 0x5751;   // 'WQ'
        private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002;

        // Globalny skrót Ctrl+Alt+Q → Szybkie połączenie (opt-in; rejestracja może się nie udać,
        // gdy inny program zajął kombinację — wtedy po prostu nie działa, bez błędu).
        private void ApplyHotkey()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            UnregisterHotKey(hwnd, HotkeyId);
            if (_settings.QuickConnectHotkey)
                RegisterHotKey(hwnd, HotkeyId, MOD_CONTROL | MOD_ALT, (uint)'Q');
        }

        // Hook WndProc + obwódkę zakładamy tu (hwnd już istnieje, okno JESZCZE niepokazane), a nie w Loaded —
        // dzięki temu wybrana obwódka („brak"/kolor) obowiązuje od PIERWSZEJ klatki i nie widać błysku akcentu
        // (kobalt), którym WPF-UI/DWM maluje krawędź, zanim Loaded zdążyłby ją nadpisać.
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndHook);
            WindowBorder.Apply(this);
        }

        private IntPtr WndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                RestoreFromTray();
                QuickConnect_Click(this, null);
                handled = true;
            }
            else if (msg == WM_NCACTIVATE)
            {
                // Windows/WPF-UI przemalowują krawędź okna przy (de)aktywacji — od razu przywróć wybraną obwódkę.
                WindowBorder.Apply(this);
            }
            return IntPtr.Zero;
        }

        // Czy tryb skupienia jest aktywny: zmaksymalizowane okno + aktywna sesja w widoku Połączenia.
        internal bool IsImmersive()
        {
            return _settings != null && !_isFullscreen
                   && (_fs.FocusOverride ?? _settings.ImmersiveOnMaximize)
                   && WindowState == WindowState.Maximized
                   && _active != null
                   && SessionsView.Visibility == Visibility.Visible;
        }

        // Tryb skupienia: po zmaksymalizowaniu chowa titlebar + panel boczny — zostają tylko karty
        // + pulpit. Przywrócenie okna (un-maximize) = pełny UI. W pełnym ekranie nie działa.
        // UWAGA: widocznością SessionToolbar rządzi WYŁĄCZNIE UpdateToolbarMode (stan pusty +
        // skupienie) — tu jej nie dotykamy, żeby nie wskrzeszać paska bez aktywnej sesji.
        internal void UpdateImmersive()
        {
            if (_settings == null || _isFullscreen) return;
            bool immersive = IsImmersive();
            if (!immersive) _fs.HideFocusPeek();   // wyjście ze skupienia: zwiń peek (przenosi Rail/Sidebar z powrotem)
            AppTitleBar.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;
            // Panel boczny ukryty w skupieniu — chyba że chwilowo wysunięty (wtedy żyje w FocusPeekPopup, nie tu).
            if (!_fs.Peeking)
            {
                Rail.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;   // rail (ikony) zostaje — pozwala rozwinąć panel
                Sidebar.Visibility = (immersive || _sidebarCollapsed) ? Visibility.Collapsed : Visibility.Visible;
            }
            FocusControls.Visibility = immersive ? Visibility.Visible : Visibility.Collapsed;
            _fs.SetPeekPolling(immersive);   // wł/wył polling krawędzi peeku wg trybu skupienia
            UpdateToolbarMode();
            if (immersive) _fs.StartTabStripRepaintPulse();   // od razu po wejściu (mysz może już być nad paskiem)
        }

        // Przełącznik trybu skupienia (przycisk na pasku) — logika w FullscreenController (PR 5).
        private void ToggleFocus_Click(object sender, RoutedEventArgs e) => _fs.ToggleFocus();

        // Przyciski okna na pasku kart (widoczne w trybie skupienia, bo titlebar jest wtedy ukryty).
        private void FocusMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void FocusRestore_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Normal;
        private void FocusClose_Click(object sender, RoutedEventArgs e) => Close();

        private void SetNav(Button b, Wpf.Ui.Controls.SymbolIcon ico, bool active)
        {
            b.Background = active ? Res("AccentSoft") : Brushes.Transparent;
            ico.Foreground = active ? Res("Accent") : Res("TextTer");
        }

        // ---------- Moduł REST (rail „REST" przełącza sidebar na kolekcje; Compass §4.4) ----------

        internal bool _restMode;

        // REST to PEŁNOPRAWNY, oddzielny widok: klik wchodzi w moduł (sidebar = kolekcje, treść = karty-konsole);
        // ponowny klik zwija/rozwija panel boczny (konwencja pozostałych ikon raila). Wyjście = dowolna inna
        // ikona raila (zwykłe przełączenie widoku przez ShowView — bez cichego powrotu do listy serwerów).
        private void NavRest_Click(object sender, RoutedEventArgs e)
        {
            if (_currentView == "Rest") { _sidebarCollapsed = !_sidebarCollapsed; UpdateImmersive(); }
            else ShowView("Rest");
        }

        // Rozwinięte węzły modułu (Id wpisu-kolekcji / Id folderu). Sesyjne; domyślnie wszystko ZWINIĘTE —
        // przy wielu kolekcjach z dziesiątkami żądań lista pozostaje kompaktowa (feedback użytkownika).
        private readonly HashSet<string> _restExpanded = new HashSet<string>();
        private string _restSearch = "";   // filtr „Szukaj żądania…" w sidebarze modułu REST

        private void ToggleRestNode(string id)
        {
            if (!_restExpanded.Remove(id)) _restExpanded.Add(id);
            BuildRestModule();
        }

        private void RestSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            _restSearch = RestSearchBox.Text ?? "";
            if (_restMode) BuildRestModule();
        }

        // ---------- Środowisko modułu REST (globalne, chip w sidebarze — jak w Postmanie) ----------

        private void RefreshRestEnvChip()
        {
            if (RestEnvChipText == null) return;
            var active = EnvironmentStore.Load().FirstOrDefault(x => x.Id == EnvironmentStore.GetActiveId());
            RestEnvChipText.Text = L("S.rest.env.label") + ": " + (active?.Name ?? L("S.rest.env.none"));
        }

        // Klik chipa środowiska: menu wyboru aktywnego środowiska (globalne) + „Zarządzaj środowiskami".
        private void RestEnvChip_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var menu = new ContextMenu();
            string activeId = EnvironmentStore.GetActiveId();
            var none = new MenuItem { Header = L("S.rest.env.none"), IsCheckable = true, IsChecked = string.IsNullOrEmpty(activeId) };
            none.Click += (s, a) => SetActiveEnv("");
            menu.Items.Add(none);
            foreach (var env in EnvironmentStore.Load())
            {
                string id = env.Id;
                var mi = new MenuItem { Header = env.Name, IsCheckable = true, IsChecked = id == activeId };
                mi.Click += (s, a) => SetActiveEnv(id);
                menu.Items.Add(mi);
            }
            menu.Items.Add(new Separator());
            var manage = new MenuItem { Header = L("S.rest.env.manage") };
            manage.Click += (s, a) => { new RestEnvWindow { Owner = this }.ShowDialog(); RefreshOpenRestConsoles(); RefreshRestEnvChip(); };
            menu.Items.Add(manage);
            menu.PlacementTarget = RestEnvChip;
            menu.IsOpen = true;
        }

        private void SetActiveEnv(string id)
        {
            EnvironmentStore.SetActiveId(id);
            RefreshOpenRestConsoles();
            RefreshRestEnvChip();
        }

        // Środowisko jest globalne — po zmianie każda otwarta konsola przelicza kolory {{zmiennych}}.
        private void RefreshOpenRestConsoles()
        {
            foreach (var s in _sessions) s.Rest?.RefreshActiveEnv();
        }

        // Każdy wpis REST = kolekcja → foldery → żądania (dane z RestStore.For per serwer).
        // Klik kolekcji/folderu = zwiń/rozwiń; klik żądania = otwórz. To JEDYNE drzewo kolekcji
        // (konsola nie ma już swojego panelu) — więc PPM obsługuje pełną strukturę: na kolekcji
        // wariant REST z BuildServerContextMenu (+ nowe żądanie/folder), na folderze i żądaniu
        // menu z BuildRestFolderMenu/BuildRestReqMenu (nowe/zmień nazwę/usuń).
        internal void BuildRestModule()
        {
            RefreshRestEnvChip();
            RestModuleTree.Children.Clear();
            var rest = _vm.Servers.Where(s => s.Protocol == RemoteProtocol.Rest)
                                  .OrderByDescending(s => s.Pinned)   // przypięte kolekcje na górze
                                  .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
            string q = (_restSearch ?? "").Trim();
            bool searching = q.Length > 0;
            int total = 0;
            foreach (var srv in rest)
            {
                var s = srv;
                var coll = RestStore.For(srv.Id);
                total += coll.Requests.Count;

                if (searching)
                {
                    // Tryb szukania: płaska lista pasujących żądań pod nagłówkiem kolekcji (jak w Postmanie).
                    var hits = coll.Requests
                        .Where(r => (r.Name ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                        .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
                    if (hits.Count == 0) continue;
                    RestModuleTree.Children.Add(RestModuleRow(RestCollHeaderContent(s, true), 0, null));   // nagłówek bez zwijania
                    foreach (var r in hits)
                    {
                        var rr = r;
                        var reqRow = RestModuleRow(RestReqContent(rr), 1, () => OpenRestRequest(s, rr.Id));
                        reqRow.ContextMenu = BuildRestReqMenu(s, rr);
                        RestModuleTree.Children.Add(reqRow);
                    }
                    continue;
                }

                bool open = _restExpanded.Contains(s.Id);
                var row = RestModuleRow(RestCollHeaderContent(s, open), 0, () => ToggleRestNode(s.Id));
                row.ContextMenu = BuildServerContextMenu(s);
                RestModuleTree.Children.Add(row);
                if (open)
                {
                    foreach (var f in coll.Folders.Where(x => string.IsNullOrEmpty(x.ParentId)).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                        AddRestFolder(s, coll, f, 1, new HashSet<string>());
                    foreach (var r in coll.Requests.Where(x => string.IsNullOrEmpty(x.FolderId)).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        var rr = r;
                        var reqRow = RestModuleRow(RestReqContent(rr), 1, () => OpenRestRequest(s, rr.Id));
                        reqRow.ContextMenu = BuildRestReqMenu(s, rr);
                        RestModuleTree.Children.Add(reqRow);
                    }
                }
            }
            RestModuleCount.Text = total.ToString();
            if (rest.Count == 0)
                RestModuleTree.Children.Add(new TextBlock { Text = L("S.rest.module.empty"), Foreground = Res("TextTer"),
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 12, 10, 0) });
            else if (searching && RestModuleTree.Children.Count == 0)
                RestModuleTree.Children.Add(new TextBlock { Text = string.Format(L("S.tree.noresults"), q), Foreground = Res("TextTer"),
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 12, 10, 0) });
        }

        private void AddRestFolder(ServerInfo srv, RestCollection coll, RestFolder f, int depth, HashSet<string> seen)
        {
            if (depth > 8 || !seen.Add(f.Id)) return;   // broń przed cyklem ParentId (A9)
            bool open = _restExpanded.Contains(f.Id);
            var frow = RestModuleRow(RestFolderContent(f.Name, open), depth, () => ToggleRestNode(f.Id));
            frow.ContextMenu = BuildRestFolderMenu(srv, f);
            RestModuleTree.Children.Add(frow);
            if (!open) return;
            foreach (var sub in coll.Folders.Where(x => x.ParentId == f.Id).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                AddRestFolder(srv, coll, sub, depth + 1, seen);
            foreach (var r in coll.Requests.Where(x => x.FolderId == f.Id).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                var rr = r;
                var reqRow = RestModuleRow(RestReqContent(rr), depth + 1, () => OpenRestRequest(srv, rr.Id));
                reqRow.ContextMenu = BuildRestReqMenu(srv, rr);
                RestModuleTree.Children.Add(reqRow);
            }
        }

        // ---------- Menu kontekstowe modułu REST (struktura kolekcji edytowana wyłącznie tutaj) ----------
        // Operacje idą przez otwartą konsolę wpisu (EnsureRestConsole): jej kopia kolekcji to jedyne
        // źródło prawdy, a każdą utrwaloną zmianę zgłasza CollectionChanged → przebudowa modułu.

        private ContextMenu BuildRestFolderMenu(ServerInfo srv, RestFolder f)
        {
            var menu = new ContextMenu();
            var newReq = new MenuItem { Header = L("S.rest.newreq") };
            newReq.Click += (s, e) => AddRestRequestCmd(srv, f.Id);
            var newFolder = new MenuItem { Header = L("S.rest.newfolder") };
            newFolder.Click += (s, e) => AddRestFolderCmd(srv, f.Id);
            var rename = new MenuItem { Header = L("S.rest.rename") };
            rename.Click += (s, e) => RenameRestNodeCmd(f.Name, name => EnsureRestConsole(srv)?.RenameFolder(f.Id, name));
            var del = new MenuItem { Header = L("S.rest.delete") };
            del.Click += (s, e) => DeleteRestNodeCmd(f.Name, () => EnsureRestConsole(srv)?.DeleteFolderById(f.Id));
            menu.Items.Add(newReq);
            menu.Items.Add(newFolder);
            menu.Items.Add(new Separator());
            menu.Items.Add(rename);
            menu.Items.Add(new Separator());
            menu.Items.Add(del);
            return menu;
        }

        private ContextMenu BuildRestReqMenu(ServerInfo srv, RestRequest r)
        {
            var menu = new ContextMenu();
            var open = new MenuItem { Header = L("S.rest.openreq") };
            open.Click += (s, e) => OpenRestRequest(srv, r.Id);
            var rename = new MenuItem { Header = L("S.rest.rename") };
            rename.Click += (s, e) => RenameRestNodeCmd(r.Name, name => EnsureRestConsole(srv)?.RenameRequest(r.Id, name));
            var del = new MenuItem { Header = L("S.rest.delete") };
            del.Click += (s, e) => DeleteRestNodeCmd(r.Name, () => EnsureRestConsole(srv)?.DeleteRequestById(r.Id));
            menu.Items.Add(open);
            menu.Items.Add(new Separator());
            menu.Items.Add(rename);
            menu.Items.Add(new Separator());
            menu.Items.Add(del);
            return menu;
        }

        // Rozwija węzły, w których pojawi się nowy element (rodzice są już rozwinięci — menu było na widocznym wierszu).
        internal void AddRestRequestCmd(ServerInfo srv, string folderId)
        {
            _restExpanded.Add(srv.Id);
            if (!string.IsNullOrEmpty(folderId)) _restExpanded.Add(folderId);
            EnsureRestConsole(srv)?.NewRequest(folderId);
        }

        internal void AddRestFolderCmd(ServerInfo srv, string parentId)
        {
            var dlg = new InputDialog(L("S.rest.newfolder"), L("S.rest.newfolder.label"), "") { Owner = this };
            if (dlg.ShowDialog() != true || dlg.Value.Trim().Length == 0) return;
            _restExpanded.Add(srv.Id);
            if (!string.IsNullOrEmpty(parentId)) _restExpanded.Add(parentId);
            EnsureRestConsole(srv)?.NewFolder(parentId, dlg.Value.Trim());
        }

        private void RenameRestNodeCmd(string current, Action<string> apply)
        {
            var dlg = new InputDialog(L("S.rest.rename"), L("S.rest.rename.label"), current) { Owner = this };
            if (dlg.ShowDialog() != true || dlg.Value.Trim().Length == 0) return;
            apply(dlg.Value.Trim());
        }

        private void DeleteRestNodeCmd(string name, Action apply)
        {
            if (MessageBox.Show(string.Format(L("S.rest.delete.confirm"), name), L("S.rest.delete"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            apply();
        }

        // Wiersz modułu: wcięcie wg głębokości, hover, opcjonalny klik (folder = bez klika).
        private FrameworkElement RestModuleRow(FrameworkElement content, int depth, Action onClick)
        {
            var bd = new Border
            {
                Padding = new Thickness(8 + depth * 14, 5, 8, 5),
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent,
                Cursor = onClick != null ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow,
                Child = content
            };
            if (onClick != null)
            {
                bd.MouseEnter += (s, e) => bd.Background = Res("Panel");
                bd.MouseLeave += (s, e) => bd.Background = Brushes.Transparent;
                bd.MouseLeftButtonUp += (s, e) => onClick();
            }
            return bd;
        }

        private FrameworkElement RestChevron(bool open) => new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = open ? Wpf.Ui.Controls.SymbolRegular.ChevronDown16 : Wpf.Ui.Controls.SymbolRegular.ChevronRight16,
            FontSize = (double)TryFindResource("IconXs"), Foreground = Res("TextTer"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
        };

        private FrameworkElement RestCollHeaderContent(ServerInfo srv, bool open)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(RestChevron(open));
            sp.Children.Add(new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Folder24, FontSize = (double)TryFindResource("IconSm"), Foreground = Res("Accent"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            sp.Children.Add(new TextBlock { Text = srv.Name, Foreground = Res("TextPrim"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            if (srv.Pinned)
                sp.Children.Add(new TextBlock { Text = "★", Foreground = Res("Idle"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
            return sp;
        }

        private FrameworkElement RestFolderContent(string name, bool open)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(RestChevron(open));
            sp.Children.Add(new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Folder24, FontSize = (double)TryFindResource("IconXs"), Foreground = Res("TextTer"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
            sp.Children.Add(new TextBlock { Text = name, Foreground = Res("TextTer"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            return sp;
        }

        private FrameworkElement RestReqContent(RestRequest r)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            string method = (r.Method ?? "GET").ToUpperInvariant();
            sp.Children.Add(new Border
            {
                Background = RestConsole.MethodBadgeBg(method), CornerRadius = new CornerRadius(5),
                Padding = new Thickness(5, 1, 5, 1), MinWidth = 38, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
                Child = new TextBlock { Text = method, Foreground = RestConsole.MethodBrush(method), FontFamily = (FontFamily)TryFindResource("Mono"), FontWeight = FontWeights.Bold, FontSize = 9, TextAlignment = TextAlignment.Center }
            });
            sp.Children.Add(new TextBlock { Text = r.Name, Foreground = Res("TextSec"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            return sp;
        }

        // Otwiera (lub uaktywnia) konsolę REST danego wpisu i zaznacza żądanie po Id.
        private void OpenRestRequest(ServerInfo srv, string reqId)
            => EnsureRestConsole(srv)?.SelectRequestById(reqId);

        // Otwiera (lub uaktywnia) sesję-konsolę wpisu REST i ją zwraca — wspólna brama dla nawigacji
        // i operacji na strukturze z modułu (mutuje zawsze konsola, nigdy moduł bezpośrednio).
        private RestConsole EnsureRestConsole(ServerInfo srv)
        {
            LaunchServer(srv, autoConnect: true);
            return _sessions.Find(x => x.Server == srv)?.Rest;
        }

        // „+" w nagłówku modułu: nowa kolekcja REST (wpisy REST nie żyją na liście serwerów,
        // więc moduł potrzebuje własnego wejścia do tworzenia).
        private void AddRestCollection_Click(object sender, RoutedEventArgs e)
        {
            var server = new ServerInfo { Group = "HTTP", Status = ServerStatus.Offline, Protocol = RemoteProtocol.Rest };
            var dlg = new ServerEditWindow(server, "", _credProfiles) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            _vm.Add(server);
            PersistServers();
            SaveCredential(server, dlg.EnteredPassword);
            BuildRestModule();
        }

        private PasswordGeneratorWindow _genWindow;

        // Generator haseł/tokenów/GUID — niemodalny, jedno okno (drugie kliknięcie aktywuje istniejące).
        private void OpenPasswordGen_Click(object sender, RoutedEventArgs e)
        {
            if (_genWindow == null)
            {
                _genWindow = new PasswordGeneratorWindow { Owner = this };
                _genWindow.Closed += (s, a) => _genWindow = null;
                _genWindow.Show();
            }
            else
            {
                if (_genWindow.WindowState == WindowState.Minimized) _genWindow.WindowState = WindowState.Normal;
                _genWindow.Activate();
            }
        }

        // Karta aktualizacji + historia zmian w „O aplikacji" — logika w Services/UpdateService (PR 1 refaktoru);
        // tu zostają tylko linki repo/licencja/zgłoszenia i wspólny OpenUrl.
        private static readonly string RepoUrl = "https://github.com/FilipB97/Waypoint";
        private void AboutRepo_Click(object sender, RoutedEventArgs e) => OpenUrl(RepoUrl);
        private void AboutLicense_Click(object sender, RoutedEventArgs e) => OpenUrl(RepoUrl + "/blob/master/LICENSE");
        private void AboutReportIssue_Click(object sender, RoutedEventArgs e) => OpenUrl(RepoUrl + "/issues/new");

        // internal: używany też przez Services/UpdateService („Co nowego" bez sieci → strona wydań).
        internal static void OpenUrl(string url)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* brak przeglądarki — ignoruj */ }
        }

        // ---------- Zoom interfejsu (Ctrl + kółko / Ctrl +/- / Ctrl 0) ----------

        // Skala UI (zoom): powiększa/pomniejsza CHROME Waypointa. Widok sesji jest kontr-skalowany
        // (SessionScale = 1/scale), żeby RDP/terminal/VNC renderowały się 1:1 niezależnie od zoomu — inaczej
        // przy <100% rozdzielczość liczona z ActualWidth hosta wychodziła za duża i obraz nie mieścił się w oknie.
        private void ApplyUiScale(double scale)
        {
            scale = Math.Clamp(scale, 0.7, 1.8);
            RootScale.ScaleX = RootScale.ScaleY = scale;
            SessionScale.ScaleX = SessionScale.ScaleY = 1.0 / scale;
        }

        private void ZoomTo(double scale)
        {
            scale = Math.Round(Math.Clamp(scale, 0.7, 1.8), 2);
            _settings.UiScale = scale;
            ApplyUiScale(scale);
            if (SettingsView.Visibility == Visibility.Visible)
                SetUiScale.Text = ((int)Math.Round(scale * 100)).ToString();
            QueueSettingsSave();   // kółko myszy potrafi sypnąć dziesiątkami zdarzeń — nie pisz pliku co tick
        }

        private DispatcherTimer _settingsSaveTimer;

        private void QueueSettingsSave()
        {
            if (_settingsSaveTimer == null)
            {
                _settingsSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                _settingsSaveTimer.Tick += (s, e) => { _settingsSaveTimer.Stop(); SettingsStore.Save(_settings); };
            }
            _settingsSaveTimer.Stop();
            _settingsSaveTimer.Start();
        }

        // Próbnik akcentu (Compass §4.7): „Domyślny" (per motyw) + kilka gotowych kolorów. Klik = zastosuj
        // od razu (podgląd na żywo) i zapisz (debounce). Wybór nadpisuje rodzinę Accent* w ThemeManager.
        private static readonly (string key, string hex)[] AccentOptions = new[]
        {
            ("S.accent.default", ""),
            ("S.accent.violet",  "#7C6CFB"),
            ("S.accent.green",   "#22B07D"),
            ("S.accent.amber",   "#E0872E"),
            ("S.accent.rose",    "#E8556B"),
        };

        private void BuildAccentSwatches()
        {
            AccentSwatches.Children.Clear();
            string cur = _settings.AccentColor ?? "";
            foreach (var (key, hex) in AccentOptions)
            {
                bool selected = string.Equals(cur, hex, StringComparison.OrdinalIgnoreCase);
                // „Domyślny" pokazuje akcent, jaki faktycznie obowiązuje bez własnego koloru — czyli akcent
                // aktywnego presetu (§4.9), a nie tylko domyślny motywu.
                var color = hex.Length == 0
                    ? DefaultAccentColor()
                    : (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
                var dot = new Border
                {
                    Width = 20, Height = 20, CornerRadius = new CornerRadius(6),
                    Background = new SolidColorBrush(color),
                    Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand,
                    BorderBrush = selected ? Res("Accent") : Res("Border"),
                    BorderThickness = new Thickness(selected ? 2 : 1),
                    ToolTip = L(key)
                };
                string h = hex;
                dot.MouseLeftButtonUp += (s, e) =>
                {
                    _settings.AccentColor = h;
                    ThemeManager.Apply(_settings.Theme, h, _settings.ThemeVariantDark, _settings.ThemeVariantLight);   // podgląd na żywo (akcent na wierzchu presetu)
                    RefreshThemedViews();
                    QueueSettingsSave();
                    BuildAccentSwatches();                    // odśwież zaznaczenie (obwódka)
                };
                AccentSwatches.Children.Add(dot);
            }
        }

        // Akcent obowiązujący przy „Domyślnym" wyborze: z aktywnego presetu, a jak brak — domyślny Compass per motyw.
        private System.Windows.Media.Color DefaultAccentColor()
        {
            bool light = ThemeManager.IsLight;
            var p = ThemePresets.Find(light ? _settings.ThemeVariantLight : _settings.ThemeVariantDark, light);
            return p?.Accent ?? (light ? System.Windows.Media.Color.FromRgb(0x26, 0x57, 0xD6)
                                       : System.Windows.Media.Color.FromRgb(0x4C, 0x86, 0xFF));
        }

        // Siatka presetów motywu (Compass §4.9) — karty z podglądem palety, dla bieżącego trybu (ciemny/jasny).
        // Klik = ustaw preset dla tego trybu, zastosuj na żywo, zapisz (debounce).
        private void BuildThemePresets()
        {
            ThemePresetGrid.Children.Clear();
            bool light = ThemeManager.IsLight;
            string cur = light ? _settings.ThemeVariantLight : _settings.ThemeVariantDark;
            foreach (var p in ThemePresets.For(light))
            {
                bool selected = string.Equals(cur, p.Id, StringComparison.OrdinalIgnoreCase);

                // Podgląd palety = trzy pasy (tło / powierzchnia / akcent) w proporcji 52/30/18 (jak w mockupie).
                var bars = new Grid { Height = 30 };
                bars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52, GridUnitType.Star) });
                bars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30, GridUnitType.Star) });
                bars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18, GridUnitType.Star) });
                void Bar(Color c, int col) { var b = new Border { Background = new SolidColorBrush(c) }; Grid.SetColumn(b, col); bars.Children.Add(b); }
                Bar(p.Canvas, 0); Bar(p.Panel, 1); Bar(p.Accent, 2);
                var preview = new Border { CornerRadius = new CornerRadius(7), ClipToBounds = true, Child = bars };

                var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 7, 0, 0) };
                nameRow.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = new SolidColorBrush(p.Accent), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
                nameRow.Children.Add(new TextBlock
                {
                    Text = p.Name, Foreground = Res("TextPrim"), FontSize = (double)TryFindResource("FontSmall"),
                    FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
                });

                var sp = new StackPanel();
                sp.Children.Add(preview);
                sp.Children.Add(nameRow);
                var card = new Border
                {
                    Width = 172, CornerRadius = new CornerRadius(11), Padding = new Thickness(9),
                    Margin = new Thickness(0, 0, 10, 10), Cursor = Cursors.Hand, Background = Res("Panel"),
                    BorderBrush = selected ? Res("Accent") : Res("Border"),
                    BorderThickness = new Thickness(selected ? 2 : 1),
                    Child = sp, ToolTip = p.Name
                };
                string id = p.Id;
                card.MouseLeftButtonUp += (s, e) =>
                {
                    if (light) _settings.ThemeVariantLight = id; else _settings.ThemeVariantDark = id;
                    ThemeManager.Apply(_settings.Theme, _settings.AccentColor, _settings.ThemeVariantDark, _settings.ThemeVariantLight);
                    RefreshThemedViews();
                    QueueSettingsSave();
                    BuildThemePresets();
                    BuildAccentSwatches();   // „Domyślny" akcent zależy od presetu
                };
                ThemePresetGrid.Children.Add(card);
            }
        }

        private void Window_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || _isFullscreen) return;
            ZoomTo(_settings.UiScale + (e.Delta > 0 ? 0.1 : -0.1));
            e.Handled = true;
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!(_update?.IsUpdating ?? false) && _settings.ConfirmCloseConnected &&
                (_sessions.Any(s => s.Connected) || _sessionWindows.Any(w => w.IsConnected)) &&
                MessageBox.Show(L("S.msg.closeapp"), L("S.msg.closeapp.title"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            // Zapamiętaj otwarte karty + dograj odroczony zapis ustawień (debounce zoomu), zanim aplikacja zniknie.
            _settingsSaveTimer?.Stop();
            PersistOpenSessions();

            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            try { UnregisterHotKey(new WindowInteropHelper(this).Handle, HotkeyId); } catch { }

            foreach (var s in _sessions)
            {
                if (s.IsTerm) { try { s.Term.DisposeTerminal(); } catch { } continue; }
                if (s.IsVnc) { var vc = s.Vnc.Client; s.Vnc.Client = null; try { vc?.Close(); } catch { } try { s.Host.Dispose(); } catch { } continue; }
                try { s.Resizer?.Dispose(); } catch { }
                try { s.Rdp.Disconnect(); } catch { }
                try { s.Host.Dispose(); } catch { }
            }
            foreach (var w in _sessionWindows.ToList())
            {
                try { w.Close(); } catch { }
            }
        }

        // ---------- Ustawienia ----------

        // Blokada zdarzeń SelectionChanged podczas wypełniania formularza — inaczej ustawianie SelectedIndex
        // w LoadSettingsForm od razu „stosowałoby" ustawienia i pisało plik.
        private bool _loadingSettings;

        private void LoadSettingsForm()
        {
            _loadingSettings = true;
            SettingsCats.SelectedIndex = 0;   // domyślna kategoria (Interfejs) → pokazuje pierwszą kartę
            SetUiScale.Text = ((int)Math.Round(_settings.UiScale * 100)).ToString();
            SetBarDelay.Text = _settings.FullscreenBarDelayMs.ToString();
            SetFocusPeekOpacity.Text = _settings.FocusPeekOpacity.ToString();
            SetTermFontSize.Text = _settings.TerminalFontSize.ToString();
            SegSet(ThemeSeg, _settings.Theme);
            SegSet(BorderSeg, _settings.WindowBorderColor ?? "");
            SetLanguage.SelectedIndex = _settings.Language == "en" ? 1 : 0;
            SegSet(ListStyleSeg, _settings.ListStyle);
            SetShowLatency.IsChecked = _settings.ShowLatency;
            BuildThemePresets();
            BuildAccentSwatches();
            SetDefaultPort.Text = _settings.DefaultPort.ToString();
            SelectColorDepthSeg(_settings.ColorDepth);
            SetRedirClip.IsChecked = _settings.DefaultRedirectClipboard;
            SetRedirDrives.IsChecked = _settings.DefaultRedirectDrives;
            SetProbeTimeout.Text = _settings.ProbeTimeoutSeconds.ToString();
            SetAutoReconnect.IsChecked = _settings.AutoReconnect;
            SetReachEnabled.IsChecked = _settings.ReachabilityEnabled;
            SetReachInterval.Text = _settings.ReachabilityIntervalSec.ToString();
            SetConfirmClose.IsChecked = _settings.ConfirmCloseConnected;
            SetConnLog.IsChecked = _settings.ConnectionLogEnabled;
            SetOpenNewWindow.IsChecked = _settings.OpenInNewWindowByDefault;
            SetImmersive.IsChecked = _settings.ImmersiveOnMaximize;
            SetCheckUpdates.IsChecked = _settings.CheckUpdates;
            SetMinimizeToTray.IsChecked = _settings.MinimizeToTray;
            SetHotkey.IsChecked = _settings.QuickConnectHotkey;
            SetRestorePrompt.IsChecked = _settings.RestorePrompt;
            BuildAutoConnectList();
            BuildProfilesList();
            SetDataPath.Text = SettingsStore.Dir;
            var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            string verStr = ver.Major + "." + ver.Minor + "." + Math.Max(ver.Build, 0);
            AboutVersion.Text = string.Format(L("S.about.installedver"), verStr);
            AboutDataPath.Text = L("S.msg.about.datafolder") + " " + SettingsStore.Dir;
            _update?.RefreshAboutCard();   // karta aktualizacji + historia zmian (fallback od razu, GitHub w tle)
            SettingsStatus.Text = "";
            _loadingSettings = false;
        }

        // Lista serwerów do „Połącz na starcie": checkbox = auto-połączenie, przeciąganie (uchwyt ⠿) ustala
        // KOLEJNOŚĆ uruchamiania. Serwery pogrupowane wg protokołu (nagłówki); w grupie: zapisana kolejność → nazwa.
        private void BuildAutoConnectList()
        {
            AutoConnectList.Children.Clear();
            var servers = _vm.Servers.ToList();
            if (servers.Count == 0)
            {
                AutoConnectList.Children.Add(new TextBlock
                {
                    Text = L("S.set.autoconnect.empty"),
                    Foreground = Res("TextTer"), FontSize = 12
                });
                return;
            }

            var ids = _settings.AutoConnectServerIds ?? new List<string>();
            var selected = new HashSet<string>(ids);
            int SavedOrder(ServerInfo s) { int i = ids.IndexOf(s.Id); return i < 0 ? int.MaxValue : i; }

            foreach (var grp in servers.GroupBy(s => s.Protocol).OrderBy(g => ProtocolOrder(g.Key)))
            {
                AutoConnectList.Children.Add(MakeAcHeader(ProtocolLabel(grp.Key), grp.Count()));
                foreach (var s in grp.OrderBy(SavedOrder).ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
                    AutoConnectList.Children.Add(BuildAutoConnectRow(s, selected.Contains(s.Id)));
            }
        }

        // Nagłówek grupy protokołu na liście autostartu (nieprzeciągalny — zapis pomija: OfType<Border>()).
        private FrameworkElement MakeAcHeader(string label, int count) => new TextBlock
        {
            Text = label + "  ·  " + count,
            Foreground = Res("TextTer"), FontSize = 11, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 8, 0, 3)
        };

        private static int ProtocolOrder(RemoteProtocol p) => p switch
        {
            RemoteProtocol.Rdp => 0,
            RemoteProtocol.Ssh => 1,
            RemoteProtocol.Sftp => 2,
            RemoteProtocol.Ftp => 3,
            RemoteProtocol.Vnc => 4,
            RemoteProtocol.Telnet => 5,
            RemoteProtocol.Serial => 6,
            RemoteProtocol.Http => 7,
            RemoteProtocol.Rest => 8,
            _ => 9
        };

        // ---------- Profile poświadczeń (lista w Ustawieniach) ----------
        // Profile poświadczeń renderowane w DWÓCH miejscach: dedykowana kategoria (ProfilesList) oraz
        // inline w „Połączenie" wg mockupu (ConnProfilesList). BuildProfileRow tworzy świeże elementy, więc
        // wołamy per panel.
        private void BuildProfilesList()
        {
            FillProfiles(ProfilesList);
            if (ConnProfilesList != null) FillProfiles(ConnProfilesList);
        }

        private void FillProfiles(Panel panel)
        {
            panel.Children.Clear();
            if (_credProfiles.Count == 0)
            {
                panel.Children.Add(new TextBlock { Text = L("S.prof.empty"), Foreground = Res("TextTer"), FontSize = 12 });
                return;
            }
            foreach (var pr in _credProfiles)
                panel.Children.Add(BuildProfileRow(pr));
        }

        private FrameworkElement BuildProfileRow(CredentialProfile profile)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            string login = string.IsNullOrWhiteSpace(profile.Domain) ? profile.Username : profile.Domain + "\\" + profile.Username;
            var info = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = profile.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            info.Children.Add(new TextBlock { Text = "   " + login, Foreground = Res("TextTer"), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(info);

            var btns = new StackPanel { Orientation = Orientation.Horizontal };
            Grid.SetColumn(btns, 1);
            var edit = new Wpf.Ui.Controls.Button { Content = L("S.prof.edit"), Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary };
            edit.Click += (s, e) => EditProfile(profile);
            var del = new Wpf.Ui.Controls.Button { Content = L("S.prof.delete"), Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary, Margin = new Thickness(8, 0, 0, 0) };
            del.Click += (s, e) => DeleteProfile(profile);
            btns.Children.Add(edit);
            btns.Children.Add(del);
            row.Children.Add(btns);
            return row;
        }

        private void AddProfile_Click(object sender, RoutedEventArgs e)
        {
            var profile = new CredentialProfile();
            var dlg = new CredentialProfileWindow(profile, "") { Owner = this };
            if (dlg.ShowDialog() != true) return;
            _credProfiles.Add(profile);
            CredentialProfileRepository.Save(_credProfiles);
            SaveProfilePassword(profile, dlg.EnteredPassword);
            BuildProfilesList();
        }

        private void EditProfile(CredentialProfile profile)
        {
            CredentialStore.TryRead(profile.CredTarget, out var cur);
            var dlg = new CredentialProfileWindow(profile, cur ?? "") { Owner = this };
            if (dlg.ShowDialog() != true) return;
            CredentialProfileRepository.Save(_credProfiles);
            SaveProfilePassword(profile, dlg.EnteredPassword);
            BuildProfilesList();
        }

        private void DeleteProfile(CredentialProfile profile)
        {
            if (MessageBox.Show(string.Format(L("S.prof.deleteconfirm"), profile.Name), L("S.prof.edit.title"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            _credProfiles.Remove(profile);
            CredentialProfileRepository.Save(_credProfiles);
            CredentialStore.Delete(profile.CredTarget);
            // Serwery używające tego profilu wracają do własnego (pustego) loginu — wyczyść odnośnik.
            bool changed = false;
            foreach (var s in _vm.Servers)
                if (s.CredentialProfileId == profile.Id) { s.CredentialProfileId = ""; changed = true; }
            if (changed) PersistServers();
            BuildProfilesList();
        }

        // Ostrzeżenie o nieudanym zapisie hasła do sejfu (CredWrite odmówił). Dotąd wyjątek połykał globalny
        // handler i użytkownik nie wiedział, że hasło nie zostało zapisane.
        private static void WarnCredSaveFailed()
            => MessageBox.Show(L("S.cred.saveFailed"), L("S.cred.saveFailed.title"), MessageBoxButton.OK, MessageBoxImage.Warning);

        private static void SaveProfilePassword(CredentialProfile profile, string password)
        {
            if (string.IsNullOrEmpty(password)) CredentialStore.Delete(profile.CredTarget);
            else if (!CredentialStore.TrySave(profile.CredTarget, profile.Username, password)) WarnCredSaveFailed();
        }

        private Point _acDragStart;
        private Border _acDragRow;
        private Border _acDropRow;

        private FrameworkElement BuildAutoConnectRow(ServerInfo server, bool isChecked)
        {
            var row = new Border
            {
                Tag = server.Id, CornerRadius = new CornerRadius(6), Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(0),
                Padding = new Thickness(2, 1, 2, 1), Margin = new Thickness(0, 1, 0, 1), AllowDrop = true
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock
            {
                Text = "⠿", Foreground = Res("TextTer"), FontSize = 13, Cursor = Cursors.SizeAll,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0)
            });
            sp.Children.Add(new CheckBox
            {
                Content = (string.IsNullOrWhiteSpace(server.Name) ? server.Host : server.Name) + "  —  " + DisplayHost(server),
                Tag = server.Id, IsChecked = isChecked,
                Foreground = Res("TextPrim"), VerticalAlignment = VerticalAlignment.Center
            });
            row.Child = sp;

            // Przeciąganie startuje po ruchu (klik w checkbox bez ruchu nadal przełącza zaznaczenie).
            row.PreviewMouseLeftButtonDown += (s, e) => { _acDragStart = e.GetPosition(null); _acDragRow = row; };
            row.PreviewMouseMove += (s, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || _acDragRow != row) return;
                var pos = e.GetPosition(null);
                if (Math.Abs(pos.X - _acDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(pos.Y - _acDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                row.Opacity = 0.5;
                try { DragDrop.DoDragDrop(row, row, DragDropEffects.Move); }
                catch { }
                finally { row.Opacity = 1.0; _acDragRow = null; ClearAcDropIndicator(); }
            };
            row.DragOver += (s, e) =>
            {
                if (!(e.Data.GetData(typeof(Border)) is Border dragged) || dragged == row) { e.Effects = DragDropEffects.None; return; }
                e.Effects = DragDropEffects.Move; e.Handled = true;
                ShowAcDropIndicator(row, e.GetPosition(row).Y > row.ActualHeight / 2);
            };
            row.DragLeave += (s, e) => ClearAcDropIndicator();
            row.Drop += (s, e) =>
            {
                ClearAcDropIndicator();
                if (!(e.Data.GetData(typeof(Border)) is Border dragged) || dragged == row) return;
                bool bottom = e.GetPosition(row).Y > row.ActualHeight / 2;
                AutoConnectList.Children.Remove(dragged);
                int target = AutoConnectList.Children.IndexOf(row);
                AutoConnectList.Children.Insert(bottom ? target + 1 : target, dragged);
                e.Handled = true;
            };
            return row;
        }

        private void ShowAcDropIndicator(Border row, bool bottom)
        {
            if (_acDropRow != null && _acDropRow != row) _acDropRow.BorderThickness = new Thickness(0);
            _acDropRow = row;
            row.BorderBrush = Res("Accent");
            row.BorderThickness = bottom ? new Thickness(0, 0, 0, 2) : new Thickness(0, 2, 0, 0);
        }

        private void ClearAcDropIndicator()
        {
            if (_acDropRow != null) { _acDropRow.BorderThickness = new Thickness(0); _acDropRow = null; }
        }

        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(SetUiScale.Text.Trim(), out var us)) _settings.UiScale = Math.Clamp(us / 100.0, 0.7, 1.8);
            _settings.FullscreenBarDelayMs = int.TryParse(SetBarDelay.Text.Trim(), out var d) ? Math.Clamp(d, 0, 3000) : 450;
            _settings.FocusPeekOpacity = int.TryParse(SetFocusPeekOpacity.Text.Trim(), out var fpo) ? Math.Clamp(fpo, 0, 100) : 92;
            _settings.TerminalFontSize = int.TryParse(SetTermFontSize.Text.Trim(), out var tfs) ? Math.Clamp(tfs, 8, 24) : 14;
            _settings.DefaultPort = int.TryParse(SetDefaultPort.Text.Trim(), out var p) ? Math.Clamp(p, 1, 65535) : 3389;
            _settings.ColorDepth = ParseColorDepth();
            _settings.DefaultRedirectClipboard = SetRedirClip.IsChecked == true;
            _settings.DefaultRedirectDrives = SetRedirDrives.IsChecked == true;
            _settings.ProbeTimeoutSeconds = int.TryParse(SetProbeTimeout.Text.Trim(), out var pt) ? Math.Clamp(pt, 1, 60) : 2;
            _settings.AutoReconnect = SetAutoReconnect.IsChecked == true;
            _settings.ReachabilityEnabled = SetReachEnabled.IsChecked == true;
            _settings.ReachabilityIntervalSec = int.TryParse(SetReachInterval.Text.Trim(), out var r) ? Math.Clamp(r, 5, 3600) : 30;
            _settings.ConfirmCloseConnected = SetConfirmClose.IsChecked == true;
            _settings.ConnectionLogEnabled = SetConnLog.IsChecked == true;
            _settings.OpenInNewWindowByDefault = SetOpenNewWindow.IsChecked == true;
            _settings.ImmersiveOnMaximize = SetImmersive.IsChecked == true;
            _settings.CheckUpdates = SetCheckUpdates.IsChecked == true;
            _settings.MinimizeToTray = SetMinimizeToTray.IsChecked == true;
            _settings.QuickConnectHotkey = SetHotkey.IsChecked == true;
            _settings.RestorePrompt = SetRestorePrompt.IsChecked == true;
            // Kolejność wierszy (po przeciąganiu) = kolejność uruchamiania na starcie.
            _settings.AutoConnectServerIds = AutoConnectList.Children.OfType<Border>()
                .Select(b => (b.Child as StackPanel)?.Children.OfType<CheckBox>().FirstOrDefault())
                .Where(cb => cb != null && cb.IsChecked == true)
                .Select(cb => cb.Tag as string)
                .Where(id => !string.IsNullOrEmpty(id)).ToList();
            _settings.Theme = SegTag(ThemeSeg) ?? "Dark";
            _settings.Language = (SetLanguage.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "pl";
            _settings.ListStyle = SegTag(ListStyleSeg) ?? "Default";
            _settings.WindowBorderColor = SegTag(BorderSeg) ?? "";
            _settings.ShowLatency = SetShowLatency.IsChecked == true;

            SettingsStore.Save(_settings);
            ApplySettings();
            SettingsStatus.Text = L("S.st.saved");
        }

        private int ParseColorDepth() => RdpUtils.ParseColorDepth(SegTag(ColorDepthSeg));

        // Zaznacza segment głębi kolorów (16/24/32) wg wartości z ustawień.
        private void SelectColorDepthSeg(int depth)
        {
            string tag = depth == 16 ? "16" : depth == 24 ? "24" : "32";
            foreach (var rb in ColorDepthSeg.Children.OfType<RadioButton>())
                rb.IsChecked = (rb.Tag as string) == tag;
        }

        private void ApplySettings()
        {
            ConnectionLog.Enabled = _settings.ConnectionLogEnabled;
            ApplyUiScale(_settings.UiScale);
            // Clampy także tutaj — ustawienia mogą przyjść z importu profilu (plik zewnętrzny).
            _fs.ApplyBarDelay();   // opóźnienie paska pełnoekranowego + peeku wg ustawień (PR 5)
            _reach?.ApplySettings();   // limit czasu sondy + interwał + wł/wył cyklu wg ustawień

            // Cykliczne sprawdzanie aktualizacji wg tego samego przełącznika co start.
            _update?.ApplyCheckUpdatesSetting();

            ThemeManager.Apply(_settings.Theme, _settings.AccentColor, _settings.ThemeVariantDark, _settings.ThemeVariantLight);
            LocalizationManager.Apply(_settings.Language);
            BuildTrayMenu();   // etykiety menu zasobnika w nowym języku
            ApplyHotkey();
            UpdateImmersive();
            // Styl widoku (Domyślny/Minimalny) i motyw zmieniają wygląd wierszy/kart — przerysuj oba na żywo.
            RenderTree(SearchBox.Text);
            RebuildTabStrip();
            RefreshThemedViews();
        }

        // Pulpit i „Ostatnie" budują karty/wiersze z MIGAWKAMI kolorów (Res(...) w chwili budowy), więc nie
        // reagują na zmianę motywu/presetu „na żywo" — trzeba przebudować aktywny widok. Wołane po każdym
        // ThemeManager.Apply (ApplySettings, klik presetu, akcent, przełącz motyw).
        private void RefreshThemedViews()
        {
            if (_currentView == "Dashboard") BuildDashboard();
            else if (_currentView == "Recent") BuildRecent();
        }

        // Filtr Ustawień: chowa karty, których zagregowany (zlokalizowany) tekst nie zawiera zapytania.
        // Tekst czytamy z drzewa LOGICZNEGO (działa bez rozwijania list i bez renderu; łapie etykiety,
        // treści checkboxów i pozycje list rozwijanych). Kilka kart — koszt pomijalny, bez cache.
        private void SettingsSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            string q = (SettingsSearch.Text ?? "").Trim().ToLowerInvariant();
            if (q.Length == 0) { ShowSettingsCategory(SettingsCats.SelectedIndex); return; }   // pusto → tryb kategorii
            foreach (var child in SettingsCards.Children)   // wyszukiwanie: tryb wyników — pasujące karty ze wszystkich kategorii
            {
                if (!(child is Border card)) continue;
                var sb = new System.Text.StringBuilder();
                CollectSettingsText(card, sb);
                card.Visibility = sb.ToString().ToLowerInvariant().Contains(q) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // Lewa nawigacja Ustawień: klik kategorii wychodzi z ewentualnego wyszukiwania i pokazuje jej kartę.
        private void SettingsCat_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (SettingsSearch == null || SettingsCards == null) return;   // może zostać wywołane w trakcie inicjalizacji
            if (!string.IsNullOrEmpty(SettingsSearch.Text)) { SettingsSearch.Text = ""; return; }   // TextChanged pokaże kategorię
            ShowSettingsCategory(SettingsCats.SelectedIndex);
        }

        // Pokazuje jedną kartę: kolejność kart w SettingsCards = kolejność pozycji w SettingsCats.
        private void ShowSettingsCategory(int index)
        {
            if (SettingsCards == null) return;
            if (index < 0) index = 0;
            int i = 0;
            foreach (var child in SettingsCards.Children)
            {
                if (!(child is Border card)) continue;
                card.Visibility = (i == index) ? Visibility.Visible : Visibility.Collapsed;
                i++;
            }
        }

        private static void CollectSettingsText(object node, System.Text.StringBuilder sb)
        {
            if (node is string s) sb.Append(s).Append(' ');
            else if (node is TextBlock tb) sb.Append(tb.Text).Append(' ');
            if (node is DependencyObject d)
                foreach (var kid in LogicalTreeHelper.GetChildren(d))
                    CollectSettingsText(kid, sb);
        }

        // Ustawienia interfejsu (motyw / język / styl listy / obwódka) działają OD RAZU po zmianie — bez
        // scrollowania do „Zapisz". Zapis pliku jest odroczony (debounce). Reszta zatwierdza przycisk Zapisz.
        // Combo (Język) → SelectionChanged; segmenty (Motyw/Styl/Obwódka) → Checked (SegSetting_Changed).
        private void InterfaceSetting_Changed(object sender, SelectionChangedEventArgs e) => ApplyInterfaceLive();
        private void SegSetting_Changed(object sender, RoutedEventArgs e) => ApplyInterfaceLive();

        private void ApplyInterfaceLive()
        {
            if (_loadingSettings || _settings == null) return;
            _settings.Theme = SegTag(ThemeSeg) ?? "Dark";
            _settings.Language = (SetLanguage.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "pl";
            _settings.ListStyle = SegTag(ListStyleSeg) ?? "Default";
            _settings.WindowBorderColor = SegTag(BorderSeg) ?? "";
            WindowBorder.SetSpec(_settings.WindowBorderColor);
            ApplySettings();
            // Zmiana motywu może przełączyć tryb (ciemny/jasny) → odśwież siatkę presetów i próbnik akcentu.
            BuildThemePresets();
            BuildAccentSwatches();
            QueueSettingsSave();
        }

        // Kontrolka segmentowa = grupa RadioButtonów (Tag = wartość) w jednym StackPanelu. Odczyt/zapis po Tagu.
        private static string SegTag(Panel seg)
            => seg.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string;

        private static void SegSet(Panel seg, string tag)
        {
            foreach (var r in seg.Children.OfType<RadioButton>())
                r.IsChecked = string.Equals(r.Tag as string ?? "", tag ?? "", StringComparison.OrdinalIgnoreCase);
        }

        private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.IO.Directory.CreateDirectory(SettingsStore.Dir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SettingsStore.Dir) { UseShellExecute = true });
            }
            catch { /* brak eksploratora / brak uprawnień — ignorujemy */ }
        }

        private void ExportProfile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = L("S.dlg.exportprofile.title"),
                Filter = L("S.dlg.profile.filter"),
                FileName = "rdpmanager-profil.json"
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                System.IO.File.WriteAllText(dlg.FileName, ProfileBackup.Serialize(_settings, _vm.Servers));
                SettingsStatus.Text = L("S.st.exportedProfile");
            }
            catch (Exception ex)
            {
                MessageBox.Show(L("S.msg.exportprofile.fail") + "\n" + ex.Message,
                    L("S.msg.exportprofile.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ImportProfile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = L("S.dlg.importprofile.title"),
                Filter = L("S.dlg.profile.filterAll")
            };
            if (dlg.ShowDialog(this) != true) return;

            ProfileData data;
            try { data = ProfileBackup.Parse(System.IO.File.ReadAllText(dlg.FileName)); }
            catch (Exception ex)
            {
                MessageBox.Show(L("S.msg.importprofile.bad") + "\n" + ex.Message,
                    L("S.msg.importprofile.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (data == null)
            {
                MessageBox.Show(L("S.msg.importprofile.empty"), L("S.msg.importprofile.title"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string prompt = string.Format(L("S.msg.importprofile.confirm"), data.Servers.Count);
            if (_sessions.Count > 0)
                prompt += "\n" + string.Format(L("S.msg.importprofile.confirmSessions"), _sessions.Count);
            if (MessageBox.Show(prompt, L("S.msg.importprofile.title"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            // Zamknij sesje przed podmianą listy — inaczej trzymałyby osierocone ServerInfo.
            foreach (var s in _sessions.ToList())
                CloseSession(s);

            if (data.Settings != null)
            {
                _settings = data.Settings;
                SettingsStore.Save(_settings);
                _vm.UseRecentIds(_settings.RecentIds);
                ApplySettings();
                LoadSettingsForm();
            }
            _vm.LoadServers(data.Servers);
            PersistServers();
            RenderTree(SearchBox.Text);
            CheckReachabilityAsync();
            SettingsStatus.Text = L("S.st.importedProfile");
        }

        // ---------- Ostatnie / Pulpit ----------

        internal void RecordRecent(ServerInfo server)
        {
            if (string.IsNullOrEmpty(server?.Id)) return;
            _vm.RecordRecent(server.Id);
            SettingsStore.Save(_settings);   // RecentIds jest współdzielone z _settings
        }

        private void BuildRecent()
        {
            RecentPanel.Children.Clear();
            bool any = false;
            foreach (var srv in _vm.RecentServers())
            {
                any = true;
                var s = srv;
                RecentPanel.Children.Add(BuildFlyoutRow(s, s.Status, false, () => LaunchServer(s, true)));
            }
            if (!any)
                RecentPanel.Children.Add(new TextBlock { Text = L("S.dash.norecent"), Foreground = Res("TextTer") });
        }

        private void BuildDashboard()
        {
            DashboardPanel.Children.Clear();

            var stats = LoadConnectionStats(14);
            int open = _sessions.Count + _sessionWindows.Count;
            // KPI „serwerowe" liczone BEZ wpisów REST: kolekcje nie są sondowane (zawsze „offline"),
            // więc zawyżały segment offline pierścienia; REST ma własny moduł, a nie miejsce na liście.
            var srvs = _vm.Servers.Where(s => s.Protocol != RemoteProtocol.Rest).ToList();
            int online = srvs.Count(s => s.Status == ServerStatus.Online);
            int idle = srvs.Count(s => s.Status == ServerStatus.Idle);
            int offline = srvs.Count(s => s.Status == ServerStatus.Offline);
            int groups = srvs
                .Select(s => string.IsNullOrWhiteSpace(s.Group) ? L("S.group.serversdefault") : s.Group)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var lats = srvs.Where(s => s.LatencyMs >= 0).Select(s => s.LatencyMs).ToList();
            string avgLat = lats.Count > 0 ? ((int)Math.Round(lats.Average())).ToString() : "—";

            // KPI — dzielniki pionowe, bez ramek (Compass §4.8).
            var kpi = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 4, 0, 20) };
            kpi.Children.Add(KpiCell(srvs.Count.ToString(), "", L("S.dash.servers"), Res("TextPrim"), false));
            kpi.Children.Add(KpiCell(online.ToString(), "", L("S.dash.online"), Res("Online"), false));
            kpi.Children.Add(KpiCell(open.ToString(), "", L("S.dash.sessions"), Res("TextPrim"), false));
            kpi.Children.Add(KpiCell(avgLat, avgLat == "—" ? "" : " ms", L("S.dash.avglatency"), Res("TextPrim"), false));
            kpi.Children.Add(KpiCell(groups.ToString(), "", L("S.dash.groups"), Res("TextPrim"), true));
            DashboardPanel.Children.Add(kpi);

            // Siatka 2×2 kart hairline — rozciągnięta na szerokość pulpitu (górny limit, by nie urosła absurdalnie).
            var grid = new Grid { MaxWidth = 1500, HorizontalAlignment = HorizontalAlignment.Stretch };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.55, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            void Place(FrameworkElement card, int col, int row)
            {
                card.Margin = new Thickness(col == 0 ? 0 : 9, row == 0 ? 0 : 18, col == 0 ? 9 : 0, 0);
                Grid.SetColumn(card, col); Grid.SetRow(card, row); grid.Children.Add(card);
            }
            Place(ChartCard(L("S.dash.latency24h"), avgLat == "—" ? "" : "śr. " + avgLat + " ms", MakeLatencyChart()), 0, 0);
            Place(ChartCard(L("S.dash.availability"), online + "/" + srvs.Count, MakeAvailabilityChart(online, idle, offline)), 1, 0);
            Place(ChartCard(L("S.dash.connweek"), stats.PerWeekday.Sum().ToString(), MakeWeekdayChart(stats.PerWeekday)), 0, 1);
            Place(ChartCard(L("S.dash.protocols"), _vm.Total.ToString(), MakeProtocolBar()), 1, 1);
            DashboardPanel.Children.Add(grid);
        }

        // Kafelek KPI: duża wartość (+ jednostka) nad etykietą; pionowy dzielnik po prawej (poza ostatnim).
        private FrameworkElement KpiCell(string value, string unit, string label, Brush valueBrush, bool last)
        {
            var val = new TextBlock { FontSize = 25, FontWeight = FontWeights.Bold };
            val.Inlines.Add(new Run(value) { Foreground = valueBrush });
            if (!string.IsNullOrEmpty(unit))
                val.Inlines.Add(new Run(unit) { Foreground = Res("TextTer"), FontSize = 14, FontWeight = FontWeights.SemiBold });
            var sp = new StackPanel();
            sp.Children.Add(val);
            sp.Children.Add(new TextBlock { Text = label, Foreground = Res("TextSec"), FontSize = (double)TryFindResource("FontCaption"), Margin = new Thickness(0, 4, 0, 0) });
            return new Border
            {
                Child = sp,
                Padding = new Thickness(0, 0, last ? 0 : 28, 0),
                Margin = new Thickness(0, 0, last ? 0 : 28, 0),
                BorderBrush = Res("Border"),
                BorderThickness = new Thickness(0, 0, last ? 0 : 1, 0)
            };
        }

        // Karta wykresu (hairline, radius 14) z nagłówkiem: tytuł + prawy podpis (mono).
        private FrameworkElement ChartCard(string title, string sub, FrameworkElement body)
        {
            var head = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new TextBlock { Text = title, Foreground = Res("TextPrim"), FontWeight = FontWeights.SemiBold, FontSize = (double)TryFindResource("FontBody") });
            var subTb = new TextBlock { Text = sub, Foreground = Res("TextTer"), FontFamily = (FontFamily)TryFindResource("Mono"), FontSize = (double)TryFindResource("FontCaption"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(subTb, 1); head.Children.Add(subTb);
            var panel = new StackPanel();
            panel.Children.Add(head);
            panel.Children.Add(body);
            return new Border
            {
                Background = Res("Panel"), BorderBrush = Res("Border"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14), Padding = new Thickness(15, 13, 17, 13), Child = panel
            };
        }

        // Kolor z zasobu pędzla (fallback, gdy zasób nie jest SolidColorBrush).
        private Color Col(string key, Color fallback) => (Res(key) as SolidColorBrush)?.Color ?? fallback;

        private FrameworkElement ChartHint(string text) => new TextBlock
        {
            Text = text, Foreground = Res("TextTer"), Height = 210, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };

        // Opóźnienie: sparkline z wypełnieniem (linia + area + kropka na końcu) — natywne kształty WPF,
        // rysowane na Canvasie przy każdej zmianie rozmiaru (rozciąga się na szerokość karty jak w mockupie).
        private FrameworkElement MakeLatencyChart()
        {
            var latencySamples = _reach?.LatencySamples;
            if (latencySamples == null || latencySamples.Count < 2) return ChartHint(L("S.dash.nolatency"));
            var samples = latencySamples.ToArray();
            var acc = Col("Accent", Color.FromRgb(0x4C, 0x86, 0xFF));

            var area = new Polygon { Fill = new SolidColorBrush(Color.FromArgb(38, acc.R, acc.G, acc.B)) };
            var line = new Polyline { Stroke = new SolidColorBrush(acc), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
            var dot = new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(acc) };
            var canvas = new Canvas { Height = 210, ClipToBounds = true };
            canvas.Children.Add(area);
            canvas.Children.Add(line);
            canvas.Children.Add(dot);

            double min = samples.Min(), span = Math.Max(1, samples.Max() - min);
            void Redraw()
            {
                double w = canvas.ActualWidth, h = canvas.ActualHeight;
                if (w <= 0 || h <= 0) return;
                const double pad = 8;   // margines od góry/dołu, żeby linia nie dotykała krawędzi
                var pts = new PointCollection();
                for (int i = 0; i < samples.Length; i++)
                {
                    double x = samples.Length == 1 ? w : w * i / (samples.Length - 1);
                    double y = pad + (h - 2 * pad) * (1 - (samples[i] - min) / span);
                    pts.Add(new Point(x, y));
                }
                line.Points = pts;
                var poly = new PointCollection(pts);
                poly.Add(new Point(pts[pts.Count - 1].X, h));
                poly.Add(new Point(pts[0].X, h));
                area.Points = poly;
                var last = pts[pts.Count - 1];
                Canvas.SetLeft(dot, last.X - dot.Width / 2);
                Canvas.SetTop(dot, last.Y - dot.Height / 2);
            }
            canvas.SizeChanged += (s, e) => Redraw();
            canvas.Loaded += (s, e) => Redraw();
            return canvas;
        }

        // Dostępność: pierścień (online/idle/offline) + legenda z licznikami — natywny donut
        // (tor + łuki rysowane Path/ArcSegment, grubym obrysem; zgodnie z mockupem).
        private FrameworkElement MakeAvailabilityChart(int online, int idle, int offline)
        {
            int total = online + idle + offline;
            if (total == 0) return ChartHint(L("S.dash.nodata"));

            const double size = 158, r = 65, thick = 11, cx = size / 2, cy = size / 2;
            var canvas = new Canvas { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };

            // Tor pierścienia (pełny okrąg).
            var track = new Ellipse { Width = 2 * r, Height = 2 * r, Stroke = Res("Elevated"), StrokeThickness = thick };
            Canvas.SetLeft(track, cx - r); Canvas.SetTop(track, cy - r);
            canvas.Children.Add(track);

            Point P(double frac) { double a = frac * 2 * Math.PI - Math.PI / 2; return new Point(cx + r * Math.Cos(a), cy + r * Math.Sin(a)); }
            double cursor = 0;
            void Arc(int count, Brush color)
            {
                if (count <= 0) return;
                double frac = (double)count / total;
                if (frac >= 0.999)   // jeden segment na pełen okrąg — ArcSegment byłby zdegenerowany
                {
                    var full = new Ellipse { Width = 2 * r, Height = 2 * r, Stroke = color, StrokeThickness = thick };
                    Canvas.SetLeft(full, cx - r); Canvas.SetTop(full, cy - r);
                    canvas.Children.Add(full); cursor += frac; return;
                }
                var fig = new PathFigure { StartPoint = P(cursor), IsClosed = false };
                fig.Segments.Add(new ArcSegment { Point = P(cursor + frac), Size = new Size(r, r), SweepDirection = SweepDirection.Clockwise, IsLargeArc = frac > 0.5 });
                var geo = new PathGeometry(); geo.Figures.Add(fig);
                canvas.Children.Add(new Path { Data = geo, Stroke = color, StrokeThickness = thick, StrokeStartLineCap = PenLineCap.Flat, StrokeEndLineCap = PenLineCap.Flat });
                cursor += frac;
            }
            Arc(online, Res("Online")); Arc(idle, Res("Idle")); Arc(offline, Res("Offline"));

            var legend = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(26, 0, 0, 0) };
            legend.Children.Add(LegendRow(Res("Online"), L("S.status.online"), online));
            legend.Children.Add(LegendRow(Res("Idle"), L("S.status.idle"), idle));
            legend.Children.Add(LegendRow(Res("Offline"), L("S.status.offline"), offline));
            // Wyśrodkowane w karcie (donut + legenda), żeby nie zostawiać pustej prawej strony po rozciągnięciu.
            var row = new StackPanel { Orientation = Orientation.Horizontal, Height = 210, HorizontalAlignment = HorizontalAlignment.Center };
            row.Children.Add(canvas);
            row.Children.Add(legend);
            return row;
        }

        private FrameworkElement LegendRow(Brush color, string label, int count)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            sp.Children.Add(new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(2), Background = color, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            sp.Children.Add(new TextBlock { Text = label, Foreground = Res("TextSec"), FontSize = (double)TryFindResource("FontSmall"), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = count.ToString(), Foreground = Res("TextPrim"), FontFamily = (FontFamily)TryFindResource("Mono"), FontWeight = FontWeights.SemiBold, FontSize = (double)TryFindResource("FontSmall"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            return sp;
        }

        // Połączenia w tygodniu: słupki (Pn–Nd) z dziennika audytu — natywne prostokąty z etykietami.
        private FrameworkElement MakeWeekdayChart(int[] weekday)
        {
            if (weekday == null || weekday.Sum() == 0) return ChartHint(L("S.dash.nodata"));
            var labels = L("S.dash.weekdays").Split(',').Select(x => x.Trim()).ToArray();
            int max = Math.Max(1, weekday.Max());
            var acc = Col("Accent", Color.FromRgb(0x4C, 0x86, 0xFF));
            var fill = new SolidColorBrush(Color.FromArgb(215, acc.R, acc.G, acc.B));

            var bars = new UniformGrid { Rows = 1, Columns = weekday.Length, Height = 178, VerticalAlignment = VerticalAlignment.Bottom };
            var lbls = new UniformGrid { Rows = 1, Columns = weekday.Length, Margin = new Thickness(0, 6, 0, 0) };
            for (int i = 0; i < weekday.Length; i++)
            {
                bars.Children.Add(new Border
                {
                    Height = Math.Max(3, 164.0 * weekday[i] / max),
                    VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6, 0, 6, 0),
                    CornerRadius = new CornerRadius(4, 4, 0, 0),
                    Background = weekday[i] > 0 ? (Brush)fill : Res("Elevated"),
                    ToolTip = (i < labels.Length ? labels[i] : "") + " — " + weekday[i]
                });
                lbls.Children.Add(new TextBlock
                {
                    Text = i < labels.Length ? labels[i] : "",
                    Foreground = Res("TextTer"), FontSize = 11, TextAlignment = TextAlignment.Center
                });
            }
            var host = new StackPanel { Height = 210 };
            host.Children.Add(bars);
            host.Children.Add(lbls);
            return host;
        }

        // Protokoły: poziomy pasek udziału (segmenty w kolorach protokołów) + legenda z licznikami.
        private FrameworkElement MakeProtocolBar()
        {
            var protos = _vm.Servers.GroupBy(s => s.Protocol)
                .Select(g => new { g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count).ThenBy(x => x.Key.ToString()).ToList();
            int total = protos.Sum(p => p.Count);
            if (total == 0) return ChartHint(L("S.dash.nodata"));

            var barGrid = new Grid();
            for (int i = 0; i < protos.Count; i++)
            {
                barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(protos[i].Count, GridUnitType.Star) });
                var seg = new Border { Background = ProtocolBrush(protos[i].Key), Margin = new Thickness(0, 0, i < protos.Count - 1 ? 2 : 0, 0) };
                Grid.SetColumn(seg, i); barGrid.Children.Add(seg);
            }
            var bar = new Border { Height = 11, CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = barGrid, Margin = new Thickness(0, 6, 0, 14) };

            var legend = new WrapPanel();
            foreach (var p in protos)
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 8) };
                item.Children.Add(new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(2), Background = ProtocolBrush(p.Key), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
                item.Children.Add(new TextBlock { Text = ProtocolLabel(p.Key), Foreground = Res("TextSec"), FontSize = (double)TryFindResource("FontSmall"), VerticalAlignment = VerticalAlignment.Center });
                item.Children.Add(new TextBlock { Text = p.Count.ToString(), Foreground = Res("TextPrim"), FontFamily = (FontFamily)TryFindResource("Mono"), FontWeight = FontWeights.SemiBold, FontSize = (double)TryFindResource("FontSmall"), Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                legend.Children.Add(item);
            }
            var host = new StackPanel { Height = 210 };
            host.Children.Add(bar);
            host.Children.Add(legend);
            return host;
        }

        private Core.ConnectionStats LoadConnectionStats(int days)
        {
            try
            {
                string path = System.IO.Path.Combine(SettingsStore.Dir, "connections.log");
                if (System.IO.File.Exists(path))
                    return Core.ConnectionStats.Compute(System.IO.File.ReadLines(path), DateTime.Now, days);
            }
            catch { /* dziennik best-effort */ }
            return Core.ConnectionStats.Compute(null, DateTime.Now, days);
        }

        private FrameworkElement DashSection(string text) => new TextBlock
        {
            Text = text, Foreground = Res("TextSec"),
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 8)
        };

        private FrameworkElement DashCard(FrameworkElement content) => new Border
        {
            Background = Res("Panel"), BorderBrush = Res("Border"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(0, 0, 0, 22), Child = content
        };

        private FrameworkElement DashHint(string text) => new TextBlock
        {
            Text = text, Foreground = Res("TextTer"), Margin = new Thickness(2, 0, 0, 22)
        };

        // Wykres słupkowy „połączenia / dzień" — rysowany prostokątami (bez zależności od bibliotek).
        private FrameworkElement BuildBarChart(int[] values, DateTime endDate)
        {
            int max = Math.Max(1, values.Max());
            var row = new StackPanel { Orientation = Orientation.Horizontal, Height = 108,
                VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left };
            var accent = Res("Accent");
            var dim = Res("Elevated");
            for (int i = 0; i < values.Length; i++)
            {
                var date = endDate.Date.AddDays(-(values.Length - 1 - i));
                row.Children.Add(new System.Windows.Shapes.Rectangle
                {
                    Width = 18, Height = Math.Max(3, 92.0 * values[i] / max), RadiusX = 3, RadiusY = 3,
                    Fill = values[i] > 0 ? accent : dim, Margin = new Thickness(3, 0, 3, 0),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    ToolTip = date.ToString("dd.MM") + " — " + values[i]
                });
            }
            return row;
        }

        // Poziome słupki: nazwa | pasek (proporcjonalny) | liczba.
        private FrameworkElement BuildTopServers(List<KeyValuePair<string, int>> top)
        {
            int max = Math.Max(1, top.Max(t => t.Value));
            var accent = Res("Accent");
            var track = Res("Elevated");
            var panel = new StackPanel();
            foreach (var kv in top)
            {
                var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var name = new TextBlock { Text = kv.Key, Foreground = Res("TextPrim"),
                    FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(name, 0); grid.Children.Add(name);

                var barGrid = new Grid { Width = 200, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 10, 0) };
                barGrid.Children.Add(new Border { Height = 8, Background = track, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Stretch });
                barGrid.Children.Add(new Border { Height = 8, Width = 200.0 * kv.Value / max, Background = accent,
                    CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left });
                Grid.SetColumn(barGrid, 1); grid.Children.Add(barGrid);

                var count = new TextBlock { Text = kv.Value.ToString(), Foreground = Res("TextSec"),
                    FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, MinWidth = 24, TextAlignment = TextAlignment.Right };
                Grid.SetColumn(count, 2); grid.Children.Add(count);

                panel.Children.Add(grid);
            }
            return panel;
        }

        internal static string ProtocolLabel(RemoteProtocol p) => p switch
        {
            RemoteProtocol.Rdp => "RDP",
            RemoteProtocol.Ssh => "SSH",
            RemoteProtocol.Telnet => "Telnet",
            RemoteProtocol.Serial => "Serial (COM)",
            RemoteProtocol.Http => "WWW",
            RemoteProtocol.Vnc => "VNC",
            RemoteProtocol.Sftp => "SFTP",
            RemoteProtocol.Ftp => "FTP",
            RemoteProtocol.Rest => "REST",
            _ => p.ToString()
        };

        // Krótki znacznik protokołu do etykiety w wierszu (kolumna wąska; „Serial (COM)" → „COM").
        internal static string ProtocolShort(RemoteProtocol p) => p switch
        {
            RemoteProtocol.Telnet => "TEL",
            RemoteProtocol.Serial => "COM",
            RemoteProtocol.Http => "WWW",
            _ => ProtocolLabel(p)
        };

        // Kolor etykiety protokołu (Compass §2). VNC dzieli kolor z RDP (pulpit zdalny), FTP z SFTP
        // (transfer plików), Serial z Telnet (terminal) — brak osobnych kluczy dla tych trzech.
        internal Brush ProtocolBrush(RemoteProtocol p) => p switch
        {
            RemoteProtocol.Rdp => Res("ProtoRdp"),
            RemoteProtocol.Vnc => Res("ProtoRdp"),
            RemoteProtocol.Ssh => Res("ProtoSsh"),
            RemoteProtocol.Sftp => Res("ProtoSftp"),
            RemoteProtocol.Ftp => Res("ProtoSftp"),
            RemoteProtocol.Rest => Res("ProtoRest"),
            RemoteProtocol.Http => Res("ProtoWeb"),
            RemoteProtocol.Telnet => Res("ProtoTelnet"),
            RemoteProtocol.Serial => Res("ProtoTelnet"),
            _ => Res("TextSec")
        };


        private FrameworkElement StatCard(string label, string value)
        {
            var card = new Border
            {
                Background = Res("Panel"), BorderBrush = Res("Border"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10), Padding = new Thickness(18, 14, 18, 14), Margin = new Thickness(0, 0, 12, 0), MinWidth = 130
            };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = value, Foreground = Res("Accent"), FontSize = 26, FontWeight = FontWeights.Bold });
            sp.Children.Add(new TextBlock { Text = label, Foreground = Res("TextSec"), FontSize = 12 });
            card.Child = sp;
            return card;
        }

        private void FsBarThumb_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e) => _fs.OnBarThumbDragDelta(e);


        private bool IsMinimalList => _settings != null && _settings.ListStyle == "Minimal";

        // ---------- Drzewo serwerów → Controllers/ServerTreeController (PR 3 refaktoru) ----------
        // Cienkie shimy: RenderTree wołane z ~14 miejsc, UpdateActiveRows z Activate, BuildServerContextMenu
        // z modułu REST; SearchBox_TextChanged to handler podpięty w XAML.
        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => _tree.RenderTree(SearchBox.Text);
        private void RenderTree(string filter = null) => _tree.RenderTree(filter);
        internal void UpdateActiveRows() => _tree.UpdateActiveRows();
        private ContextMenu BuildServerContextMenu(ServerInfo server) => _tree.BuildServerContextMenu(server);

        // ---------- Otwieranie / przełączanie sesji ----------

        // Klik „otwórz serwer" (drzewo / ostatnie / pulpit / szybkie połączenie): karta w managerze
        // albo od razu osobne okno — zależnie od ustawienia OpenInNewWindowByDefault.
        /// <summary>Adres do wyświetlenia — z prefiksem protokołu, żeby odróżnić na pierwszy rzut oka.</summary>
        internal static string DisplayHost(ServerInfo s)
        {
            switch (s.Protocol)
            {
                case RemoteProtocol.Ssh: return "ssh://" + s.Host;
                case RemoteProtocol.Telnet: return "telnet://" + s.Host;
                case RemoteProtocol.Vnc: return "vnc://" + s.Host;
                case RemoteProtocol.Sftp: return "sftp://" + s.Host;
                case RemoteProtocol.Ftp: return "ftp://" + s.Host;
                case RemoteProtocol.Serial: return s.Host + " @" + s.Port;   // COM3 @115200
                default: return s.Host;
            }
        }

        // ---------- Pasek zakładek: delegacje do TabStripController (PR 4) ----------
        // Wołane z wielu miejsc cyklu życia sesji/motywu — cienki shim zamiast edycji każdego wywołania.
        internal void RebuildTabStrip() => _tabs.RebuildTabStrip();
        internal void RefreshTabStyles() => _tabs.RefreshTabStyles();
        internal void RefreshTabTitles() => _tabs.RefreshTabTitles();
        internal void SetTabStatus(Session s, ServerStatus status) => _tabs.SetTabStatus(s, status);

        // ---------- Cykl życia sesji: delegacje do SessionManager (PR 6) ----------
        // Handlery XAML + metody wołane z innych kontrolerów/miejsc — cienki shim zamiast edycji każdego wołania.
        private void OverlayAction_Click(object sender, RoutedEventArgs e) => _sm.OverlayAction_Click(sender, e);
        private void Connect_Click(object sender, RoutedEventArgs e) => _sm.Connect_Click(sender, e);
        private void PassBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) => _sm.PassBox_KeyDown(sender, e);
        private void Files_Click(object sender, RoutedEventArgs e) => _sm.Files_Click(sender, e);
        private void Disconnect_Click(object sender, RoutedEventArgs e) => _sm.Disconnect_Click(sender, e);
        private void SendCtrlAltDel_Click(object sender, RoutedEventArgs e) => _sm.SendCtrlAltDel_Click(sender, e);
        private void WinAuth_Changed(object sender, RoutedEventArgs e) => _sm.WinAuth_Changed(sender, e);
        internal void OpenServer(ServerInfo server, bool autoConnect = false, bool forceNew = false) => _sm.OpenServer(server, autoConnect, forceNew);
        internal void LaunchServer(ServerInfo server, bool autoConnect, bool forceNew = false) => _sm.LaunchServer(server, autoConnect, forceNew);
        internal void OpenInNewWindow(ServerInfo server, string password = null) => _sm.OpenInNewWindow(server, password);
        internal void Activate(Session session) => _sm.Activate(session);
        internal void RequestCloseSession(Session session) => _sm.RequestCloseSession(session);
        internal void CloseOtherSessions(Session keep) => _sm.CloseOtherSessions(keep);
        internal void DuplicateSession(Session s) => _sm.DuplicateSession(s);
        internal void BroadcastToSsh() => _sm.BroadcastToSsh();
        internal void SendCtrlAltDel(Session s) => _sm.SendCtrlAltDel(s);
        internal void TearOffToWindow(Session s) => _sm.TearOffToWindow(s);
        internal void EnterSplit(Session right) => _sm.EnterSplit(right);
        internal void ExitSplit() => _sm.ExitSplit();
        internal bool ShowSplitDropZone(Session dragged) => _sm.ShowSplitDropZone(dragged);
        internal void HideSplitDropZone() => _sm.HideSplitDropZone();
        internal void PromptAndConnect(Session s, string reason) => _sm.PromptAndConnect(s, reason);
        internal void SetStatus(string text, StatusKind kind = StatusKind.Info) => _sm.SetStatus(text, kind);
        private void BeginConnect(Session s) => _sm.BeginConnect(s);
        private void CloseSession(Session session) => _sm.CloseSession(session);
        private void LoadToolbar(Session s) => _sm.LoadToolbar(s);
        private void UpdateToolbarMode() => _sm.UpdateToolbarMode();
        private void UpdateToolbarEnabled() => _sm.UpdateToolbarEnabled();


        // ---------- Konto Windows ----------


        // ---------- Pełny ekran ----------

        // Otwiera serwer w OSOBNYM oknie sesji (model jak mstsc — kontrolka żyje w tym oknie na stałe).
        // Domyślne otwieranie idzie do zakładki w oknie głównym; to jest opcja na drugi monitor.
        // password != null → przenosimy hasło z zakładki przy „wyciąganiu" (bez ponownego pytania).
        private void Fullscreen_Click(object sender, RoutedEventArgs e) => _fs.ToggleFullscreen();

        internal const int MONITOR_DEFAULTTONEAREST = 0x2;
        internal const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77,
                          SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

        internal const uint SWP_SHOWWINDOW = 0x0040;

        [DllImport("user32.dll")]
        internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        internal static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

        [DllImport("user32.dll")]
        internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        internal static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        internal const byte VK_CONTROL = 0x11, VK_MENU = 0x12, VK_END = 0x23;
        internal const uint KEYEVENTF_KEYUP = 0x0002;
        internal const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

        [DllImport("user32.dll")]
        internal static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            FsPopup.IsOpen = false;
            WindowState = WindowState.Minimized;   // po przywróceniu wraca do pełnego ekranu (granice zachowane)
        }

        private void FsPopup_MouseLeave(object sender, MouseEventArgs e) => _fs.OnFsPopupMouseLeave();

        // ---------- Flyout "inne połączenia" (krok 7) ----------

        private void ToggleFlyout_Click(object sender, RoutedEventArgs e)
        {
            if (FsFlyout.Visibility == Visibility.Visible) { CollapseFlyout(); return; }
            FsFlyoutSearch.Text = "";
            BuildFlyoutLists("");
            FsFlyout.Visibility = Visibility.Visible;
            InnyBtn.Content = L("S.fs.others.up");
            // Fokus na szukajkę zaraz po wyrenderowaniu popupu (dostępność klawiaturowa).
            FsFlyoutSearch.Dispatcher.BeginInvoke(
                new Action(() => FsFlyoutSearch.Focus()), DispatcherPriority.Input);
        }

        private void TogglePin_Click(object sender, RoutedEventArgs e) => _fs.TogglePin();

        private void TabScroller_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) return;   // Ctrl+kółko = zoom
            if (sender is ScrollViewer sv)
            {
                sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
                e.Handled = true;
            }
        }

        internal void CollapseFlyout()
        {
            FsFlyout.Visibility = Visibility.Collapsed;
            InnyBtn.Content = L("S.fs.others");
        }

        private void FsFlyoutSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            BuildFlyoutLists(FsFlyoutSearch.Text);
        }

        // ---------- Szybkie przełączanie z paska kart (tryb skupienia) ----------

        private Action _qsFirstAction;   // Enter w szukajce (bez zaznaczenia strzałkami) = pierwsze trafienie

        // Paleta poleceń (Ctrl+P): płaska lista wierszy do nawigacji klawiaturą (Góra/Dół) + indeks zaznaczenia.
        private readonly List<(Border row, Brush rest, Action go)> _paletteFlat = new List<(Border, Brush, Action)>();
        private int _paletteSel = -1;

        // Wspólne wejście do palety poleceń: z przycisku na pasku (tryb skupienia) i z Ctrl+P (tryb zwykły).
        private void QuickSwitch_Click(object sender, RoutedEventArgs e) => OpenCommandPalette();

        private void OpenCommandPalette()
        {
            QuickSwitchSearch.Text = "";
            BuildQuickSwitchLists("");
            QuickSwitchPopup.IsOpen = true;
            QuickSwitchSearch.Dispatcher.BeginInvoke(new Action(() => QuickSwitchSearch.Focus()), DispatcherPriority.Input);
        }

        private void QuickSwitchSearch_TextChanged(object sender, TextChangedEventArgs e)
            => BuildQuickSwitchLists(QuickSwitchSearch.Text);

        private void QuickSwitchSearch_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Down) { MovePaletteSel(1); e.Handled = true; }
            else if (e.Key == Key.Up) { MovePaletteSel(-1); e.Handled = true; }
            else if (e.Key == Key.Enter)
            {
                var go = (_paletteSel >= 0 && _paletteSel < _paletteFlat.Count) ? _paletteFlat[_paletteSel].go : _qsFirstAction;
                if (go != null) { go(); e.Handled = true; }
            }
            else if (e.Key == Key.Escape) { QuickSwitchPopup.IsOpen = false; e.Handled = true; }
        }

        private void BuildQuickSwitchLists(string filter)
        {
            string f = (filter ?? "").Trim().ToLowerInvariant();
            QsSessions.Children.Clear();
            QsServers.Children.Clear();
            QsActions.Children.Clear();
            _paletteFlat.Clear();
            _paletteSel = -1;
            _qsFirstAction = null;

            foreach (var s in RankServers(_sessions, x => x.Server, f))
            {
                var session = s;
                Action go = () => { QuickSwitchPopup.IsOpen = false; Activate(session); };
                if (_qsFirstAction == null && s != _active) _qsFirstAction = go;
                AddPaletteRow(QsSessions, (Border)BuildFlyoutRow(s.Server,
                    s.Connected ? ServerStatus.Online : ServerStatus.Offline, s == _active, go), go);
            }

            // Bez wpisów REST — kolekcje żyją w module REST, nie na liście serwerów (otwarte konsole
            // dalej są w sekcji sesji powyżej).
            foreach (var server in RankServers(_vm.Servers.Where(x => x.Protocol != RemoteProtocol.Rest), x => x, f))
            {
                var srv = server;
                Action go = () => { QuickSwitchPopup.IsOpen = false; LaunchServer(srv, true); };
                if (_qsFirstAction == null) _qsFirstAction = go;
                AddPaletteRow(QsServers, (Border)BuildFlyoutRow(server, server.Status, false, go), go);
            }

            foreach (var item in RankActions(f))
            {
                var act = item.act;
                Action go = () => { QuickSwitchPopup.IsOpen = false; act(); };
                if (_qsFirstAction == null) _qsFirstAction = go;
                AddPaletteRow(QsActions, (Border)BuildActionRow(item.label, go), go);
            }

            // Chowaj nagłówek pustej grupy (mniej szumu przy filtrowaniu).
            QsSessionsHeader.Visibility = QsSessions.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            QsServersHeader.Visibility = QsServers.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            QsActionsHeader.Visibility = QsActions.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // Gating po nazwie/hoście/#tag (MatchesFilter) + ranking po Score (dokładne>prefiks>granica>podciąg).
        private IEnumerable<T> RankServers<T>(IEnumerable<T> items, Func<T, ServerInfo> sel, string filter)
        {
            var matched = new List<(T item, int score)>();
            foreach (var it in items)
            {
                var srv = sel(it);
                if (!RdpUtils.MatchesFilter(srv, filter)) continue;
                int score = Math.Max(Core.CommandPalette.Score(srv.Name ?? "", filter),
                                     Core.CommandPalette.Score(srv.Host ?? "", filter));
                matched.Add((it, score));
            }
            return matched.OrderByDescending(m => m.score).Select(m => m.item);
        }

        // Akcje palety: nawigacja + dodaj/szybkie/import + motyw, oraz akcje aktywnego serwera (gdy jest).
        private IEnumerable<(string label, Action act)> RankActions(string filter)
        {
            var all = new List<(string label, Action act)>
            {
                (L("S.nav.dashboard"),   () => ShowView("Dashboard")),
                (L("S.nav.sessions"),    () => ShowView("Sessions")),
                (L("S.nav.recent"),      () => ShowView("Recent")),
                (L("S.nav.settings"),    () => ShowView("Settings")),
                (L("S.quickConnect"),    () => QuickConnect_Click(this, null)),
                (L("S.addServer"),       () => AddServer_Click(this, null)),
                (L("S.importRdp"),       () => ImportRdp_Click(this, null)),
                (L("S.cmd.toggletheme"), ToggleTheme),
            };

            var srv = _active?.Server;
            if (srv != null)
            {
                all.Add((L("S.m.edit"), () => EditServer(srv)));
                all.Add((L("S.m.diag"), () => DiagnoseServer(srv)));
                if (!string.IsNullOrWhiteSpace(srv.MacAddress)) all.Add((L("S.m.wol"), () => WakeServer(srv)));
            }

            if (filter.Length == 0) return all;
            return all
                .Select(a => new { a, score = Core.CommandPalette.Score(a.label, filter) })
                .Where(x => x.score >= 0)
                .OrderByDescending(x => x.score)
                .Select(x => x.a);
        }

        private void AddPaletteRow(Panel group, Border row, Action go)
        {
            group.Children.Add(row);
            _paletteFlat.Add((row, row.Background, go));
        }

        // Góra/Dół po płaskiej liście wierszy (sesje -> serwery -> polecenia), z zawijaniem. Zaznaczenie = tło „Elevated".
        private void MovePaletteSel(int dir)
        {
            if (_paletteFlat.Count == 0) return;
            if (_paletteSel >= 0 && _paletteSel < _paletteFlat.Count)
                _paletteFlat[_paletteSel].row.Background = _paletteFlat[_paletteSel].rest;   // przywróć poprzednio zaznaczony

            _paletteSel = _paletteSel < 0
                ? (dir > 0 ? 0 : _paletteFlat.Count - 1)
                : ((_paletteSel + dir) % _paletteFlat.Count + _paletteFlat.Count) % _paletteFlat.Count;

            var sel = _paletteFlat[_paletteSel];
            sel.row.Background = Res("Elevated");
            sel.row.BringIntoView();
        }

        // Wiersz AKCJI w palecie (sama etykieta, bez awatara/kropki). Hover/klik jak w BuildFlyoutRow.
        private FrameworkElement BuildActionRow(string label, Action onClick)
        {
            var row = new Border
            {
                Padding = new Thickness(7, 6, 7, 6),
                CornerRadius = new CornerRadius(7),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 1, 0, 1),
                Child = new TextBlock
                {
                    Text = label, Foreground = Res("TextPrim"), FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0)
                }
            };
            row.MouseEnter += (s, e) => row.Background = Res("Elevated");
            row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (s, e) => { e.Handled = true; onClick(); };
            return row;
        }

        // Przełącz motyw Ciemny <-> Jasny z palety (System też ląduje w Ciemny/Jasny). Bez round-tripu przez combo
        // Ustawień (jego handler czyta też język/styl listy — mogłoby je nadpisać, gdy Ustawienia nie były otwarte).
        private void ToggleTheme()
        {
            _settings.Theme = _settings.Theme == "Light" ? "Dark" : "Light";
            ThemeManager.Apply(_settings.Theme, _settings.AccentColor, _settings.ThemeVariantDark, _settings.ThemeVariantLight);
            RenderTree(SearchBox.Text);   // wiersze/karty zależą od motywu
            RebuildTabStrip();
            RefreshThemedViews();
            QueueSettingsSave();
        }

        private void BuildFlyoutLists(string filter)
        {
            filter = (filter ?? "").Trim().ToLowerInvariant();
            FlyoutSessions.Children.Clear();
            FlyoutServers.Children.Clear();

            foreach (var s in _sessions)
            {
                if (!RdpUtils.MatchesFilter(s.Server, filter)) continue;
                var dot = s.Connected ? ServerStatus.Online : ServerStatus.Offline;
                var session = s;
                // Aktywuj KONKRETNĄ sesję (przy duplikatach HandleFlyoutClick trafiałby zawsze w pierwszą).
                FlyoutSessions.Children.Add(BuildFlyoutRow(s.Server, dot, s == _active, () =>
                {
                    Activate(session);
                    FsPopup.IsOpen = false;
                    CollapseFlyout();
                }));
            }

            foreach (var server in _vm.Servers)
            {
                if (!RdpUtils.MatchesFilter(server, filter)) continue;
                var srv = server;
                FlyoutServers.Children.Add(BuildFlyoutRow(server, server.Status, false, () => HandleFlyoutClick(srv)));
            }
        }

        private FrameworkElement BuildFlyoutRow(ServerInfo server, ServerStatus dotStatus, bool isActive, Action onClick)
        {
            var row = new Border
            {
                Padding = new Thickness(7, 6, 7, 6),
                CornerRadius = new CornerRadius(7),
                Background = isActive ? Res("AccentSoft") : Brushes.Transparent,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 1, 0, 1)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var avatar = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(5), Background = AvatarBrush(server),
                Child = new TextBlock
                {
                    Text = ServerInitials(server), Foreground = Brushes.White, FontSize = 7.5, FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            };
            Grid.SetColumn(avatar, 0);
            grid.Children.Add(avatar);

            var name = new TextBlock
            {
                Text = server.Name, Foreground = Res("TextPrim"), FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(name, 1);
            grid.Children.Add(name);

            var dot = new Ellipse { Width = 7, Height = 7, Fill = StatusBrush(dotStatus), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(dot, 2);
            grid.Children.Add(dot);

            row.Child = grid;
            row.MouseEnter += (s, e) => { if (!isActive) row.Background = Res("Elevated"); };
            row.MouseLeave += (s, e) => { if (!isActive) row.Background = Brushes.Transparent; };
            row.MouseLeftButtonUp += (s, e) => { e.Handled = true; onClick(); };
            return row;
        }

        private void HandleFlyoutClick(ServerInfo server)
        {
            var existing = _sessions.Find(x => x.Server == server);
            if (existing != null)
            {
                Activate(existing);            // przełącz — zostajemy w pełnym ekranie
                FsPopup.IsOpen = false;
                CollapseFlyout();
                return;
            }

            OpenServer(server);                // nowa zakładka + aktywacja (wczytuje też zapisane hasło)
            var s = _active;
            FsPopup.IsOpen = false;
            CollapseFlyout();

            // Konto Windows/zapisane hasło łączą od razu; brak poświadczeń -> prompt (modalny, działa też w pełnym ekranie).
            BeginConnect(s);
        }

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.F11) { _fs.ToggleFullscreen(); return; }
            if (e.Key == Key.Escape && _isFullscreen) { _fs.ToggleFullscreen(); return; }

            var mods = Keyboard.Modifiers;

            // Alt+1..9 -> skok do zakładki (Alt zamienia e.Key na Key.System, cyfra jest w SystemKey).
            if ((mods & ModifierKeys.Alt) != 0)
            {
                int n = DigitIndex(e.Key == Key.System ? e.SystemKey : e.Key);
                if (n >= 0) { ActivateByIndex(n); e.Handled = true; }
                return;
            }

            if ((mods & ModifierKeys.Control) != 0)
            {
                if (e.Key == Key.Tab) { CycleSession((mods & ModifierKeys.Shift) != 0 ? -1 : 1); e.Handled = true; }
                else if (e.Key == Key.W) { if (_active != null) RequestCloseSession(_active); e.Handled = true; }
                else if (e.Key == Key.F || e.Key == Key.K) { ShowView("Sessions"); SearchBox.Focus(); e.Handled = true; }
                else if (e.Key == Key.P) { OpenCommandPalette(); e.Handled = true; }   // paleta poleceń (tryb zwykły)
                else if (e.Key == Key.D0 || e.Key == Key.NumPad0) { ZoomTo(1.0); e.Handled = true; }
                else if (e.Key == Key.OemPlus || e.Key == Key.Add) { ZoomTo(_settings.UiScale + 0.1); e.Handled = true; }
                else if (e.Key == Key.OemMinus || e.Key == Key.Subtract) { ZoomTo(_settings.UiScale - 0.1); e.Handled = true; }
            }
        }

        // Zwraca 0-based indeks zakładki dla klawiszy 1..9 (górny rząd i NumPad), inaczej -1.
        private static int DigitIndex(Key k)
        {
            if (k >= Key.D1 && k <= Key.D9) return k - Key.D1;
            if (k >= Key.NumPad1 && k <= Key.NumPad9) return k - Key.NumPad1;
            return -1;
        }

        private void ActivateByIndex(int i)
        {
            if (i >= 0 && i < _sessions.Count) Activate(_sessions[i]);
        }

        private void CycleSession(int dir)
        {
            if (_sessions.Count == 0) return;
            int idx = _active == null ? -1 : _sessions.IndexOf(_active);
            idx = ((idx + dir) % _sessions.Count + _sessions.Count) % _sessions.Count;
            Activate(_sessions[idx]);
        }

        // ---------- Pomocnicze ----------

        // Szybkie połączenie: łączy od razu, BEZ zapisywania serwera na liście (sesja tymczasowa).
        private void QuickConnect_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new InputDialog(L("S.quickConnect"),
                L("S.prompt.quickconnect.label"), "") { Owner = this };
            if (dlg.ShowDialog() != true) return;
            QuickConnectFromText(dlg.Value);
        }

        // Wspólna ścieżka szybkiego łączenia (dialog z paska bocznego oraz pole w pustym stanie kanwy).
        private void QuickConnectFromText(string text)
        {
            var (host, port, user, domain) = RdpUtils.ParseQuickConnect(text, _settings.DefaultPort);
            if (string.IsNullOrWhiteSpace(host)) return;

            var srv = new ServerInfo
            {
                Name = host, Host = host, Port = port, Username = user, Domain = domain,
                Group = L("S.group.quick"), Status = ServerStatus.Offline
            };
            srv.Initials = RdpUtils.MakeInitials(srv.Name);

            // Tymczasowy — nie trafia do _vm.Servers ani do JSON; otwieramy sesję i łączymy
            // (jeśli brak poświadczeń, zapyta o nie prompt).
            LaunchServer(srv, autoConnect: true, forceNew: true);
        }

        // Pusty stan kanwy (brak aktywnej sesji): szybkie łączenie + chipy „ostatnie" (Compass §3).
        private void EmptyQuick_Click(object sender, RoutedEventArgs e) { QuickConnectFromText(EmptyQuickBox.Text); EmptyQuickBox.Text = ""; }

        private void EmptyQuick_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            QuickConnectFromText(EmptyQuickBox.Text);
            EmptyQuickBox.Text = "";
            e.Handled = true;
        }

        internal void BuildEmptyRecent()
        {
            EmptyRecentChips.Children.Clear();
            var recent = _vm.RecentServers().Take(6).ToList();
            Visibility vis = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyRecentLabel.Visibility = vis;
            EmptyRecentChips.Visibility = vis;
            foreach (var srv in recent)
            {
                var s = srv;
                EmptyRecentChips.Children.Add(MakeRecentChip(s));
            }
        }

        private FrameworkElement MakeRecentChip(ServerInfo s)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new Ellipse
            {
                Width = 7, Height = 7, Fill = ProtocolBrush(s.Protocol),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0)
            });
            sp.Children.Add(new TextBlock
            {
                Text = s.Name, Foreground = Res("TextPrim"),
                FontSize = (double)TryFindResource("FontSmall"), VerticalAlignment = VerticalAlignment.Center
            });
            var chip = new Border
            {
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 6, 6),
                Background = Res("Panel"),
                BorderBrush = Res("Border"),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Child = sp,
                ToolTip = s.Name + "\n" + DisplayHost(s)
            };
            chip.MouseLeftButtonUp += (a, e) => LaunchServer(s, true);
            return chip;
        }

        private void AddServer_Click(object sender, RoutedEventArgs e)
        {
            // Pusta grupa = domyślny „kosz" lokalizowany przy wyświetlaniu (RenderTree), nie zapisujemy tu nazwy PL.
            // Domyślne przekierowania z Ustawień → Połączenie (nowe serwery; „dyski" obejmują też drukarki).
            var server = new ServerInfo
            {
                Group = "", Status = ServerStatus.Offline, Port = _settings.DefaultPort,
                RedirectClipboard = _settings.DefaultRedirectClipboard,
                RedirectDrives = _settings.DefaultRedirectDrives,
                RedirectPrinters = _settings.DefaultRedirectDrives
            };
            var dlg = new ServerEditWindow(server, "", _credProfiles) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                _vm.Add(server);
                PersistServers();
                SaveCredential(server, dlg.EnteredPassword);
                RenderTree(SearchBox.Text);
                CheckReachabilityAsync();
                // Wpis REST nie pokazuje się na liście serwerów — przełącz na widok REST, żeby było
                // widać, gdzie trafił (inaczej „dodałem i zniknęło").
                if (server.Protocol == RemoteProtocol.Rest) ShowView("Rest");
            }
        }

        // Import: modal z kartami źródeł (Compass §4.7). Modal zwraca wybór; właściwy import (z oknem
        // wyboru pliku) uruchamiamy tutaj, żeby dialog należał do MainWindow.
        private void OpenImport_Click(object sender, RoutedEventArgs e)
        {
            var w = new ImportWindow { Owner = this };
            if (w.ShowDialog() != true) return;
            switch (w.Selected)
            {
                case "mstsc": ImportMstsc_Click(this, null); break;
                case "rdp": ImportRdp_Click(this, null); break;
                case "mrng": ImportMrng_Click(this, null); break;
                case "rdcman": ImportRdg_Click(this, null); break;
                case "rdm": ImportRdm_Click(this, null); break;
                case "filezilla": ImportFileZilla_Click(this, null); break;
                case "postman": ImportPostman_Click(this, null); break;
            }
        }

        private void ImportMrng_Click(object sender, RoutedEventArgs e)
            => ImportExternal(L("S.dlg.importmrng.title"), L("S.dlg.mrng.filter"),
                text => ExternalImport.ParseMRemoteNg(text));

        private void ImportRdg_Click(object sender, RoutedEventArgs e)
            => ImportExternal(L("S.dlg.importrdg.title"), L("S.dlg.rdg.filter"),
                text => ExternalImport.ParseRdcMan(text, _settings.DefaultPort));

        private void ImportRdm_Click(object sender, RoutedEventArgs e)
            => ImportExternal(L("S.dlg.importrdm.title"), L("S.dlg.rdm.filter"),
                text => ExternalImport.ParseRdm(text));

        private void ImportFileZilla_Click(object sender, RoutedEventArgs e)
            => ImportExternal(L("S.dlg.importfz.title"), L("S.dlg.fz.filter"),
                text => ExternalImport.ParseFileZilla(text),
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileZilla"));

        // Import kolekcji Postman: tworzy JEDEN wpis REST (= kolekcja) i zasila jego drzewo w rest.json.
        // Sekrety (Bearer/Basic) → Credential Manager. Inaczej niż ImportExternal (który dodaje wiele serwerów).
        // Plik ŚRODOWISKA (eksport env Postmana: ma „values", brak „item") też jest tu obsługiwany — karta
        // importu obiecuje „kolekcje i środowiska", a parser kolekcji wywalał się na env-eksporcie.
        private void ImportPostman_Click(object sender, RoutedEventArgs e)
        {
            string title = L("S.dlg.importpostman.title");
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = L("S.dlg.postman.filter") };
            if (dlg.ShowDialog(this) != true) return;

            string text;
            try { text = System.IO.File.ReadAllText(dlg.FileName); }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format(L("S.msg.importrdp.fail"), System.IO.Path.GetFileName(dlg.FileName)) + "\n" + ex.Message,
                    title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (TryImportPostmanEnvironment(text)) return;

            Core.PostmanImport.Result res;
            try { res = Core.PostmanImport.Parse(text); }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format(L("S.msg.importrdp.fail"), System.IO.Path.GetFileName(dlg.FileName)) + "\n" + ex.Message,
                    title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (res.RequestCount == 0)
            {
                MessageBox.Show(L("S.msg.postman.empty"), title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var entry = new ServerInfo
            {
                Name = res.Name,
                Protocol = RemoteProtocol.Rest,
                Host = "",
                Group = "HTTP",   // kolekcje zbierają się w grupie HTTP w drzewie serwerów
                Initials = RdpUtils.MakeInitials(res.Name)
            };
            _vm.Add(entry);

            // Środowiska z importu są teraz GLOBALNE — przenieś je do wspólnego store (ActiveEnvironmentId
            // kolekcji wskazuje ich Id, więc wybór po imporcie działa). Wyczyść z kolekcji, by nie dublować w rest.json.
            if (res.Collection.Environments.Count > 0)
            {
                var envs = EnvironmentStore.Load();
                foreach (var env in res.Collection.Environments)
                    if (!envs.Any(x => x.Id == env.Id)) envs.Add(env);
                EnvironmentStore.Save(envs);
                res.Collection.Environments.Clear();
            }

            RestStore.Put(entry.Id, res.Collection);
            int secrets = 0;
            foreach (var kv in res.Secrets)
                if (!string.IsNullOrEmpty(kv.Value)) { CredentialStore.TrySave(kv.Key, "", kv.Value); secrets++; }
            // Sekret auth CAŁEJ kolekcji — cel liczony z Id nowego wpisu (importer go nie znał).
            if (!string.IsNullOrEmpty(res.CollectionSecret)
                && CredentialStore.TrySave("RdpManager:restcoll:" + entry.Id, "", res.CollectionSecret)) secrets++;

            PersistServers();
            RenderTree(SearchBox.Text);
            SetStatus(string.Format(L("S.st.imported"), 1), StatusKind.Ok);

            MessageBox.Show(
                string.Format(L("S.msg.postman.done"), res.RequestCount, res.Collection.Folders.Count)
                + (secrets > 0 ? "\n" + string.Format(L("S.msg.import.withpass"), secrets) : ""),
                title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // Rozpoznaje eksport ŚRODOWISKA Postmana (obiekt z „values", bez „item") i importuje go do
        // globalnego EnvironmentStore. true = plik obsłużony (wołający nie próbuje parsera kolekcji).
        private bool TryImportPostmanEnvironment(string text)
        {
            if (!Core.PostmanImport.LooksLikeEnvironment(text)) return false;   // nie env → parser kolekcji

            var env = Core.PostmanImport.ParseEnvironment(text, out var blanked);
            var envs = EnvironmentStore.Load();
            // Unikalna nazwa (jak w RestEnvWindow) — import dwa razy nie tworzy dwóch „Production".
            string baseName = string.IsNullOrWhiteSpace(env.Name) ? L("S.rest.env.newenv") : env.Name.Trim();
            var names = new HashSet<string>(envs.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
            env.Name = baseName;
            for (int i = 2; names.Contains(env.Name); i++) env.Name = baseName + " " + i;
            envs.Add(env);
            EnvironmentStore.Save(envs);
            // Uaktywnij zaimportowane środowisko globalnie — {{zmienne}} działają od razu, bez ręcznego wyboru.
            EnvironmentStore.SetActiveId(env.Id);
            RefreshOpenRestConsoles();
            if (_restMode) RefreshRestEnvChip();

            string msg = string.Format(L("S.rest.env.imported"), env.Name);
            if (blanked.Count > 0)
                msg += "\n\n" + string.Format(L("S.rest.env.import.secretswarn"), string.Join(", ", blanked));
            MessageBox.Show(msg, L("S.rest.env.importtitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }

        // Wspólny przebieg importu z innego menedżera: plik → parser → dedup po host:port → zapis.
        // Hasła nie są przenoszone (mRemoteNG/RDCMan szyfrują je własnymi kluczami).
        private void ImportExternal(string title, string filter, Func<string, ExternalImport.Result> parse, string initialDir = null)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter };
            if (!string.IsNullOrEmpty(initialDir) && System.IO.Directory.Exists(initialDir)) dlg.InitialDirectory = initialDir;
            if (dlg.ShowDialog(this) != true) return;

            ExternalImport.Result result;
            try { result = parse(System.IO.File.ReadAllText(dlg.FileName)); }
            catch (Exception ex)
            {
                MessageBox.Show(
                    string.Format(L("S.msg.importrdp.fail"), System.IO.Path.GetFileName(dlg.FileName)) + "\n" + ex.Message,
                    title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var existing = new HashSet<string>(
                _vm.Servers.Select(s => (s.Host ?? "") + ":" + s.Port), StringComparer.OrdinalIgnoreCase);
            int added = 0, skipped = 0, withPass = 0;
            foreach (var srv in result.Servers)
            {
                if (!existing.Add(srv.Host + ":" + srv.Port)) { skipped++; continue; }
                _vm.Add(srv);
                added++;
                if (result.Passwords.TryGetValue(srv.Id, out var pw) && !string.IsNullOrEmpty(pw))
                {
                    srv.SavePassword = true;
                    SaveCredential(srv, pw);   // hasło → Credential Manager (DPAPI), nigdy do JSON
                    withPass++;
                }
            }

            if (added > 0)
            {
                PersistServers();
                RenderTree(SearchBox.Text);
                CheckReachabilityAsync();
                SetStatus(string.Format(L("S.st.imported"), added), StatusKind.Ok);
            }

            MessageBox.Show(
                string.Format(L("S.st.imported"), added)
                + (skipped > 0 ? "\n" + string.Format(L("S.msg.mstsc.skipped"), skipped) : "")
                + (result.UnsupportedProtocol > 0
                    ? "\n" + string.Format(L("S.msg.import.unsupported"), result.UnsupportedProtocol) : "")
                + "\n\n" + (withPass > 0 ? string.Format(L("S.msg.import.withpass"), withPass) : L("S.msg.import.nopass")),
                title, MessageBoxButton.OK,
                added > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        private void ImportRdp_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = L("S.dlg.importrdp.title"),
                Filter = L("S.dlg.rdp.filterAll"),
                Multiselect = true
            };
            if (dlg.ShowDialog(this) != true) return;
            ImportRdpFiles(dlg.FileNames);
        }

        // Import plików .rdp — wspólne dla menu importu i upuszczenia na drzewo (drag&drop z Eksploratora).
        internal void ImportRdpFiles(IEnumerable<string> paths)
        {
            if (paths == null) return;
            int imported = 0;
            foreach (var path in paths)
            {
                try
                {
                    var server = RdpFile.Parse(System.IO.File.ReadAllText(path));
                    server.Group = L("S.group.imported");
                    if (string.IsNullOrWhiteSpace(server.Name))
                        server.Name = System.IO.Path.GetFileNameWithoutExtension(path);
                    server.Status = ServerStatus.Offline;
                    _vm.Add(server);
                    imported++;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(string.Format(L("S.msg.importrdp.fail"), System.IO.Path.GetFileName(path)) + "\n" + ex.Message,
                        L("S.msg.importrdp.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

            if (imported > 0)
            {
                PersistServers();
                RenderTree(SearchBox.Text);
                CheckReachabilityAsync();
                SetStatus(string.Format(L("S.st.imported"), imported), StatusKind.Ok);
            }
        }


        // Zaciąga historię połączeń wbudowanego klienta RDP (mstsc) z rejestru: host (+ port) i ostatni login.
        // mstsc nie ma eksportu zbiorczego, więc to jest „jedno kliknięcie = wszystkie znane połączenia".
        private void ImportMstsc_Click(object sender, RoutedEventArgs e)
        {
            List<MstscEntry> entries;
            try { entries = MstscHistory.Read(); }
            catch (Exception ex)
            {
                MessageBox.Show(L("S.msg.mstsc.readfail") + "\n" + ex.Message,
                    L("S.msg.mstsc.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (entries.Count == 0)
            {
                MessageBox.Show(L("S.msg.mstsc.none"),
                    L("S.msg.mstsc.title"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Dedup po host:port — nie duplikujemy już dodanych serwerów.
            var existing = new HashSet<string>(
                _vm.Servers.Select(s => (s.Host ?? "") + ":" + s.Port), StringComparer.OrdinalIgnoreCase);

            int added = 0, skipped = 0;
            foreach (var en in entries)
            {
                var (host, port) = RdpUtils.SplitHostPort(en.Address, _settings.DefaultPort);
                if (string.IsNullOrWhiteSpace(host)) continue;
                if (!existing.Add(host + ":" + port)) { skipped++; continue; }

                // Rozdziel ewentualne „DOMENA\user" na domenę i login (Waypoint trzyma je osobno).
                string domain = "", user = en.Username ?? "";
                int bs = user.IndexOf('\\');
                if (bs > 0) { domain = user.Substring(0, bs); user = user.Substring(bs + 1); }

                var srv = new ServerInfo
                {
                    Name = host,
                    Host = host,
                    Port = port,
                    Username = user,
                    Domain = domain,
                    Group = L("S.group.imported.mstsc"),
                    Status = ServerStatus.Offline
                };
                srv.Initials = RdpUtils.MakeInitials(srv.Name);
                _vm.Add(srv);
                added++;
            }

            if (added > 0)
            {
                PersistServers();
                RenderTree(SearchBox.Text);
                CheckReachabilityAsync();
                SetStatus(string.Format(L("S.st.importedMstsc"), added), StatusKind.Ok);
            }

            MessageBox.Show(
                string.Format(L("S.msg.mstsc.summary"), added) +
                (skipped > 0 ? "\n" + string.Format(L("S.msg.mstsc.skipped"), skipped) : "") +
                "\n\n" + L("S.msg.mstsc.nopass"),
                L("S.msg.mstsc.title"), MessageBoxButton.OK,
                added > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        internal void ExportRdp(ServerInfo server)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = L("S.dlg.exportrdp.title"),
                Filter = L("S.dlg.rdp.filter"),
                FileName = MakeSafeFileName(server.Name ?? server.Host ?? "serwer") + ".rdp"
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                System.IO.File.WriteAllText(dlg.FileName, RdpFile.Serialize(server));
                SetStatus(string.Format(L("S.st.exported"), System.IO.Path.GetFileName(dlg.FileName)), StatusKind.Ok);
            }
            catch (Exception ex)
            {
                MessageBox.Show(L("S.msg.exportrdp.fail") + "\n" + ex.Message,
                    L("S.msg.exportrdp.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static string MakeSafeFileName(string name)
        {
            foreach (var c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "serwer" : name;
        }

        // Duplikuje serwer: kopia wszystkich ustawień (nowy Id → osobny wpis w sejfie), nazwa z „(kopia)",
        // od razu otwiera edytor — szybkie dodanie przez skopiowanie i zmianę jednej rzeczy.
        internal void DuplicateServer(ServerInfo src)
        {
            var copy = new ServerInfo   // nowy Id z konstruktora (osobny CredTarget)
            {
                Name = ((src.Name ?? "").Trim() + " " + L("S.copy.suffix")).Trim(),
                Host = src.Host, Port = src.Port, Username = src.Username, Domain = src.Domain,
                UseWindowsAccount = src.UseWindowsAccount, Group = src.Group, Initials = src.Initials,
                AvatarColor = src.AvatarColor,
                Protocol = src.Protocol, PrivateKeyPath = src.PrivateKeyPath,
                Tunnels = new List<string>(src.Tunnels ?? new List<string>()),
                RedirectClipboard = src.RedirectClipboard, RedirectDrives = src.RedirectDrives,
                RedirectPrinters = src.RedirectPrinters, AudioMode = src.AudioMode,
                AuthenticationLevel = src.AuthenticationLevel, UseAllMonitors = src.UseAllMonitors,
                AdminSession = src.AdminSession, MacAddress = src.MacAddress,
                RemoteAppProgram = src.RemoteAppProgram, RemoteAppArgs = src.RemoteAppArgs,
                GatewayHostname = src.GatewayHostname, GatewayUsageMethod = src.GatewayUsageMethod,
                SavePassword = src.SavePassword, Status = ServerStatus.Offline
            };

            string pw = "";
            if (src.SavePassword) CredentialStore.TryRead(src.CredTarget, out pw);

            var dlg = new ServerEditWindow(copy, pw, _credProfiles) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            _vm.Add(copy);
            if (copy.Protocol == RemoteProtocol.Rest) DuplicateRestData(src.Id, copy.Id);   // skopiuj też drzewo kolekcji
            PersistServers();
            SaveCredential(copy, dlg.EnteredPassword);
            RenderTree(SearchBox.Text);
            if (_restMode) BuildRestModule();   // duplikat kolekcji ma się pojawić w module
            CheckReachabilityAsync();
        }

        // Kopiuje dane kolekcji REST (rest.json) na nowy wpis: świeże Id żądań/folderów + przeniesione sekrety auth
        // (żądania, foldery — auth dziedziczone — i kolekcja jako całość, jeśli mają własne uwierzytelnianie).
        private void DuplicateRestData(string srcId, string dstId)
        {
            var copy = RestStore.DeepCopy(RestStore.For(srcId), out var reqMap, out var folderMap);
            foreach (var kv in reqMap)
                if (CredentialStore.TryRead("RdpManager:rest:" + kv.Key, out var sec) && !string.IsNullOrEmpty(sec))
                    CredentialStore.TrySave("RdpManager:rest:" + kv.Value, "", sec);
            foreach (var kv in folderMap)
                if (CredentialStore.TryRead("RdpManager:restfolder:" + kv.Key, out var sec) && !string.IsNullOrEmpty(sec))
                    CredentialStore.TrySave("RdpManager:restfolder:" + kv.Value, "", sec);
            if (CredentialStore.TryRead("RdpManager:restcoll:" + srcId, out var collSec) && !string.IsNullOrEmpty(collSec))
                CredentialStore.TrySave("RdpManager:restcoll:" + dstId, "", collSec);
            RestStore.Put(dstId, copy);
        }

        internal void EditServer(ServerInfo server)
        {
            string current = "";
            if (server.SavePassword) CredentialStore.TryRead(server.CredTarget, out current);

            var dlg = new ServerEditWindow(server, current, _credProfiles) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                PersistServers();
                SaveCredential(server, dlg.EnteredPassword);
                RenderTree(SearchBox.Text);
                if (_restMode) BuildRestModule();   // nazwa kolekcji mogła się zmienić
                CheckReachabilityAsync();

                // odśwież otwarte sesje tego serwera (etykiety zakładek + pasek aktywnej)
                RefreshTabTitles();
                if (_active?.Server == server)
                {
                    LoadToolbar(_active);
                    UpdateToolbarMode();
                    FsName.Text = server.Name + " · " + server.Host;
                }
            }
        }

        internal void DeleteServer(ServerInfo server)
        {
            if (MessageBox.Show(string.Format(L("S.msg.delete"), server.Name), L("S.msg.delete.title"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            foreach (var open in _sessions.Where(x => x.Server == server).ToList())
                CloseSession(open);

            _vm.Remove(server);
            CredentialStore.Delete(server.CredTarget);
            CleanupRestData(server);   // wpis REST: kolekcja w rest.json + sekrety auth (inaczej sieroty)
            PersistServers();
            RenderTree(SearchBox.Text);
            if (_restMode) BuildRestModule();
            CheckReachabilityAsync();
        }

        // Usuwany wpis REST zostawiał sieroty: kolekcję w rest.json i sekrety auth żądań/folderów/kolekcji
        // w Credential Managerze (DeleteServer czyścił tylko CredTarget serwera). Sprzątamy komplet.
        private void CleanupRestData(ServerInfo server)
        {
            if (server.Protocol != RemoteProtocol.Rest) return;
            var coll = RestStore.For(server.Id);
            foreach (var r in coll.Requests) CredentialStore.Delete(r.AuthCredTarget);
            foreach (var f in coll.Folders) CredentialStore.Delete(f.AuthCredTarget);
            CredentialStore.Delete("RdpManager:restcoll:" + server.Id);
            RestStore.Remove(server.Id);
        }

        // Menu zbiorcze dla zaznaczonych serwerów (Ctrl/Shift+klik): przenieś N do grupy / usuń N.
        internal ContextMenu BuildBulkContextMenu(List<ServerInfo> servers)
        {
            var menu = new ContextMenu();
            menu.Items.Add(new MenuItem { Header = string.Format(L("S.m.bulk.count"), servers.Count), IsEnabled = false });
            menu.Items.Add(new Separator());

            var move = new MenuItem { Header = L("S.m.bulk.move") };
            foreach (var g in ExistingGroupNames())
            {
                string target = g;
                var mi = new MenuItem { Header = g };
                mi.Click += (s, e) => MoveServersToGroup(servers, target);
                move.Items.Add(mi);
            }
            if (move.Items.Count > 0) move.Items.Add(new Separator());
            var newGrp = new MenuItem { Header = L("S.m.bulk.move.new") };
            newGrp.Click += (s, e) =>
            {
                var dlg = new InputDialog(L("S.prompt.newgroup.title"), L("S.prompt.newgroup.label"), "") { Owner = this };
                if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Value))
                    MoveServersToGroup(servers, dlg.Value.Trim());
            };
            move.Items.Add(newGrp);
            menu.Items.Add(move);

            menu.Items.Add(new Separator());
            var del = new MenuItem { Header = string.Format(L("S.m.bulk.delete"), servers.Count) };
            del.Click += (s, e) => DeleteServers(servers);
            menu.Items.Add(del);
            return menu;
        }

        // Nazwy istniejących (niepustych) grup — cel dla „przenieś do grupy". Kolejność pojawiania się.
        private List<string> ExistingGroupNames()
        {
            var names = new List<string>();
            foreach (var s in _vm.Servers)
                if (!string.IsNullOrWhiteSpace(s.Group) && !names.Contains(s.Group))
                    names.Add(s.Group);
            return names;
        }

        private void MoveServersToGroup(List<ServerInfo> servers, string group)
        {
            foreach (var s in servers) s.Group = group;
            PersistServers();
            RenderTree(SearchBox.Text);
        }

        // Usuwa wiele serwerów naraz (JEDNO potwierdzenie). Odzwierciedla czyszczenie z DeleteServer
        // (zamknięcie otwartych sesji + skasowanie poświadczeń z Credential Managera).
        private void DeleteServers(List<ServerInfo> servers)
        {
            if (servers.Count == 0) return;
            if (MessageBox.Show(string.Format(L("S.msg.bulkdelete"), servers.Count), L("S.msg.delete.title"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            foreach (var server in servers)
            {
                foreach (var open in _sessions.Where(x => x.Server == server).ToList())
                    CloseSession(open);
                _vm.Remove(server);
                CredentialStore.Delete(server.CredTarget);
                CleanupRestData(server);
            }
            PersistServers();
            RenderTree(SearchBox.Text);
            CheckReachabilityAsync();
        }

        internal void PersistServers() => ServerRepository.Save(_vm.Servers.ToList());

        internal void SaveCredential(ServerInfo server, string password)
        {
            if (server.SavePassword && !string.IsNullOrEmpty(password))
            {
                if (!CredentialStore.TrySave(server.CredTarget, server.Username, password)) WarnCredSaveFailed();
            }
            else
                CredentialStore.Delete(server.CredTarget);   // nie zapisujemy / kasujemy stare
        }

        // Awatar serwera: własny kolor (hex) → gradient od koloru do jego ciemniejszego wariantu;
        // brak koloru → automatyczny wg grupy. Inicjały zawsze z NAZWY (nie ze starego zapisu z IP).
        internal Brush AvatarBrush(ServerInfo s)
        {
            if (s != null && !string.IsNullOrWhiteSpace(s.AvatarColor))
            {
                try
                {
                    var c = (Color)ColorConverter.ConvertFromString(s.AvatarColor);
                    var c2 = Color.FromRgb((byte)(c.R * 0.78), (byte)(c.G * 0.78), (byte)(c.B * 0.78));
                    var g = new LinearGradientBrush(c, c2, 45); g.Freeze();
                    return g;
                }
                catch { /* zły hex → fallback do grupy */ }
            }
            return AvatarBrush(s?.Group);
        }

        internal static string ServerInitials(ServerInfo s) => RdpUtils.MakeInitials(s?.Name);

        internal void CopyToClipboard(string text)
        {
            if (string.IsNullOrEmpty(text)) { SetStatus(L("S.st.copyempty"), StatusKind.Ok); return; }
            try { Clipboard.SetText(text); SetStatus(L("S.st.copied"), StatusKind.Ok); }
            catch { /* schowek chwilowo zajęty przez inny proces */ }
        }

        private static string ReadPassword(ServerInfo s)
            => CredentialStore.TryRead(s.CredTarget, out var p) ? (p ?? "") : "";

        // Jak ReadPassword, ale z EFEKTYWNEGO celu (profil poświadczeń albo własny) — do kopiowania z menu.
        internal string ReadEffPassword(ServerInfo s)
            => CredentialStore.TryRead(EffCredTarget(s), out var p) ? (p ?? "") : "";

        // ---------- Profile poświadczeń ----------
        // Serwer może wskazywać współdzielony profil (CredentialProfileId). Gdy wskazuje, login/domena/hasło
        // przy łączeniu pochodzą z PROFILU, nie z pól serwera. Poniższe „Eff*" rozwiązują to w jednym miejscu.
        private CredentialProfile ProfileFor(ServerInfo s)
            => string.IsNullOrEmpty(s?.CredentialProfileId) ? null
               : _credProfiles.FirstOrDefault(p => p.Id == s.CredentialProfileId);

        internal string EffUser(ServerInfo s)       { var p = ProfileFor(s); return p != null ? p.Username : s.Username; }
        internal string EffDomain(ServerInfo s)     { var p = ProfileFor(s); return p != null ? p.Domain   : s.Domain; }
        internal string EffCredTarget(ServerInfo s) { var p = ProfileFor(s); return p != null ? p.CredTarget : s.CredTarget; }
        internal bool   EffSavedPw(ServerInfo s)    { var p = ProfileFor(s); return p != null || s.SavePassword; }

        // Tożsamość do łączenia: kontrolka SSH czyta server.Username WEWNĘTRZNIE (auth + SFTP), więc gdy jest
        // profil, podajemy płytką kopię serwera z podmienionym loginem/domeną (transient) — bez ruszania kodu
        // uwierzytelniania w SshTerminalControl. Dla RDP ustawiamy UserName/Domain wprost (EffUser/EffDomain).
        internal ServerInfo ConnectIdentity(ServerInfo s)
        {
            var p = ProfileFor(s);
            if (p == null) return s;
            var c = s.ShallowClone();
            c.Username = p.Username;
            c.Domain = p.Domain;
            return c;
        }

        private Brush AvatarBrush(string group)
        {
            switch (group)
            {
                case "Produkcja": return Res("AvProd");
                case "Staging": return Res("AvStaging");
                case "Klienci": return Res("AvClient");
            }
            var key = group ?? "";
            if (!_avatarCache.TryGetValue(key, out var b))
            {
                int i = Math.Abs(StableHash(key)) % GroupPalette.Length;
                var c1 = (Color)ColorConverter.ConvertFromString(GroupPalette[i][0]);
                var c2 = (Color)ColorConverter.ConvertFromString(GroupPalette[i][1]);
                b = new LinearGradientBrush(c1, c2, 45);
                b.Freeze();
                _avatarCache[key] = b;
            }
            return b;
        }

        internal Brush GroupDotBrush(string group)
        {
            switch (group)
            {
                case "Produkcja": return Res("GdProd");
                case "Staging": return Res("GdStaging");
                case "Klienci": return Res("GdClient");
            }
            return AvatarBrush(group) is LinearGradientBrush g
                ? new SolidColorBrush(g.GradientStops[0].Color)
                : Res("GdClient");
        }

        private static int StableHash(string s)
        {
            int h = 17;
            foreach (char c in s) h = h * 31 + c;
            return h;
        }

        // internal: wołany też przez Services/ReachabilityService (kolor kropki statusu po sondzie).
        internal Brush StatusBrush(ServerStatus status)
        {
            switch (status)
            {
                case ServerStatus.Online: return Res("Online");
                case ServerStatus.Idle: return Res("Idle");
                default: return Res("Offline");
            }
        }

        /// <summary>Tekstowy odpowiednik statusu (dla czytników ekranu — status nie tylko kolorem).</summary>
        internal static string StatusLabel(ServerStatus status)
        {
            switch (status)
            {
                case ServerStatus.Online: return LocalizationManager.S("S.status.online");
                case ServerStatus.Idle: return LocalizationManager.S("S.status.idle");
                default: return LocalizationManager.S("S.status.offline");
            }
        }

        // ---------- Osiągalność serwerów (logika w Services/ReachabilityService — PR 2 refaktoru) ----------
        // Cienkie shimy: wołane z wielu miejsc (dodanie/edycja/usunięcie serwera, WOL/diagnoza z menu),
        // więc delegują do serwisu bez zmiany tych wywołań.

        private void CheckReachabilityAsync() => _reach?.CheckNow();

        internal void WakeServer(ServerInfo server) => _reach?.WakeServer(server);

        internal void DiagnoseServer(ServerInfo server) => _reach?.DiagnoseServer(server);

    }
}
