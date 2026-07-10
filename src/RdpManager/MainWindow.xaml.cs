using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net.Sockets;
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
        private readonly List<Session> _sessions = new List<Session>();
        private Session _active;
        // Podział ekranu (split-screen): dwie sesje RDP obok siebie. null/null = brak podziału.
        // _active wskazuje panel z fokusem (podświetlenie karty / toolbar).
        private Session _paneLeft, _paneRight;
        private Session _splitDropSession;   // sesja przeciągana nad strefę upuszczenia podziału (fallback dla Drop)

        private readonly Dictionary<ServerInfo, Border> _serverRows = new Dictionary<ServerInfo, Border>();
        // Jak wiersz odzwierciedla stan „aktywny" — różni się między stylami (Domyślny: obrys+tło,
        // Minimal: pasek koloru→akcent+tło). UpdateActiveRows woła te akcje zamiast znać elementy.
        private readonly Dictionary<ServerInfo, Action<bool>> _serverActivate = new Dictionary<ServerInfo, Action<bool>>();
        private readonly Dictionary<ServerInfo, Ellipse> _serverStatusDot = new Dictionary<ServerInfo, Ellipse>();
        // Etykieta opóźnienia (ms) w wierszu — aktualizowana na żywo po sondzie osiągalności (gdy włączone).
        private readonly Dictionary<ServerInfo, TextBlock> _serverLatency = new Dictionary<ServerInfo, TextBlock>();
        // Próbki średniego opóźnienia (ms) z kolejnych cykli sondowania — źródło wykresu „opóźnienie" na pulpicie.
        // W pamięci (bez utrwalania), przycinane do ostatnich N; resetują się przy restarcie.
        private readonly List<double> _latencySamples = new List<double>();
        // Aktywny filtr protokołu z paska chipów (null = „Wszystkie"). Stan sesyjny — po restarcie zawsze „Wszystkie",
        // żeby użytkownik nie zobaczył „znikniętych" serwerów przez zapamiętany filtr (świadome odstępstwo od §4.2).
        private RemoteProtocol? _protocolFilter;
        private readonly Dictionary<Session, Rectangle> _tabUnderline = new Dictionary<Session, Rectangle>();
        private readonly Dictionary<Session, Ellipse> _tabStatus = new Dictionary<Session, Ellipse>();
        private readonly Dictionary<Session, TextBlock> _tabName = new Dictionary<Session, TextBlock>();
        private readonly Dictionary<Session, TextBlock> _tabClose = new Dictionary<Session, TextBlock>();
        // Grupy kart (stosy jak w Vivaldi). Przynależność po Id serwera (w TabGroup.ServerIds), więc
        // grupy zapisują się do ustawień i wracają po restarcie. Runtime-lista niżej ładowana z _settings.
        private readonly List<TabGroup> _tabGroups = new List<TabGroup>();
        private readonly MainViewModel _vm = new MainViewModel();

        // internal: czytany też przez serwisy wyniesione z tej klasy (Services/UpdateService — PR 1 refaktoru).
        internal AppSettings _settings = new AppSettings();

        // Sprawdzanie osiągalności serwerów w tle (TCP na porcie RDP) -> kropki statusu.
        private DispatcherTimer _reachTimer;
        private bool _reachBusy;

        // Stabilne, odrębne kolory awatarów dla dowolnych (także własnych) grup.
        private readonly Dictionary<string, LinearGradientBrush> _avatarCache = new Dictionary<string, LinearGradientBrush>();
        private static readonly string[][] GroupPalette =
        {
            new[]{"#7C6CFB","#4F3FD1"}, new[]{"#FFB454","#D98F2E"}, new[]{"#36C4CF","#1F8B94"},
            new[]{"#3DDC97","#1F9E6B"}, new[]{"#FB6C9C","#D13F6E"}, new[]{"#6C9CFB","#3F5FD1"},
            new[]{"#C06CFB","#7A3FD1"}, new[]{"#F0C05A","#C79030"}
        };

        private WindowStyle _prevStyle;
        private WindowState _prevState;
        private ResizeMode _prevResize;
        private double _prevLeft, _prevTop, _prevWidth, _prevHeight;
        private bool _prevTopmost;
        private double _prevScale = 1.0;
        private bool _isFullscreen;
        private bool _fsPinned;   // pasek pełnoekranowy „przypięty" (bez auto-chowania)
        private double _fsBarOffset;   // przesunięcie paska od środka (przeciąganie w poziomie)

        // Drag&drop kolejności serwerów w drzewie.
        private Point _dragStartPoint;
        private ServerInfo _dragCandidate;
        private bool _didDrag;

        // Zaznaczenie wielu serwerów (Ctrl/Shift+klik) do akcji zbiorczych. Nietrwałe — czyszczone przy każdej
        // przebudowie drzewa (filtr, zwinięcie grupy, akcja zbiorcza). _visibleOrder = kolejność wierszy dla Shift.
        private readonly HashSet<ServerInfo> _multiSelect = new HashSet<ServerInfo>();
        private ServerInfo _selectAnchor;
        private readonly List<ServerInfo> _visibleOrder = new List<ServerInfo>();

        private InsertionAdorner _dropAdorner;   // linia „tu wyląduje" na krawędzi wiersza
        private Border _dropRow;                  // wiersz, do którego przypięty jest adorner

        // Współdzielone profile poświadczeń (login/domena + hasło w Credential Manager), wskazywane przez serwery.
        private List<CredentialProfile> _credProfiles = new List<CredentialProfile>();

        // Klucz sekcji „Przypięte" w AppSettings.CollapsedGroups (nie koliduje z nazwami grup użytkownika).
        private const string PinnedGroupKey = "__pinned__";

        // Skrót do lokalizowanego tekstu (dla UI budowanego w kodzie: menu, komunikaty).
        private static string L(string key) => LocalizationManager.S(key);

        /// <summary>Skrót do pędzla z zasobów motywu; null gdy brak (te same semantyki co dotychczasowy rzut).
        /// internal: używany też przez serwisy wyniesione z tej klasy (Services/UpdateService).</summary>
        internal Brush Res(string key) => TryFindResource(key) as Brush;

        // Otwarte, samodzielne okna sesji (model wielookienny).
        private readonly System.Collections.Generic.List<SessionWindow> _sessionWindows = new System.Collections.Generic.List<SessionWindow>();

        // Opóźnienie pojawienia się paska pełnoekranowego (jak w mstsc) + polling pozycji kursora.
        private DispatcherTimer _fsBarDelay;
        private DispatcherTimer _fsCursorPoll;
        private DispatcherTimer _focusPeekPoll;    // wykrywa najechanie na lewą krawędź w trybie skupienia
        private DispatcherTimer _focusPeekDelay;   // opóźnienie przytrzymania (jak pasek pełnoekranowy)
        private bool _focusPeeking;                // panel boczny chwilowo wysunięty w trybie skupienia
        private bool? _focusOverride;              // ręczne wł/wył skupienia (null = wg ustawienia); reset po un-maximize
        private Services.UpdateService _update;   // aktualizacje + karta „O aplikacji" (PR 1 refaktoru), tworzony w Window_Loaded
        private RECT _fsMonRect;   // prostokąt monitora w pikselach fizycznych (do wykrycia górnej krawędzi)

        public MainWindow()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _settings = SettingsStore.Load();
            _settings = SettingsStore.ConsumeUpdateSnapshot(_settings);   // po aktualizacji: przywróć stan sprzed update (migawka)
            ConnectionLog.Enabled = _settings.ConnectionLogEnabled;
            _probeTimeoutMs = Math.Clamp(_settings.ProbeTimeoutSeconds, 1, 60) * 1000;
            // Serwis aktualizacji dostaje wersję sprzed tego startu, ZANIM niżej nadpiszemy LastRunVersion.
            _update = new Services.UpdateService(this, _settings.LastRunVersion ?? "");
            var curVer = Services.UpdateService.CurrentVersion().ToString();
            if (_settings.LastRunVersion != curVer) { _settings.LastRunVersion = curVer; SettingsStore.Save(_settings); }
            _credProfiles = CredentialProfileRepository.Load();
            _vm.UseRecentIds(_settings.RecentIds);   // współdziel listę „ostatnich" z ustawieniami
            LoadTabGroups();                          // grupy kart z poprzedniej sesji (przypisanie po Id serwera)
            ApplyUiScale(_settings.UiScale);

            FsPopup.CustomPopupPlacementCallback = PlaceFsPopup;
            _fsBarDelay = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(Math.Clamp(_settings.FullscreenBarDelayMs, 0, 3000))
            };
            _fsBarDelay.Tick += (s, a) =>
            {
                _fsBarDelay.Stop();
                if (_isFullscreen) FsPopup.IsOpen = true;
            };

            _fsCursorPoll = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(90)
            };
            _fsCursorPoll.Tick += FsCursorPollTick;

            // Tryb skupienia: to samo opóźnienie „przytrzymania" co pasek pełnoekranowy.
            _focusPeekDelay = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(Math.Clamp(_settings.FullscreenBarDelayMs, 0, 3000)) };
            _focusPeekDelay.Tick += (s, a) => { _focusPeekDelay.Stop(); ShowFocusPeek(); };
            _focusPeekPoll = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(90) };
            _focusPeekPoll.Tick += FocusPeekPollTick;

            BuildServerTree();
            BuildEmptyRecent();     // chipy „ostatnie" w pustym stanie kanwy — od startu (UpdateCanvas odświeży później)
            WireTreeFileDrop();     // import .rdp: upuść pliki z Eksploratora na drzewo serwerów
            ApplyTabStripStyle();   // margines paska / rozmiar ikon wg stylu (Domyślny/Minimal) — zanim wejdą karty
            UpdateToolbarEnabled();
            UpdateToolbarMode();

            _reachTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(Math.Clamp(_settings.ReachabilityIntervalSec, 5, 3600))
            };
            _reachTimer.Tick += (s, a) => CheckReachabilityAsync();
            if (_settings.ReachabilityEnabled) { _reachTimer.Start(); CheckReachabilityAsync(); }

            ShowView("Sessions");
            Core.KnownHosts.Load(SettingsStore.Dir);   // wykryj/oddziel uszkodzony known_hosts.json ZANIM opróżnimy notki
            ShowHealthNotices();   // nieblokujący sygnał, jeśli przy ładowaniu zadziałała samonaprawa/kwarantanna

            InitTray();
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndHook);
            ApplyHotkey();
            // Podświetlanie ikon paska kart w trybie skupienia (patrz StartTabStripRepaintPulse): przy ruchu
            // myszy nad paskiem wymuszamy przerysowanie, bo WPF sam go w tym trybie nie maluje.
            TabStripHost.MouseMove += (_, __) => StartTabStripRepaintPulse();
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
        private void PersistOpenSessions()
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

        private string _currentView = "Dashboard";
        private bool _sidebarCollapsed;   // ręczne zwinięcie panelu bocznego (klik w aktywną ikonę nawigacji)

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
        private void ShowView(string view)
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
            if (WindowState == WindowState.Normal) _focusOverride = null;
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
        private bool IsImmersive()
        {
            return _settings != null && !_isFullscreen
                   && (_focusOverride ?? _settings.ImmersiveOnMaximize)
                   && WindowState == WindowState.Maximized
                   && _active != null
                   && SessionsView.Visibility == Visibility.Visible;
        }

        // Tryb skupienia: po zmaksymalizowaniu chowa titlebar + panel boczny — zostają tylko karty
        // + pulpit. Przywrócenie okna (un-maximize) = pełny UI. W pełnym ekranie nie działa.
        // UWAGA: widocznością SessionToolbar rządzi WYŁĄCZNIE UpdateToolbarMode (stan pusty +
        // skupienie) — tu jej nie dotykamy, żeby nie wskrzeszać paska bez aktywnej sesji.
        private void UpdateImmersive()
        {
            if (_settings == null || _isFullscreen) return;
            bool immersive = IsImmersive();
            if (!immersive) HideFocusPeek();   // wyjście ze skupienia: zwiń peek (przenosi Rail/Sidebar z powrotem)
            AppTitleBar.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;
            // Panel boczny ukryty w skupieniu — chyba że chwilowo wysunięty (wtedy żyje w FocusPeekPopup, nie tu).
            if (!_focusPeeking)
            {
                Rail.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;   // rail (ikony) zostaje — pozwala rozwinąć panel
                Sidebar.Visibility = (immersive || _sidebarCollapsed) ? Visibility.Collapsed : Visibility.Visible;
            }
            FocusControls.Visibility = immersive ? Visibility.Visible : Visibility.Collapsed;
            if (immersive) { if (_focusPeekPoll != null && !_focusPeekPoll.IsEnabled) _focusPeekPoll.Start(); }
            else { _focusPeekPoll?.Stop(); _focusPeekDelay?.Stop(); }
            UpdateToolbarMode();
            if (immersive) StartTabStripRepaintPulse();   // od razu po wejściu (mysz może już być nad paskiem)
        }

        // Obejście quirku WPF: w trybie skupienia (pasek kart pod LayoutTransform, obok airspace WindowsFormsHost)
        // zmiana tła podświetlenia ikon (IsMouseOver) jest USTAWIANA, ale WPF jej nie MALUJE — dopóki pętla renderu
        // nie zostanie „obudzona" (robił to dopiero realny resize okna). Gdy mysz jest nad paskiem kart, trzymamy
        // pętlę renderu aktywną (CompositionTarget.Rendering) i znaczymy przyciski „brudne", więc hover maluje się
        // od razu. Po zejściu myszy dogaszamy kilka klatek i odpinamy — brak stałego kosztu renderowania.
        private bool _tabPulseOn;
        private int _tabPulseCooldown;

        private void StartTabStripRepaintPulse()
        {
            if (!IsImmersive()) return;
            _tabPulseCooldown = 15;
            if (_tabPulseOn) return;
            _tabPulseOn = true;
            System.Windows.Media.CompositionTarget.Rendering += TabStripRepaintPulse;
        }

        private void TabStripRepaintPulse(object sender, EventArgs e)
        {
            if (TabStripHost.IsMouseOver && IsImmersive())
            {
                _tabPulseCooldown = 15;
                foreach (System.Windows.UIElement c in FocusControls.Children) c.InvalidateVisual();
            }
            else if (--_tabPulseCooldown <= 0)
            {
                System.Windows.Media.CompositionTarget.Rendering -= TabStripRepaintPulse;
                _tabPulseOn = false;
            }
        }

        // Przełącznik trybu skupienia (przycisk na pasku): wł/wył dla bieżącego zmaksymalizowanego okna.
        private void ToggleFocus_Click(object sender, RoutedEventArgs e)
        {
            if (IsImmersive()) { _focusOverride = false; }        // wyłącz — chrome wraca (okno zostaje zmaksymalizowane)
            else
            {
                _focusOverride = true;                             // włącz — schowaj chrome
                if (WindowState != WindowState.Maximized) WindowState = WindowState.Maximized;   // by miało efekt
            }
            UpdateImmersive();
        }

        // Lewy panel w trybie skupienia: pokaż/schowaj (wywoływane z pollingu krawędzi).
        // Rail+Sidebar są przenoszone do FocusPeekPopup (osobny HWND) — nakłada się na sesję BEZ jej
        // resize, więc RDP/WebView2 się nie renegocjuje i nie miga. Wejście = płynny slide z lewej.
        // Podłoże peeku panelu w trybie skupienia: solidny kolor zbliżony do kanwy z kryciem z ustawień
        // (FocusPeekOpacity). Panele reparentowane do peeku mają prawie przezroczyste tło, więc bez tego
        // podłoża jasna sesja przebija spod panelu (Compass — czytelność w skupieniu).
        private Brush FocusPeekBackground()
        {
            int pct = Math.Clamp(_settings.FocusPeekOpacity, 0, 100);
            byte a = (byte)Math.Round(pct * 255.0 / 100.0);
            var c = ThemeManager.IsLight
                ? System.Windows.Media.Color.FromArgb(a, 0xF2, 0xF3, 0xF5)
                : System.Windows.Media.Color.FromArgb(a, 0x0E, 0x0F, 0x14);
            return new SolidColorBrush(c);
        }

        private void ShowFocusPeek()
        {
            if (!IsImmersive() || _focusPeeking) return;
            if (GetForegroundWindow() != new WindowInteropHelper(this).Handle) return;   // tylko gdy Waypoint na wierzchu
            _focusPeeking = true;
            BodyGrid.Children.Remove(Rail);
            BodyGrid.Children.Remove(Sidebar);
            Rail.Visibility = Visibility.Visible;
            Sidebar.Visibility = Visibility.Visible;
            FocusPeekHost.Children.Add(Rail);
            FocusPeekHost.Children.Add(Sidebar);
            // Popup nie dziedziczy RootScale (osobny HWND) — nadaj mu ręcznie zoom UI, żeby peek miał tę
            // samą skalę co panel dokowany i mieścił się na ekranie (przy <100% był ucięty od dołu).
            FocusPeekScale.ScaleX = FocusPeekScale.ScaleY = RootScale.ScaleY;
            FocusPeekClip.Height = BodyGrid.ActualHeight;
            // Solidne podłoże pod prześwitującym panelem (Panel ~3%) — bez niego treść sesji przebija peek.
            FocusPeekClip.Background = FocusPeekBackground();
            FocusPeekPopup.IsOpen = true;

            var slide = new System.Windows.Media.Animation.DoubleAnimation(-280, 0,
                new Duration(TimeSpan.FromMilliseconds(160)))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            FocusPeekSlide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, slide);
        }

        private void HideFocusPeek()
        {
            if (!_focusPeeking) return;
            _focusPeeking = false;
            FocusPeekSlide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            FocusPeekSlide.X = -280;
            FocusPeekPopup.IsOpen = false;
            FocusPeekHost.Children.Remove(Rail);
            FocusPeekHost.Children.Remove(Sidebar);
            // Wróć do layoutu (Grid.Column zachowane na elementach). W skupieniu ukryte, poza — widoczne.
            BodyGrid.Children.Add(Rail);
            BodyGrid.Children.Add(Sidebar);
            Rail.Visibility = IsImmersive() ? Visibility.Collapsed : Visibility.Visible;
            Sidebar.Visibility = (IsImmersive() || _sidebarCollapsed) ? Visibility.Collapsed : Visibility.Visible;
        }

        // Polling kursora w trybie skupienia: najechanie na lewą krawędź (i przytrzymanie) wysuwa panel;
        // zjechanie na prawo od panelu go chowa. Airspace kontrolki sesji wyklucza zwykłe MouseEnter.
        private void FocusPeekPollTick(object sender, EventArgs e)
        {
            if (!IsImmersive()) { _focusPeekPoll.Stop(); _focusPeekDelay.Stop(); return; }
            if (WindowState == WindowState.Minimized || QuickSwitchPopup.IsOpen) return;

            // Wysuwaj panel TYLKO gdy Waypoint jest na pierwszym planie — inaczej najechanie na lewą krawędź
            // ekranu pokazywało listę serwerów nad inną aplikacją (np. przeglądarką), gdy okno było w tle.
            if (GetForegroundWindow() != new WindowInteropHelper(this).Handle)
            {
                _focusPeekDelay.Stop();
                if (_focusPeeking) HideFocusPeek();
                return;
            }

            if (!GetCursorPos(out POINT p)) return;

            // Lewa krawędź LICZONA Z PROSTOKĄTA MONITORA (jak pasek pełnoekranowy), nie z PointToScreen(0,0):
            // zmaksymalizowane okno wystaje ~8px poza monitor, więc PointToScreen dawało ujemny lewy brzeg
            // i próg poza ekranem — trigger nigdy się nie odpalał.
            IntPtr mon = MonitorFromWindow(new WindowInteropHelper(this).Handle, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(mon, ref mi)) return;
            var r = mi.rcMonitor;

            if (!_focusPeeking)
            {
                bool withinY = p.Y >= r.top && p.Y < r.bottom;
                if (withinY && p.X <= r.left + 3) { if (!_focusPeekDelay.IsEnabled) _focusPeekDelay.Start(); }
                else if (_focusPeekDelay.IsEnabled) _focusPeekDelay.Stop();
            }
            else
            {
                // PointToScreen uwzględnia DPI i zoom UI (Sidebar jest pod RootScale).
                double sbRight = Sidebar.PointToScreen(new Point(Sidebar.ActualWidth, 0)).X;
                if (p.X > sbRight + 8) HideFocusPeek();
            }
        }

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

        private bool _restMode;

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

        private void ToggleRestNode(string id)
        {
            if (!_restExpanded.Remove(id)) _restExpanded.Add(id);
            BuildRestModule();
        }

        // Każdy wpis REST = kolekcja → foldery → żądania (dane z RestStore.For per serwer).
        // Klik kolekcji/folderu = zwiń/rozwiń; klik żądania = otwórz. To JEDYNE drzewo kolekcji
        // (konsola nie ma już swojego panelu) — więc PPM obsługuje pełną strukturę: na kolekcji
        // wariant REST z BuildServerContextMenu (+ nowe żądanie/folder), na folderze i żądaniu
        // menu z BuildRestFolderMenu/BuildRestReqMenu (nowe/zmień nazwę/usuń).
        private void BuildRestModule()
        {
            RestModuleTree.Children.Clear();
            var rest = _vm.Servers.Where(s => s.Protocol == RemoteProtocol.Rest)
                                  .OrderByDescending(s => s.Pinned)   // przypięte kolekcje na górze
                                  .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
            int total = 0;
            foreach (var srv in rest)
            {
                var s = srv;
                var coll = RestStore.For(srv.Id);
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
                total += coll.Requests.Count;
            }
            RestModuleCount.Text = total.ToString();
            if (rest.Count == 0)
                RestModuleTree.Children.Add(new TextBlock { Text = L("S.rest.module.empty"), Foreground = Res("TextTer"),
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
        private void AddRestRequestCmd(ServerInfo srv, string folderId)
        {
            _restExpanded.Add(srv.Id);
            if (!string.IsNullOrEmpty(folderId)) _restExpanded.Add(folderId);
            EnsureRestConsole(srv)?.NewRequest(folderId);
        }

        private void AddRestFolderCmd(ServerInfo srv, string parentId)
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
            _probeTimeoutMs = Math.Clamp(_settings.ProbeTimeoutSeconds, 1, 60) * 1000;
            ApplyUiScale(_settings.UiScale);
            // Clampy także tutaj — ustawienia mogą przyjść z importu profilu (plik zewnętrzny).
            _fsBarDelay.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_settings.FullscreenBarDelayMs, 0, 3000));
            if (_focusPeekDelay != null) _focusPeekDelay.Interval = _fsBarDelay.Interval;
            _reachTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(_settings.ReachabilityIntervalSec, 5, 3600));
            if (_settings.ReachabilityEnabled)
            {
                if (!_reachTimer.IsEnabled) _reachTimer.Start();
                CheckReachabilityAsync();
            }
            else
            {
                _reachTimer.Stop();
            }

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

        private void RecordRecent(ServerInfo server)
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
            if (_latencySamples.Count < 2) return ChartHint(L("S.dash.nolatency"));
            var samples = _latencySamples.ToArray();
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

        private static string ProtocolLabel(RemoteProtocol p) => p switch
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
        private static string ProtocolShort(RemoteProtocol p) => p switch
        {
            RemoteProtocol.Telnet => "TEL",
            RemoteProtocol.Serial => "COM",
            RemoteProtocol.Http => "WWW",
            _ => ProtocolLabel(p)
        };

        // Kolor etykiety protokołu (Compass §2). VNC dzieli kolor z RDP (pulpit zdalny), FTP z SFTP
        // (transfer plików), Serial z Telnet (terminal) — brak osobnych kluczy dla tych trzech.
        private Brush ProtocolBrush(RemoteProtocol p) => p switch
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

        // Pasek chipów filtra protokołów nad listą (Compass §4.2). Chipy budowane dynamicznie z protokołów
        // faktycznie obecnych na liście — bez martwych chipów. Ukryty, gdy < 2 różne protokoły (nie ma czego
        // filtrować). Pojedynczy wybór; „Wszystkie" = brak filtra. Stan sesyjny (nie zapisywany).
        private void BuildProtocolFilter()
        {
            ProtoFilterBar.Children.Clear();
            // Bez REST — kolekcje mają własny moduł w railu, chip byłby martwy (lista ich nie pokazuje).
            var protos = _vm.Servers.Select(s => s.Protocol).Where(p => p != RemoteProtocol.Rest)
                                    .Distinct().OrderBy(p => (int)p).ToList();

            // Filtr wskazujący nieobecny już protokół (usunięto ostatni taki serwer) → reset do „Wszystkie".
            if (_protocolFilter.HasValue && !protos.Contains(_protocolFilter.Value)) _protocolFilter = null;

            if (protos.Count < 2) { ProtoFilterBar.Visibility = Visibility.Collapsed; return; }
            ProtoFilterBar.Visibility = Visibility.Visible;

            ProtoFilterBar.Children.Add(MakeProtocolChip(L("S.proto.filter.all"), null, Res("TextSec")));
            foreach (var p in protos)
                ProtoFilterBar.Children.Add(MakeProtocolChip(ProtocolShort(p), p, ProtocolBrush(p)));
        }

        private FrameworkElement MakeProtocolChip(string text, RemoteProtocol? proto, Brush accent)
        {
            bool selected = _protocolFilter == proto || (proto == null && _protocolFilter == null);
            var chip = new Border
            {
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(9, 3, 9, 3),
                Margin = new Thickness(0, 0, 5, 5),
                Background = selected ? Res("AccentSoft") : Brushes.Transparent,
                BorderBrush = selected ? Res("Accent") : Res("Border"),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = text,
                    Foreground = selected ? Res("TextPrim") : accent,
                    FontSize = (double)TryFindResource("FontCaption"),
                    FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal
                }
            };
            chip.MouseLeftButtonUp += (s, e) => { _protocolFilter = proto; RenderTree(SearchBox.Text); };
            return chip;
        }

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

        // Pasek u samej góry: domyślnie wyśrodkowany, ale można go przeciągać w poziomie (_fsBarOffset).
        private CustomPopupPlacement[] PlaceFsPopup(Size popupSize, Size targetSize, Point offset)
        {
            double free = Math.Max(0, targetSize.Width - popupSize.Width);
            double x = free / 2.0 + _fsBarOffset;
            if (x < 0) x = 0;
            if (x > free) x = free;
            return new[] { new CustomPopupPlacement(new Point(x, 0), PopupPrimaryAxis.None) };
        }

        private void FsBarThumb_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            _fsBarOffset += e.HorizontalChange;
            // Wymuś ponowne przeliczenie pozycji popupu (bez zmiany netto offsetu).
            if (FsPopup.IsOpen) { FsPopup.HorizontalOffset += 0.01; FsPopup.HorizontalOffset -= 0.01; }
        }

        // ---------- Drzewo serwerów ----------

        private void BuildServerTree()
        {
            _vm.LoadServers(ServerRepository.Load());
            RenderTree();
        }

        private void RenderTree(string filter = null)
        {
            string filterDisplay = (filter ?? "").Trim();
            filter = filterDisplay.ToLowerInvariant();
            ServerTree.Children.Clear();
            _serverRows.Clear();
            _serverActivate.Clear();
            _serverStatusDot.Clear();
            _serverLatency.Clear();
            _multiSelect.Clear();
            _selectAnchor = null;
            _visibleOrder.Clear();

            // Pasek chipów filtra protokołów nad listą (Compass §4.2); też weryfikuje _protocolFilter
            // względem obecnych serwerów (gdy protokół zniknął — reset do „Wszystkie").
            BuildProtocolFilter();

            // Dostępność: strzałki i Tab przenoszą fokus między wierszami serwerów.
            System.Windows.Input.KeyboardNavigation.SetDirectionalNavigation(ServerTree, System.Windows.Input.KeyboardNavigationMode.Continue);
            System.Windows.Input.KeyboardNavigation.SetTabNavigation(ServerTree, System.Windows.Input.KeyboardNavigationMode.Continue);

            // Sekcja „Przypięte" na górze — ulubione serwery (kolejność z listy), niezależnie od grupy.
            // Wpisy REST NIE żyją na liście serwerów — mają własny moduł w railu (przypięcie sortuje je TAM).
            var pinned = _vm.Servers.Where(s => s.Pinned && s.Protocol != RemoteProtocol.Rest
                && RdpUtils.MatchesFilter(s, filter) && RdpUtils.MatchesProtocol(s, _protocolFilter)).ToList();
            if (pinned.Count > 0)
            {
                bool pinCollapsed = _settings.CollapsedGroups.Contains(PinnedGroupKey);
                ServerTree.Children.Add(BuildGroupHeader(PinnedGroupKey, pinned.Count, pinCollapsed, isPinned: true));
                if (!pinCollapsed)
                    foreach (var s in pinned) { ServerTree.Children.Add(BuildServerRow(s)); _visibleOrder.Add(s); }
            }

            // Zwykłe grupy (bez przypiętych).
            var order = new List<string>();
            var byGroup = new Dictionary<string, List<ServerInfo>>();
            foreach (var s in _vm.Servers)
            {
                if (s.Protocol == RemoteProtocol.Rest) continue;   // kolekcje REST → moduł w railu, nie lista
                if (s.Pinned) continue;
                if (!RdpUtils.MatchesFilter(s, filter)) continue;
                if (!RdpUtils.MatchesProtocol(s, _protocolFilter)) continue;
                var g = string.IsNullOrWhiteSpace(s.Group) ? L("S.group.serversdefault") : s.Group;
                if (!byGroup.ContainsKey(g)) { order.Add(g); byGroup[g] = new List<ServerInfo>(); }
                byGroup[g].Add(s);
            }
            foreach (var g in order)
            {
                bool collapsed = _settings.CollapsedGroups.Contains(g);
                ServerTree.Children.Add(BuildGroupHeader(g, byGroup[g].Count, collapsed, isPinned: false));
                if (!collapsed)
                    foreach (var s in byGroup[g])
                    { ServerTree.Children.Add(BuildServerRow(s)); _visibleOrder.Add(s); }
            }
            UpdateActiveRows();

            // Pusty stan drzewa (3.1 z przeglądu): rozróżnij "w ogóle brak serwerów" od "filtr nic nie znalazł" —
            // liczymy dopasowania, nie _visibleOrder (te pomija zwinięte grupy, więc byłoby mylące gdy wszystko zwinięte).
            int matchCount = pinned.Count + byGroup.Values.Sum(l => l.Count);
            if (_vm.Servers.Count == 0) { TreeEmptyHint.Text = L("S.tree.empty"); TreeEmptyHint.Visibility = Visibility.Visible; }
            else if (matchCount == 0)
            {
                // Puste dopasowanie może wynikać z tekstu w polu szukania i/lub z filtra protokołu — pokaż
                // to, co faktycznie zawęża (sam „{0}" byłby pusty, gdy filtruje tylko chip protokołu).
                string needle = filterDisplay.Length > 0 ? filterDisplay
                              : _protocolFilter.HasValue ? ProtocolLabel(_protocolFilter.Value) : "";
                TreeEmptyHint.Text = string.Format(L("S.tree.noresults"), needle);
                TreeEmptyHint.Visibility = Visibility.Visible;
            }
            else TreeEmptyHint.Visibility = Visibility.Collapsed;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RenderTree(SearchBox.Text);

        private FrameworkElement BuildGroupHeader(string name, int count, bool collapsed, bool isPinned)
        {
            var row = new Border
            {
                // Minimal: ciaśniejszy padding niż domyślny (lżejsze nagłówki grup i sekcja przypiętych).
                Padding = IsMinimalList ? new Thickness(6, 5, 6, 2) : new Thickness(6, 10, 6, 4),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };

            // Strzałka zwijania (▸ zwinięte / ▾ rozwinięte).
            sp.Children.Add(new TextBlock
            {
                Text = collapsed ? "▸" : "▾",
                Foreground = Res("TextTer"), FontSize = 10, Width = 12,
                VerticalAlignment = VerticalAlignment.Center
            });

            if (isPinned)
                sp.Children.Add(new TextBlock
                {
                    Text = "★", Foreground = Res("Idle"), FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
                });
            else
                sp.Children.Add(new Ellipse
                {
                    Width = 6, Height = 6, Fill = GroupDotBrush(name),
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
                });

            sp.Children.Add(new TextBlock
            {
                Text = (isPinned ? L("S.group.pinned") : name.ToUpperInvariant()) + "  ·  " + count,
                Foreground = Res("TextSec"),
                FontSize = 13, FontWeight = FontWeights.Bold,   // grupa nadrzędna — wyraźniej niż wiersze w środku
                VerticalAlignment = VerticalAlignment.Center
            });

            row.Child = sp;

            string key = isPinned ? PinnedGroupKey : name;
            row.MouseLeftButtonUp += (s, e) => ToggleGroupCollapse(key);

            if (!isPinned)
            {
                var menu = new ContextMenu();
                var rename = new MenuItem { Header = L("S.m.renamegroup") };
                rename.Click += (s, e) => RenameGroup(name);
                menu.Items.Add(rename);
                row.ContextMenu = menu;
            }
            return row;
        }

        // Zwija/rozwija grupę i zapamiętuje stan w ustawieniach.
        private void ToggleGroupCollapse(string key)
        {
            if (!_settings.CollapsedGroups.Remove(key)) _settings.CollapsedGroups.Add(key);
            SettingsStore.Save(_settings);
            RenderTree(SearchBox.Text);
        }

        // Zmienia nazwę grupy dla WSZYSTKICH jej serwerów naraz (bez wchodzenia w każdy z osobna).
        private void RenameGroup(string oldName)
        {
            var dlg = new InputDialog(L("S.prompt.renamegroup.title"),
                string.Format(L("S.prompt.renamegroup.label"), oldName), oldName) { Owner = this };
            if (dlg.ShowDialog() != true) return;

            string newName = dlg.Value;
            if (string.IsNullOrWhiteSpace(newName) || newName == oldName) return;

            foreach (var s in _vm.Servers)
                if ((string.IsNullOrWhiteSpace(s.Group) ? L("S.group.serversdefault") : s.Group) == oldName)
                    s.Group = newName;

            // Przenieś stan zwinięcia na nową nazwę.
            if (_settings.CollapsedGroups.Remove(oldName) && !_settings.CollapsedGroups.Contains(newName))
                _settings.CollapsedGroups.Add(newName);
            SettingsStore.Save(_settings);

            PersistServers();
            RenderTree(SearchBox.Text);
        }

        // Przypina/odpina serwer (sekcja „Przypięte" na górze).
        private void TogglePin(ServerInfo server)
        {
            server.Pinned = !server.Pinned;
            PersistServers();
            RenderTree(SearchBox.Text);
            if (_restMode) BuildRestModule();   // przypięcie sortuje kolekcje w module
        }

        private bool IsMinimalList => _settings != null && _settings.ListStyle == "Minimal";

        private FrameworkElement BuildServerRow(ServerInfo server)
            => IsMinimalList ? BuildServerRowMinimal(server) : BuildServerRowDefault(server);

        // Wariant DOMYŚLNY (bez zmian): awatar 22px + dwie linie (nazwa/host) + kropka statusu po prawej.
        private FrameworkElement BuildServerRowDefault(ServerInfo server)
        {
            var row = new Border
            {
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(18, 1, 0, 1),   // wcięcie = element należy do grupy powyżej (Compass §4.3)
                Padding = new Thickness(6, 7, 8, 7),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Tag = server
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var accent = new Rectangle
            {
                Width = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = Res("Accent"),
                VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(0, 2, 0, 2),
                Visibility = Visibility.Collapsed
            };
            Grid.SetColumn(accent, 0);
            grid.Children.Add(accent);

            var avatar = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(6),
                Background = AvatarBrush(server), Margin = new Thickness(8, 0, 0, 0),
                Child = new TextBlock
                {
                    Text = ServerInitials(server), Foreground = Brushes.White, FontSize = 9.5, FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            };
            Grid.SetColumn(avatar, 1);
            grid.Children.Add(avatar);

            // Sam adres (DisplayHost) zdjęty z wiersza — nie mieścił się z nazwą; jest w tooltipie (WireServerRow).
            var meta = new StackPanel { Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            meta.Children.Add(new TextBlock { Text = server.Name, Foreground = Res("TextPrim"), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(meta, 2);
            grid.Children.Add(meta);

            var status = new Ellipse
            {
                Width = 7, Height = 7, Fill = StatusBrush(server.Status),
                VerticalAlignment = VerticalAlignment.Center
            };
            _serverStatusDot[server] = status;

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(BuildProtocolTag(server));
            AddLatencyLabel(right, server);
            if (server.Pinned)
                right.Children.Add(new TextBlock
                {
                    Text = "★", Foreground = Res("Idle"), FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
                });
            right.Children.Add(status);
            Grid.SetColumn(right, 3);
            grid.Children.Add(right);

            row.Child = grid;

            _serverActivate[server] = active =>
            {
                row.Background = active ? Res("AccentSoft") : Brushes.Transparent;
                accent.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            };
            WireServerRow(row, server);
            return row;
        }

        // Wariant MINIMALISTYCZNY: jednowierszowy, bez awatara — pasek koloru + kropka statusu + nazwa/host.
        private FrameworkElement BuildServerRowMinimal(ServerInfo server)
        {
            var row = new Border
            {
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(18, 1, 0, 1),   // wcięcie = element należy do grupy powyżej (Compass §4.3)
                Padding = new Thickness(0, 3, 8, 3),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Tag = server
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });                    // pasek koloru
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                      // kropka
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // nazwa
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                      // host

            // Pasek koloru przy lewej krawędzi = tożsamość serwera; zmienia się na akcent, gdy zaznaczony.
            var serverColor = AvatarBrush(server);
            var bar = new Rectangle
            {
                Width = 3, RadiusX = 2, RadiusY = 2, Fill = serverColor,
                VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(0, 3, 0, 3)
            };
            Grid.SetColumn(bar, 0);
            grid.Children.Add(bar);

            var status = new Ellipse
            {
                Width = 7, Height = 7, Fill = StatusBrush(server.Status),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(11, 0, 0, 0)
            };
            _serverStatusDot[server] = status;
            Grid.SetColumn(status, 1);
            grid.Children.Add(status);

            var name = new TextBlock
            {
                Text = server.Name, Foreground = Res("TextPrim"), FontSize = 12, FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(name, 2);
            grid.Children.Add(name);

            // Po prawej: znacznik protokołu (+ opcjonalne opóźnienie / gwiazdka). Adres (DisplayHost) zdjęty
            // z wiersza — nazwa nie mieściła się z adresem; adres jest w tooltipie (WireServerRow).
            var rightPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
            };
            rightPanel.Children.Add(BuildProtocolTag(server));
            AddLatencyLabel(rightPanel, server);
            if (server.Pinned)
                rightPanel.Children.Add(new TextBlock
                {
                    Text = "★", Foreground = Res("Idle"), FontSize = 9,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
                });
            Grid.SetColumn(rightPanel, 3);
            grid.Children.Add(rightPanel);

            row.Child = grid;

            _serverActivate[server] = active =>
            {
                row.Background = active ? Res("AccentSoft") : Brushes.Transparent;
                bar.Fill = active ? Res("Accent") : serverColor;
            };
            WireServerRow(row, server);
            return row;
        }

        // Kolorowa etykieta protokołu (mono) po prawej stronie wiersza — świadoma protokołów lista (Compass §3).
        private TextBlock BuildProtocolTag(ServerInfo server) => new TextBlock
        {
            Text = ProtocolShort(server.Protocol),
            Foreground = ProtocolBrush(server.Protocol),
            FontSize = (double)TryFindResource("FontCaption"),
            FontFamily = (FontFamily)TryFindResource("Mono"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };

        // Etykieta opóźnienia (ms) — tylko gdy włączone „Pokazuj opóźnienia"; rejestrowana do aktualizacji na żywo.
        private void AddLatencyLabel(Panel host, ServerInfo server)
        {
            if (_settings == null || !_settings.ShowLatency) return;
            var lat = new TextBlock
            {
                Text = RdpUtils.FormatLatency(server.LatencyMs),
                Foreground = Res("TextTer"),
                FontSize = (double)TryFindResource("FontCaption"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            _serverLatency[server] = lat;
            host.Children.Add(lat);
        }

        // Wspólne zachowanie wiersza (hover / przeciąganie-zmiana kolejności / klik / menu) — jednakowe w obu stylach.
        private void WireServerRow(Border row, ServerInfo server)
        {
            // Dostępność (z PR #21): wiersz fokusowalny (nawigacja klawiaturą), nazwa dla czytnika ekranu
            // (nazwa — host — status), a kropka statusu — swój tekst. Wspólne dla obu stylów listy.
            row.Focusable = true;
            string tagText = (server.Tags != null && server.Tags.Count > 0) ? "  #" + string.Join(" #", server.Tags) : "";
            System.Windows.Automation.AutomationProperties.SetName(row,
                server.Name + " — " + DisplayHost(server) + " — " + StatusLabel(server.Status) + tagText);
            // Adres zdjęty z wiersza (nie mieścił się z nazwą) → pokazujemy go tutaj, w tooltipie, razem
            // z tagami i notatką (jeśli są). Nazwa zawsze; adres prawie zawsze — więc tooltip jest zawsze.
            string dh = DisplayHost(server);
            string hostText = string.IsNullOrWhiteSpace(dh) ? "" : "\n" + dh;
            string tagsTip = (server.Tags != null && server.Tags.Count > 0) ? "\n#" + string.Join(" #", server.Tags) : "";
            string noteText = string.IsNullOrWhiteSpace(server.Notes) ? "" : "\n" + server.Notes.Trim();
            row.ToolTip = server.Name + hostText + tagsTip + noteText;
            if (_serverStatusDot.TryGetValue(server, out var statusDot))
                System.Windows.Automation.AutomationProperties.SetName(statusDot, StatusLabel(server.Status));

            row.MouseEnter += (s, e) => { if (_active?.Server != server) row.Background = Res("Elevated"); };
            row.MouseLeave += (s, e) => { if (_active?.Server != server && !row.IsKeyboardFocused) row.Background = RowRestBackground(server); };
            row.GotKeyboardFocus += (s, e) => { if (_active?.Server != server) row.Background = Res("Elevated"); };
            row.LostKeyboardFocus += (s, e) => { if (_active?.Server != server) row.Background = RowRestBackground(server); };
            row.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter || e.Key == Key.Space) { LaunchServer(server, true); e.Handled = true; }
            };

            // Drag&drop: przeciągnięcie zmienia kolejność (a upuszczenie na inną grupę przenosi do niej).
            row.AllowDrop = true;
            row.PreviewMouseLeftButtonDown += (s, e) => { _dragStartPoint = e.GetPosition(null); _dragCandidate = server; _didDrag = false; };
            row.PreviewMouseMove += (s, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate == null) return;
                var pos = e.GetPosition(null);
                if (Math.Abs(pos.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(pos.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                _didDrag = true;
                row.Opacity = 0.4;   // wizualnie „podnieś" przeciągany wiersz
                try { DragDrop.DoDragDrop(row, _dragCandidate, DragDropEffects.Move); }
                catch { }
                finally { row.Opacity = 1.0; ClearDropIndicator(); _dragCandidate = null; }
            };
            row.DragOver += (s, e) =>
            {
                if (!e.Data.GetDataPresent(typeof(ServerInfo))) return;   // pliki z Eksploratora → obsłuży ServerTree (import .rdp)
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
                var dragged = e.Data.GetData(typeof(ServerInfo)) as ServerInfo;
                if (dragged == null || dragged == server) { ClearDropIndicator(); return; }
                bool bottom = e.GetPosition(row).Y > row.ActualHeight / 2;
                ShowDropIndicator(row, bottom);
            };
            row.Drop += (s, e) =>
            {
                if (!e.Data.GetDataPresent(typeof(ServerInfo))) return;   // pliki bąbelkują do ServerTree (import .rdp)
                ClearDropIndicator();
                bool bottom = e.GetPosition(row).Y > row.ActualHeight / 2;
                ReorderServer(e.Data.GetData(typeof(ServerInfo)) as ServerInfo, server, bottom);
                e.Handled = true;
            };
            row.MouseLeftButtonUp += (s, e) =>
            {
                if (_didDrag) { _didDrag = false; return; }   // to było przeciąganie, nie klik
                var mods = Keyboard.Modifiers;
                if (mods.HasFlag(ModifierKeys.Shift) && _selectAnchor != null) { RangeSelect(server); e.Handled = true; return; }
                if (mods.HasFlag(ModifierKeys.Control) || mods.HasFlag(ModifierKeys.Shift)) { ToggleSelect(server); e.Handled = true; return; }
                ClearMultiSelect();   // zwykły klik = połącz i wyczyść zaznaczenie
                LaunchServer(server, true);
            };

            row.ContextMenu = BuildServerContextMenu(server);
            row.ContextMenuOpening += (s, e) =>
            {
                // Prawy-klik na zaznaczonym wierszu przy zaznaczeniu ≥2 → menu zbiorcze; inaczej menu pojedyncze
                // (prawy-klik poza zaznaczeniem czyści zaznaczenie i pokazuje zwykłe menu wiersza).
                if (_multiSelect.Count >= 2 && _multiSelect.Contains(server))
                    row.ContextMenu = BuildBulkContextMenu(_multiSelect.ToList());
                else
                {
                    ClearMultiSelect();
                    row.ContextMenu = BuildServerContextMenu(server);
                }
            };
            _serverRows[server] = row;
        }

        // Tło wiersza w stanie spoczynku (nie hover/focus/aktywny): zaznaczony = AccentSoft, inaczej przezroczysty.
        private Brush RowRestBackground(ServerInfo s)
            => _multiSelect.Contains(s) ? Res("AccentSoft") : Brushes.Transparent;

        // Ctrl+klik: przełącz pojedynczy wiersz w zaznaczeniu (ustaw kotwicę dla ewentualnego Shift).
        private void ToggleSelect(ServerInfo server)
        {
            if (!_multiSelect.Remove(server)) _multiSelect.Add(server);
            _selectAnchor = server;
            RefreshSelectionVisuals();
        }

        // Shift+klik: zaznacz ciągły zakres od kotwicy do wskazanego wiersza (w kolejności widocznej).
        private void RangeSelect(ServerInfo server)
        {
            int a = _visibleOrder.IndexOf(_selectAnchor), b = _visibleOrder.IndexOf(server);
            if (a < 0 || b < 0) { ToggleSelect(server); return; }
            if (a > b) { (a, b) = (b, a); }
            _multiSelect.Clear();
            for (int i = a; i <= b; i++) _multiSelect.Add(_visibleOrder[i]);
            RefreshSelectionVisuals();
        }

        private void ClearMultiSelect()
        {
            _selectAnchor = null;
            if (_multiSelect.Count == 0) return;
            _multiSelect.Clear();
            RefreshSelectionVisuals();
        }

        // Odśwież tło wierszy wg zaznaczenia. Pomijamy: aktywną sesję (maluje ją UpdateActiveRows) oraz wiersze
        // pod kursorem / z fokusem (te odświeżą własne handlery MouseLeave/LostKeyboardFocus).
        private void RefreshSelectionVisuals()
        {
            foreach (var kv in _serverRows)
            {
                if (_active?.Server == kv.Key || kv.Value.IsMouseOver || kv.Value.IsKeyboardFocused) continue;
                kv.Value.Background = _multiSelect.Contains(kv.Key) ? Res("AccentSoft") : Brushes.Transparent;
            }
        }

        private ContextMenu BuildServerContextMenu(ServerInfo server)
        {
            var menu = new ContextMenu();
            bool rdp = server.Protocol == RemoteProtocol.Rdp;
            bool rest = server.Protocol == RemoteProtocol.Rest;   // kolekcja — nie serwer: bez WoL, „Duplikuj kolekcję"
            var pinItem = new MenuItem { Header = L(server.Pinned ? "S.m.unpin" : "S.m.pin") };
            pinItem.Click += (s, e) => TogglePin(server);
            var newWinItem = new MenuItem { Header = L("S.m.newwin") };
            newWinItem.Click += (s, e) => OpenInNewWindow(server);
            var connectAsItem = new MenuItem { Header = L("S.m.connectas") };
            connectAsItem.Click += (s, e) =>
            {
                OpenServer(server);
                if (_active?.Server == server) PromptAndConnect(_active, L("S.prompt.connectas"));
            };
            var editItem = new MenuItem { Header = L("S.m.edit") };
            editItem.Click += (s, e) => EditServer(server);
            var dupItem = new MenuItem { Header = L(rest ? "S.m.dupcollection" : "S.m.dupserver") };
            dupItem.Click += (s, e) => DuplicateServer(server);

            // Kopiuj ▸ — pojedyncze pola (i login+hasło) do schowka. Hasło z Credential Managera na żądanie.
            var copyMenu = new MenuItem { Header = L("S.m.copy") };
            void AddCopy(string key, Func<string> value)
            {
                var mi = new MenuItem { Header = L(key) };
                mi.Click += (s, e) => CopyToClipboard(value());
                copyMenu.Items.Add(mi);
            }
            AddCopy("S.m.copy.name", () => server.Name);
            AddCopy("S.m.copy.host", () => server.Host);
            if (server.Protocol != RemoteProtocol.Http && server.Protocol != RemoteProtocol.Rest)
                AddCopy("S.m.copy.port", () => server.Port.ToString());   // WWW/REST: URL niesie port
            if (rdp || server.Protocol == RemoteProtocol.Ssh || server.Protocol == RemoteProtocol.Sftp || server.Protocol == RemoteProtocol.Ftp)
            {
                AddCopy("S.m.copy.user", () => EffUser(server));
                if (rdp) AddCopy("S.m.copy.domain", () => EffDomain(server));
                copyMenu.Items.Add(new Separator());
                AddCopy("S.m.copy.pass", () => ReadEffPassword(server));
                AddCopy("S.m.copy.userpass", () => EffUser(server) + "\t" + ReadEffPassword(server));
            }

            var diagItem = new MenuItem { Header = L("S.m.diag") };
            diagItem.Click += (s, e) => DiagnoseServer(server);
            var wolItem = new MenuItem
            {
                Header = L("S.m.wol"),
                IsEnabled = !string.IsNullOrWhiteSpace(server.MacAddress)   // bez MAC nie ma czego budzić
            };
            wolItem.Click += (s, e) => WakeServer(server);
            var exportItem = new MenuItem { Header = L("S.m.exportrdp") };
            exportItem.Click += (s, e) => ExportRdp(server);
            var delItem = new MenuItem { Header = L("S.m.delete") };
            delItem.Click += (s, e) => DeleteServer(server);
            // Moduł REST: klik wiersza kolekcji zwija/rozwija, więc otwarcie konsoli ma jawny wpis w menu;
            // do tego tworzenie żądań/folderów w korzeniu (foldery i żądania mają własne menu z pełną strukturą).
            if (rest)
            {
                var openItem = new MenuItem { Header = L("S.m.opencoll") };
                openItem.Click += (s, e) => LaunchServer(server, true);
                menu.Items.Add(openItem);
                var newReqItem = new MenuItem { Header = L("S.rest.newreq") };
                newReqItem.Click += (s, e) => AddRestRequestCmd(server, "");
                menu.Items.Add(newReqItem);
                var newFolderItem = new MenuItem { Header = L("S.rest.newfolder") };
                newFolderItem.Click += (s, e) => AddRestFolderCmd(server, "");
                menu.Items.Add(newFolderItem);
                menu.Items.Add(new Separator());
            }
            menu.Items.Add(pinItem);
            menu.Items.Add(new Separator());
            if (rdp) menu.Items.Add(newWinItem);       // osobne okno sesji jest RDP-owe
            if (rdp || server.Protocol == RemoteProtocol.Ssh || server.Protocol == RemoteProtocol.Sftp || server.Protocol == RemoteProtocol.Ftp) menu.Items.Add(connectAsItem);
            menu.Items.Add(editItem);
            menu.Items.Add(dupItem);
            menu.Items.Add(copyMenu);
            if (server.Protocol != RemoteProtocol.Serial && server.Protocol != RemoteProtocol.Http && server.Protocol != RemoteProtocol.Rest)
                menu.Items.Add(diagItem);   // sonda TCP — nie dla COM/URL/REST
            if (!rest) menu.Items.Add(wolItem);   // Wake-on-LAN nie dotyczy kolekcji REST
            if (rdp) menu.Items.Add(exportItem);       // .rdp ma sens tylko dla RDP
            menu.Items.Add(new Separator());
            menu.Items.Add(delItem);
            return menu;
        }

        private void UpdateActiveRows()
        {
            foreach (var kv in _serverActivate)
                kv.Value(_active != null && _active.Server == kv.Key);
        }

        /// <summary>Zmienia kolejność serwerów (drag&drop): wstawia <paramref name="dragged"/> przed albo
        /// za <paramref name="target"/> (zależnie od <paramref name="after"/> = połowa wiersza, na którą
        /// upuszczono); upuszczenie na inną grupę przenosi serwer do tej grupy.</summary>
        private void ReorderServer(ServerInfo dragged, ServerInfo target, bool after = false)
        {
            if (dragged == null || target == null || dragged == target) return;
            int from = _vm.Servers.IndexOf(dragged);
            int to = _vm.Servers.IndexOf(target);
            if (from < 0 || to < 0) return;

            dragged.Group = target.Group;   // upuszczenie na inną grupę = przeniesienie do niej

            // Docelowy indeks po usunięciu z „from": przed/za wskazanym wierszem.
            if (after && from > to) to += 1;
            else if (!after && from < to) to -= 1;
            to = Math.Max(0, Math.Min(to, _vm.Servers.Count - 1));

            _vm.Servers.Move(from, to);
            PersistServers();
            RenderTree(SearchBox.Text);
            FlashRow(dragged);   // podświetl, gdzie wylądował
        }

        // Pokazuje/aktualizuje linię wskazującą miejsce upuszczenia na krawędzi wiersza.
        private void ShowDropIndicator(Border row, bool bottom)
        {
            var layer = AdornerLayer.GetAdornerLayer(row);
            if (layer == null) { ClearDropIndicator(); return; }

            if (_dropRow == row && _dropAdorner != null)
            {
                if (_dropAdorner.AtBottom != bottom) { _dropAdorner.AtBottom = bottom; _dropAdorner.InvalidateVisual(); }
                return;
            }
            ClearDropIndicator();
            _dropAdorner = new InsertionAdorner(row, Res("Accent")) { AtBottom = bottom };
            layer.Add(_dropAdorner);
            _dropRow = row;
        }

        private void ClearDropIndicator()
        {
            if (_dropAdorner != null && _dropRow != null)
                AdornerLayer.GetAdornerLayer(_dropRow)?.Remove(_dropAdorner);
            _dropAdorner = null;
            _dropRow = null;
        }

        // Krótkie podświetlenie wiersza (akcent → zanik) po zmianie kolejności — żeby oko złapało, gdzie wylądował.
        private void FlashRow(ServerInfo server)
        {
            if (server == null || !_serverRows.TryGetValue(server, out var row)) return;

            Color accent = (TryFindResource("Accent") as SolidColorBrush)?.Color ?? Color.FromRgb(0x29, 0xC5, 0xD6);
            var brush = new SolidColorBrush(Color.FromArgb(0x66, accent.R, accent.G, accent.B));
            row.Background = brush;

            var anim = new ColorAnimation
            {
                To = Color.FromArgb(0x00, accent.R, accent.G, accent.B),
                Duration = TimeSpan.FromMilliseconds(700),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            anim.Completed += (s, e) =>
            {
                bool active = _active != null && _active.Server == server;
                row.Background = active ? Res("AccentSoft") : Brushes.Transparent;
            };
            brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }

        // ---------- Otwieranie / przełączanie sesji ----------

        // Klik „otwórz serwer" (drzewo / ostatnie / pulpit / szybkie połączenie): karta w managerze
        // albo od razu osobne okno — zależnie od ustawienia OpenInNewWindowByDefault.
        private void LaunchServer(ServerInfo server, bool autoConnect, bool forceNew = false)
        {
            // Terminale zawsze jako karta — osobne okno sesji (SessionWindow) jest RDP-owe.
            if (_settings.OpenInNewWindowByDefault && server.Protocol == RemoteProtocol.Rdp) OpenInNewWindow(server);
            else OpenServer(server, autoConnect, forceNew);
        }

        // Wpis WWW: nie ma sesji — otwieramy panel webowy w domyślnej przeglądarce. Tylko http/https,
        // zob. Core.UrlValidation.
        private void OpenUrl(ServerInfo server)
        {
            string raw = (server.Host ?? "").Trim();
            if (raw.Length == 0) return;
            if (!Core.UrlValidation.TryNormalizeWebUrl(raw, out var uri))
            {
                SetStatus(string.Format(L("S.st.badurl"), raw), StatusKind.Error);
                return;
            }
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                RecordRecent(server);
            }
            catch (Exception ex) { SetStatus(string.Format(L("S.st.exception"), ex.Message), StatusKind.Error); }
        }

        private void OpenServer(ServerInfo server, bool autoConnect = false, bool forceNew = false)
        {
            if (server.Protocol == RemoteProtocol.Http) { OpenUrl(server); return; }

            // Kontrolka (RDP/konsola) musi powstać przy widocznym kontenerze sesji. Widoki „Sessions" i „Rest"
            // dzielą ten sam SessionContainer, więc gdy już jesteśmy w którymś z nich — nie przełączaj (inaczej
            // otwarcie żądania REST wyrzucałoby z modułu REST na listę serwerów). Z innych widoków → sesje.
            if (_currentView != "Sessions" && _currentView != "Rest") ShowView("Sessions");
            if (!forceNew)
            {
                var existing = _sessions.Find(x => x.Server == server);
                if (existing != null)
                {
                    Activate(existing);
                    if (autoConnect && !existing.Connected) BeginConnect(existing);
                    return;
                }
            }

            Session session;
            if (server.Protocol == RemoteProtocol.Telnet)
            {
                var term = new TelnetTerminalControl();
                SessionContainer.Children.Add(term);
                session = new Session(server, term);
                WireTermEvents(session);
            }
            else if (server.Protocol == RemoteProtocol.Serial)
            {
                var term = new SerialTerminalControl();
                SessionContainer.Children.Add(term);
                session = new Session(server, term);
                WireTermEvents(session);
            }
            else if (server.Protocol == RemoteProtocol.Ssh)
            {
                // SSH: terminal (WebView2 + xterm.js) zamiast kontrolki RDP; reszta cyklu życia wspólna.
                var term = new SshTerminalControl();
                term.TrustHostKey = AskTrustHostKey;              // TOFU klucza hosta (dialog na wątku UI)
                term.RequestKeyPassphrase = AskKeyPassphrase;     // zaszyfrowany klucz → prompt o passphrase
                SessionContainer.Children.Add(term);
                session = new Session(server, term);
                WireTermEvents(session);
            }
            else if (server.Protocol == RemoteProtocol.Sftp)
            {
                // SFTP jako osobny protokół: panel plików = widok sesji; łączy się leniwie (jak terminal).
                var conn = new SshConnectionFactory
                {
                    TrustHostKey = AskTrustHostKey,
                    RequestKeyPassphrase = AskKeyPassphrase
                };
                var panel = new DualFilePanel(() => conn.NewFs());
                SessionContainer.Children.Add(panel);
                session = new Session(server, panel, conn);
                WireFilesEvents(session);
            }
            else if (server.Protocol == RemoteProtocol.Ftp)
            {
                // FTP/FTPS jako osobny protokół: ten sam panel plików (IRemoteFs), konektor FluentFTP.
                var conn = new FtpConnector { TrustCertificate = AskTrustFtpsCert };   // TOFU certyfikatu (dialog na wątku UI)
                var panel = new DualFilePanel(() => conn.NewFs());
                SessionContainer.Children.Add(panel);
                session = new Session(server, panel, conn);
                WireFilesEvents(session);
            }
            else if (server.Protocol == RemoteProtocol.Rest)
            {
                // REST: konsola HTTP jako widok sesji. Narzędzie bez cyklu łączenia — gotowe od razu.
                var console = new RestConsole(server);
                // Konsola utrwaliła zmianę kolekcji (nazwa/metoda/struktura) → moduł w railu przebudowuje drzewo.
                console.CollectionChanged += () => { if (_restMode) BuildRestModule(); };
                SessionContainer.Children.Add(console);
                session = new Session(server, console);
                session.Connected = true;
                RecordRecent(server);
            }
            else if (server.Protocol == RemoteProtocol.Vnc)
            {
                // VNC (RemoteViewing) — kontrolka WinForms w hoście WPF, jak RDP. Zdarzenia wiążemy przy połączeniu.
                var vnc = new RemoteViewing.Windows.Forms.VncControl
                {
                    AllowInput = true,
                    Dock = System.Windows.Forms.DockStyle.Fill,
                    AllowRemoteCursor = true
                };
                var host = new WindowsFormsHost { Child = vnc };
                SessionContainer.Children.Add(host);
                host.UpdateLayout();
                session = new Session(server, vnc, host);
            }
            else
            {
                var rdp = new AxMsRdpClient11NotSafeForScripting();
                var host = new WindowsFormsHost();

                ((ISupportInitialize)rdp).BeginInit();
                rdp.Dock = System.Windows.Forms.DockStyle.Fill;
                host.Child = rdp;
                ((ISupportInitialize)rdp).EndInit();

                SessionContainer.Children.Add(host);
                host.UpdateLayout();
                try { ((System.Windows.Forms.Control)rdp).CreateControl(); } catch { }  // wymuś utworzenie kontrolki ActiveX

                session = new Session(server, rdp, host);
                session.Resizer = new RdpDynamicResolution(session, host);
                WireEvents(session);
                // W podziale: klik w panel (RDP przejmuje fokus klawiatury) czyni go aktywnym — karta i toolbar podążają.
                var focusTarget = session;
                host.GotKeyboardFocus += (s, e) => OnPaneFocused(focusTarget);
            }
            if (EffSavedPw(server) && CredentialStore.TryRead(EffCredTarget(server), out var savedPw))
                session.Password = savedPw;

            _sessions.Add(session);
            session.TabButton = BuildTab(session);
            if (GroupOf(session) != null) RebuildTabStrip();   // serwer w grupie → renderuj w jej kontenerze
            else { TabStrip.Children.Add(session.TabButton); RefreshTabTitles(); }
            if (session.IsRest) SetTabStatus(session, ServerStatus.Online);   // narzędzie: gotowe od razu

            Activate(session);
            if (autoConnect) BeginConnect(session);
            PersistOpenSessions();
        }

        private bool CanAuto(Session s)
        {
            switch (s.Server.Protocol)
            {
                case RemoteProtocol.Telnet:
                case RemoteProtocol.Serial:
                    return true;   // logowanie (jeśli jest) dzieje się w terminalu
                case RemoteProtocol.Ssh:
                case RemoteProtocol.Sftp:
                    return !string.IsNullOrWhiteSpace(EffUser(s.Server))
                           && (!string.IsNullOrEmpty(s.Password) || !string.IsNullOrWhiteSpace(s.Server.PrivateKeyPath));
                case RemoteProtocol.Ftp:
                    return s.Server.FtpAnonymous
                           || (!string.IsNullOrWhiteSpace(EffUser(s.Server)) && !string.IsNullOrEmpty(s.Password));
                default:
                    return s.Server.UseWindowsAccount || !string.IsNullOrEmpty(s.Password);
            }
        }

        private void Activate(Session session)
        {
            HideFocusPeek();   // aktywacja sesji (np. klik z wysuniętego panelu) chowa peek (i przenosi panel z powrotem)
            _active = session;
            // W podziale: aktywacja karty NIEbędącej panelem kończy podział (pokaż tę sesję pojedynczo).
            // Klik w kartę panelu tylko przenosi fokus (podział zostaje).
            if ((_paneLeft != null || _paneRight != null) && session != _paneLeft && session != _paneRight)
            { _paneLeft = null; _paneRight = null; }
            // Zwinięta grupa pokazuje aktywną kartę — po zmianie aktywnej trzeba przebudować pasek.
            if (_tabGroups.Any(g => g.Collapsed)) RebuildTabStrip();
            RefreshTabStyles();
            UpdateActiveRows();
            LoadToolbar(session);
            UpdateToolbarEnabled();
            UpdateToolbarMode();
            UpdateCanvas();
            SetStatus(session.Status, session.StatusKind);
            FsName.Text = session.Server.Name + " · " + session.Server.Host;
            UpdateImmersive();
        }

        // ---------- Podział ekranu (split-screen) ----------

        /// <summary>Wchodzi w podział: sesja `right` w prawym panelu, aktywna (lub pierwsza inna) RDP w lewym.
        /// Tylko RDP; wymaga dwóch różnych sesji RDP.</summary>
        private void EnterSplit(Session right)
        {
            if (right == null || right.Server.Protocol != RemoteProtocol.Rdp) return;
            Session left = (_active != null && _active != right && _active.Server.Protocol == RemoteProtocol.Rdp)
                ? _active
                : _sessions.FirstOrDefault(s => s != right && s.Server.Protocol == RemoteProtocol.Rdp);
            if (left == null) return;   // potrzebne dwie sesje RDP
            _paneLeft = left;
            _paneRight = right;
            Activate(right);            // fokus na nowy panel; Activate odświeży pasek/toolbar/status + UpdateCanvas (podział)
        }

        /// <summary>Kończy podział — pozostaje pojedynczy widok aktywnej sesji.</summary>
        private void ExitSplit()
        {
            if (_paneLeft == null && _paneRight == null) return;
            _paneLeft = null;
            _paneRight = null;
            UpdateCanvas();
        }

        /// <summary>Klik w panel podziału (RDP przejął fokus klawiatury) → uczyń go aktywnym: podświetlenie
        /// karty i toolbar podążają za panelem, w którym pracujesz.</summary>
        private void OnPaneFocused(Session s)
        {
            if (_paneLeft == null && _paneRight == null) return;   // działa tylko w podziale
            if ((s != _paneLeft && s != _paneRight) || _active == s) return;
            _active = s;
            RefreshTabStyles();
            LoadToolbar(s);
            UpdateToolbarEnabled();
            SetStatus(s.Status, s.StatusKind);
        }

        /// <summary>Pokazuje strefę upuszczenia podziału na czas przeciągania karty (tylko RDP, ≥2 sesje RDP,
        /// bez aktywnego podziału, gdy jest gdzie ją położyć). Zwraca true, jeśli pokazano.</summary>
        private bool ShowSplitDropZone(Session dragged)
        {
            if (dragged == null || dragged.Server.Protocol != RemoteProtocol.Rdp) return false;
            if (_paneLeft != null || _paneRight != null) return false;                 // już podzielone
            if (_sessions.Count(x => x.Server.Protocol == RemoteProtocol.Rdp) < 2) return false;
            if (SessionContainer.ActualWidth < 100 || SessionContainer.ActualHeight < 100) return false;
            SplitDropBorder.Width = SessionContainer.ActualWidth;    // dopasuj do obszaru sesji (host renderuje 1:1)
            SplitDropBorder.Height = SessionContainer.ActualHeight;
            _splitDropSession = dragged;
            SplitDropZone.IsOpen = true;
            return true;
        }

        private void HideSplitDropZone()
        {
            SplitDropZone.IsOpen = false;
            _splitDropSession = null;
        }

        /// <summary>
        /// Steruje kanwą: aktywna kontrolka RDP widoczna tylko gdy połączona; w przeciwnym razie
        /// nakładka (spinner „Łączenie…" albo „Rozłączono" + przycisk ponownego połączenia).
        /// </summary>
        private void UpdateCanvas()
        {
            bool has = _active != null;
            EmptyState.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            if (!has) BuildEmptyRecent();   // odśwież chipy „ostatnie" przy każdym powrocie do pustego stanu

            // Tryb podziału: dwie sesje RDP widoczne naraz (lewy panel = kol.0, prawy = kol.2, splitter w kol.1).
            if (_paneLeft != null && _paneRight != null)
            {
                foreach (var s in _sessions)
                {
                    bool pane = s == _paneLeft || s == _paneRight;
                    if (pane) Grid.SetColumn(s.View, s == _paneRight ? 2 : 0);
                    if (s.Resizer != null) s.Resizer.FitToWindow = pane;   // panele skalują pulpit do swojej połówki (zawsze się mieści)
                    s.View.Visibility = (pane && s.Connected) ? Visibility.Visible : Visibility.Collapsed;
                }
                if (PaneColRight.Width.GridUnitType != GridUnitType.Star)   // wejście w podział = 50/50; drag splittera zachowany
                    PaneColRight.Width = new GridLength(1, GridUnitType.Star);
                PaneSplitter.Visibility = Visibility.Visible;
                SessionOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            // Bez podziału: pojedynczy widok w kol.0 (przywróć pełną szerokość po ewentualnym dragu splittera).
            PaneColLeft.Width = new GridLength(1, GridUnitType.Star);
            PaneColRight.Width = new GridLength(0);
            PaneSplitter.Visibility = Visibility.Collapsed;

            // Terminale (SSH/Telnet/Serial): widoczne od razu — statusy łączenia piszą do siebie.
            foreach (var s in _sessions)
            {
                Grid.SetColumn(s.View, 0);
                if (s.Resizer != null) s.Resizer.FitToWindow = false;   // pojedynczy widok = natywna, ostra rozdzielczość
                s.View.Visibility = (s == _active && (s.Connected || s.IsTerm || s.IsFiles)) ? Visibility.Visible : Visibility.Collapsed;
            }

            if (!has)
            {
                SessionOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            // Nakładka nie dla terminali (HWND by ją zakrył) ani plików (panel ma własny pasek statusu).
            if (_active.Connected || _active.IsTerm || _active.IsFiles)
            {
                SessionOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            SessionOverlay.Visibility = Visibility.Visible;
            bool connecting = _active.StatusKind == StatusKind.Connecting;
            OverlaySpinner.Visibility = connecting ? Visibility.Visible : Visibility.Collapsed;
            OverlayReconnect.Visibility = connecting ? Visibility.Collapsed : Visibility.Visible;
            OverlayReconnect.Content = L("S.reconnect");
            OverlayTitle.Text = connecting
                ? string.Format(L("S.st.connecting"), _active.Server.Host)
                : (_active.StatusKind == StatusKind.Error ? L("S.st.disconnectedShort") : L("S.st.ready"));
            OverlayMsg.Text = connecting ? "" : _active.Status;
        }

        private void OverlayAction_Click(object sender, RoutedEventArgs e)
        {
            // Nie przez Connect_Click: pasek z hasłem bywa ukryty (tryb skupienia) — działaj na modelu
            // sesji i dopytaj dialogiem, gdy brakuje poświadczeń.
            if (_active == null) return;
            if (CanAuto(_active)) ConnectSession(_active);
            else PromptAndConnect(_active, null);
        }

        private void LoadToolbar(Session s)
        {
            CfAvatar.Background = AvatarBrush(s.Server);
            CfAvatarText.Text = ServerInitials(s.Server);
            CfName.Text = s.Server.Name;
            CfHost.Text = s.Server.Host + ":" + s.Server.Port;
            // Konto Windows tylko dla RDP; Telnet/Serial nie mają pól poświadczeń w ogóle.
            WinAuthCheck.Visibility = s.Server.Protocol == RemoteProtocol.Rdp ? Visibility.Visible : Visibility.Collapsed;
            WinAuthCheck.IsChecked = s.Server.UseWindowsAccount;
            PassBox.Password = s.Password ?? "";
            UpdatePassVisibility();
        }

        // ---------- Pasek zakładek ----------

        private FrameworkElement BuildTab(Session session)
            => IsMinimalList ? BuildTabMinimal(session) : BuildTabDefault(session);

        // Wariant DOMYŚLNY: awatar 14px + nazwa + kropka statusu + ✕; aktywna = podkreślenie akcentem.
        private FrameworkElement BuildTabDefault(Session session)
        {
            var tab = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Transparent,
                Background = Brushes.Transparent,
                Padding = new Thickness(10, 6, 7, 5),
                Margin = new Thickness(0, 0, 4, 0),
                Cursor = Cursors.Hand,
                Tag = session,
                ToolTip = session.Server.Name + " — " + DisplayHost(session.Server)
            };

            // 2 wiersze: treść (góra) + pasek podświetlenia (dół) z odstępem — pasek nie nachodzi na nazwę.
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new Border
            {
                Width = 14, Height = 14, CornerRadius = new CornerRadius(4),
                Background = AvatarBrush(session.Server), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = ServerInitials(session.Server), Foreground = Brushes.White, FontSize = 7, FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            });
            var tabName = new TextBlock
            {
                Text = session.Server.Name, Foreground = Res("TextPrim"), FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0)
            };
            _tabName[session] = tabName;
            content.Children.Add(tabName);
            // Adres nie jest już na karcie (był w 3 miejscach naraz) — zostaje w pasku bocznym,
            // podpowiedzi karty i szybkim przełączaniu. Karta = ikona + nazwa + kropka + ✕.
            // Kropka odzwierciedla ŻYWY stan sesji (nie statyczny status serwera): startowo rozłączona.
            var tabDot = new Ellipse
            {
                Width = 6, Height = 6, Fill = StatusBrush(ServerStatus.Offline),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0)
            };
            _tabStatus[session] = tabDot;
            content.Children.Add(tabDot);
            content.Children.Add(BuildTabClose(session));
            Grid.SetRow(content, 0);
            grid.Children.Add(content);

            var underline = BuildTabUnderline(new Thickness(2, 4, 2, 0));
            Grid.SetRow(underline, 1);
            grid.Children.Add(underline);

            tab.Child = grid;
            _tabUnderline[session] = underline;
            WireTab(tab, session);
            return tab;
        }

        // Wariant MINIMALISTYCZNY: kropka statusu + nazwa (bez awatara i hosta) — niższa, lżejsza karta.
        private FrameworkElement BuildTabMinimal(Session session)
        {
            var tab = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Transparent,
                Background = Brushes.Transparent,
                Padding = new Thickness(11, 2, 6, 2),
                Margin = new Thickness(0, 0, 4, 0),
                Cursor = Cursors.Hand,
                Tag = session,
                ToolTip = session.Server.Name + " — " + DisplayHost(session.Server)
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            var tabDot = new Ellipse
            {
                Width = 7, Height = 7, Fill = StatusBrush(ServerStatus.Offline), VerticalAlignment = VerticalAlignment.Center
            };
            _tabStatus[session] = tabDot;
            content.Children.Add(tabDot);
            var tabName = new TextBlock
            {
                Text = session.Server.Name, Foreground = Res("TextPrim"), FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0)
            };
            _tabName[session] = tabName;
            content.Children.Add(tabName);
            content.Children.Add(BuildTabClose(session));
            Grid.SetRow(content, 0);
            grid.Children.Add(content);

            var underline = BuildTabUnderline(new Thickness(2, 2, 2, 0));
            Grid.SetRow(underline, 1);
            grid.Children.Add(underline);

            tab.Child = grid;
            _tabUnderline[session] = underline;
            WireTab(tab, session);
            return tab;
        }

        // ✕ karty (wspólny dla obu stylów): pokazywany na aktywnej/hoverze (Hidden, nie Collapsed — stała szerokość).
        private TextBlock BuildTabClose(Session session)
        {
            var close = new TextBlock
            {
                Text = "✕", Foreground = Res("TextTer"), FontSize = 11,
                Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(3, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand,
                Visibility = Visibility.Hidden
            };
            close.MouseEnter += (s, e) => close.Foreground = Res("Danger");
            close.MouseLeave += (s, e) => close.Foreground = Res("TextTer");
            close.MouseLeftButtonUp += (s, e) => { e.Handled = true; RequestCloseSession(session); };
            _tabClose[session] = close;
            return close;
        }

        private Rectangle BuildTabUnderline(Thickness margin) => new Rectangle
        {
            Height = 2, Fill = Res("Accent"), RadiusX = 1, RadiusY = 1,
            Margin = margin, Visibility = Visibility.Hidden   // Hidden: karta ma stałą wysokość aktywna/nie
        };

        // Wspólne zachowanie karty (hover / klik / środkowy-klik / przeciąganie: grupuj lub zmień kolejność / menu).
        private void WireTab(Border tab, Session session)
        {
            tab.MouseEnter += (s, e) =>
            {
                if (session != _active) tab.Background = Res("Elevated") ?? Brushes.Transparent;
                if (_tabClose.TryGetValue(session, out var c)) c.Visibility = Visibility.Visible;
            };
            tab.MouseLeave += (s, e) => RefreshTabStyles();
            tab.MouseLeftButtonUp += (s, e) =>
            {
                if (_tabDidDrag) { _tabDidDrag = false; return; }   // to było przeciąganie, nie klik
                Activate(session);
            };
            // Środkowy klik zamyka kartę (standard z przeglądarek).
            tab.MouseDown += (s, e) =>
            {
                if (e.ChangedButton == MouseButton.Middle) { RequestCloseSession(session); e.Handled = true; }
            };

            tab.AllowDrop = true;
            tab.PreviewMouseLeftButtonDown += (s, e) => { _tabDragStart = e.GetPosition(null); _tabDragSession = session; _tabDidDrag = false; };
            tab.PreviewMouseMove += (s, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || _tabDragSession != session) return;
                var pos = e.GetPosition(null);
                if (Math.Abs(pos.X - _tabDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(pos.Y - _tabDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                _tabDidDrag = true;
                tab.Opacity = 0.5;
                bool zone = ShowSplitDropZone(session);   // „upuść w obszar sesji, aby podzielić" (tylko RDP, ≥2 sesje)
                try { DragDrop.DoDragDrop(tab, session, DragDropEffects.Move); }
                catch { }
                finally { tab.Opacity = 1.0; _tabDragSession = null; if (zone) HideSplitDropZone(); }
            };
            tab.DragOver += (s, e) =>
            {
                if (!(e.Data.GetData(typeof(Session)) is Session over) || over == session)
                { e.Effects = DragDropEffects.None; return; }
                e.Effects = DragDropEffects.Move; e.Handled = true;
                double x = e.GetPosition(tab).X, w = tab.ActualWidth;
                ShowTabDropIndicator(tab, group: x > w * 0.33 && x < w * 0.67, after: x >= w / 2);
            };
            tab.DragLeave += (s, e) => ClearTabDropIndicator();
            tab.Drop += (s, e) =>
            {
                ClearTabDropIndicator();
                if (!(e.Data.GetData(typeof(Session)) is Session dragged) || dragged == session) return;
                double x = e.GetPosition(tab).X, w = tab.ActualWidth;
                if (x > w * 0.33 && x < w * 0.67) GroupTabs(session, dragged);   // środek celu = grupuj
                else MoveTabTo(dragged, session, after: x >= w / 2);              // brzeg = zmiana kolejności
                e.Handled = true;
            };

            var tabMenu = new ContextMenu();
            var tearItem = new MenuItem { Header = L("S.m.tearoff") };
            tearItem.Click += (s, e) => TearOffToWindow(session);
            var dupItem = new MenuItem { Header = L("S.m.duplicate") };
            dupItem.Click += (s, e) => DuplicateSession(session);
            var moveLeft = new MenuItem { Header = L("S.m.moveleft") };
            moveLeft.Click += (s, e) => MoveTab(session, -1);
            var moveRight = new MenuItem { Header = L("S.m.moveright") };
            moveRight.Click += (s, e) => MoveTab(session, +1);
            var closeOthers = new MenuItem { Header = L("S.m.closeothers") };
            closeOthers.Click += (s, e) => CloseOtherSessions(session);
            var closeThis = new MenuItem { Header = L("S.m.close") };
            closeThis.Click += (s, e) => RequestCloseSession(session);
            if (session.IsSsh)
            {
                var broadcastItem = new MenuItem { Header = L("S.m.broadcast") };
                broadcastItem.Click += (s, e) => BroadcastToSsh();
                tabMenu.Items.Add(broadcastItem);
                tabMenu.Items.Add(new Separator());
            }
            MenuItem splitItem = null, unsplitItem = null;
            if (session.Server.Protocol == RemoteProtocol.Rdp)
            {
                tabMenu.Items.Add(tearItem);   // wyciąganie do okna jest RDP-owe
                var cadItem = new MenuItem { Header = L("S.m.cad") };
                cadItem.Click += (s, e) => SendCtrlAltDel(session);
                tabMenu.Items.Add(cadItem);
                splitItem = new MenuItem { Header = L("S.m.split") };      // ta sesja w prawym panelu, aktywna w lewym
                splitItem.Click += (s, e) => EnterSplit(session);
                unsplitItem = new MenuItem { Header = L("S.m.unsplit") };
                unsplitItem.Click += (s, e) => ExitSplit();
                tabMenu.Items.Add(splitItem);
                tabMenu.Items.Add(unsplitItem);
            }
            tabMenu.Items.Add(dupItem);
            tabMenu.Items.Add(new Separator());
            tabMenu.Items.Add(moveLeft);
            tabMenu.Items.Add(moveRight);
            tabMenu.Items.Add(new Separator());
            tabMenu.Items.Add(closeOthers);
            tabMenu.Items.Add(closeThis);
            tab.ContextMenu = tabMenu;
            // Pozycje dot. grup zależą od bieżącego stanu (jakie grupy istnieją) — wstrzykiwane przy otwarciu.
            tabMenu.Opened += (s, e) =>
            {
                PopulateTabGroupItems(tabMenu, session);
                if (splitItem != null)   // „Podziel" gdy są ≥2 sesje RDP i nie ma podziału; „Zakończ podział" w podziale
                {
                    bool split = _paneLeft != null && _paneRight != null;
                    int rdp = _sessions.Count(x => x.Server.Protocol == RemoteProtocol.Rdp);
                    splitItem.Visibility = (!split && rdp >= 2) ? Visibility.Visible : Visibility.Collapsed;
                    unsplitItem.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
                }
            };
        }

        /// <summary>Wysyła jedną komendę (z Enterem) do wszystkich połączonych sesji SSH naraz.</summary>
        private void BroadcastToSsh()
        {
            var targets = _sessions.Where(s => s.IsSsh && s.Connected).ToList();
            if (targets.Count == 0)
            {
                MessageBox.Show(L("S.bc.none"), L("S.m.broadcast"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new InputDialog(L("S.m.broadcast"), string.Format(L("S.bc.prompt"), targets.Count), "") { Owner = this };
            if (dlg.ShowDialog() != true) return;
            string cmd = dlg.Value;
            if (string.IsNullOrEmpty(cmd)) return;

            int sent = 0;
            foreach (var s in targets)
            {
                try { s.Ssh.SendText(cmd + "\n"); sent++; } catch { /* sesja właśnie padła — pomiń */ }
            }
            SetStatus(string.Format(L("S.bc.sent"), sent), StatusKind.Ok);
        }

        private void RefreshTabStyles()
        {
            foreach (var s in _sessions)
            {
                if (!(s.TabButton is Border b)) continue;
                bool active = s == _active;
                // Lżej: aktywna = subtelne tło + akcent (underline), bez „pudełkowego" obrysu.
                b.Background = active ? Res("Panel") : Brushes.Transparent;
                b.BorderBrush = Brushes.Transparent;
                // Hierarchia: nieaktywne karty przygaszone (spokojniejszy pasek).
                if (_tabName.TryGetValue(s, out var nm))
                    nm.Foreground = Res(active ? "TextPrim" : "TextSec");
                if (_tabUnderline.TryGetValue(s, out var u))
                    u.Visibility = active ? Visibility.Visible : Visibility.Hidden;
                if (_tabClose.TryGetValue(s, out var c))
                    c.Visibility = active ? Visibility.Visible : Visibility.Hidden;   // ✕ tylko na aktywnej/hoverze
            }
        }

        /// <summary>
        /// Rozróżnia zakładki o tej samej nazwie: dopisuje host, a przy duplikatach tej samej
        /// sesji (identyczna nazwa i host) — numer wystąpienia (#2, #3…).
        /// </summary>
        private void RefreshTabTitles()
        {
            var nameSeen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in _sessions)
            {
                if (!_tabName.TryGetValue(s, out var tn)) continue;

                bool dupName = _sessions.Any(o => o != s &&
                    string.Equals(o.Server.Name, s.Server.Name, StringComparison.OrdinalIgnoreCase));
                string title = dupName ? s.Server.Name + " (" + s.Server.Host + ")" : s.Server.Name;

                nameSeen.TryGetValue(title, out int seen);
                nameSeen[title] = seen + 1;
                if (seen > 0) title += " #" + (seen + 1);   // duplikaty tej samej sesji

                tn.Text = title;
            }
        }

        private void CloseOtherSessions(Session keep)
        {
            var others = _sessions.Where(s => s != keep).ToList();
            int connected = others.Count(s => s.Connected);
            // Jedno zbiorcze potwierdzenie zamiast dialogu per sesja.
            if (connected > 0 && _settings.ConfirmCloseConnected &&
                MessageBox.Show(string.Format(L("S.msg.closeothers"), connected),
                    L("S.m.closeothers"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            foreach (var s in others) CloseSession(s);
        }

        /// <summary>Otwiera drugą, niezależną sesję do tego samego serwera (osobna zakładka).</summary>
        private void DuplicateSession(Session s) => OpenServer(s.Server, autoConnect: true, forceNew: true);

        private Point _tabDragStart;
        private Session _tabDragSession;
        private bool _tabDidDrag;

        /// <summary>Wstawia przeciąganą zakładkę przed/za <paramref name="target"/> (drag&amp;drop w pasku).</summary>
        private void MoveTabTo(Session dragged, Session target, bool after)
        {
            int from = _sessions.IndexOf(dragged);
            if (from < 0 || _sessions.IndexOf(target) < 0) return;
            _sessions.RemoveAt(from);
            int to = _sessions.IndexOf(target) + (after ? 1 : 0);
            _sessions.Insert(to, dragged);
            RebuildTabStrip();   // odbudowa respektuje grupy (kontenery) i numerację duplikatów
        }

        /// <summary>Adres do wyświetlenia — z prefiksem protokołu, żeby odróżnić na pierwszy rzut oka.</summary>
        private static string DisplayHost(ServerInfo s)
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

        /// <summary>Przesuwa zakładkę w pasku o <paramref name="dir"/> (-1 w lewo, +1 w prawo).</summary>
        private void MoveTab(Session s, int dir)
        {
            int i = _sessions.IndexOf(s);
            int j = i + dir;
            if (i < 0 || j < 0 || j >= _sessions.Count) return;

            _sessions.RemoveAt(i);
            _sessions.Insert(j, s);
            RebuildTabStrip();   // odbudowa respektuje grupy (kontenery) i numerację duplikatów
        }

        // ---------- Grupy kart (stosy jak w Vivaldi) ----------

        // Paleta kolorów grup (spójna z akcentami/awatarami motywu). Nowa grupa dostaje pierwszy nieużyty.
        private static readonly Color[] GroupColors =
        {
            Color.FromRgb(0x7C, 0x6C, 0xFB),  // fiolet
            Color.FromRgb(0x36, 0xB8, 0xC4),  // turkus
            Color.FromRgb(0xFF, 0xB4, 0x54),  // bursztyn
            Color.FromRgb(0x37, 0x8A, 0xDD),  // błękit
            Color.FromRgb(0xD4, 0x53, 0x7E),  // róż
            Color.FromRgb(0x3D, 0xDC, 0x97),  // zieleń
        };
        private const string GroupMenuMark = "grp";   // znacznik pozycji menu karty wstrzykiwanych dla grup

        private TabGroup GroupOf(Session s) => s == null ? null : _tabGroups.FirstOrDefault(g => g.ServerIds.Contains(s.Server.Id));

        private Color NextGroupColor()
        {
            foreach (var c in GroupColors)
                if (!_tabGroups.Any(g => g.Color == c)) return c;
            return GroupColors[_tabGroups.Count % GroupColors.Length];
        }

        // Wypina serwer ze wszystkich grup i kasuje grupy, które przez to zostały puste.
        private void DetachServerFromGroups(string serverId)
        {
            foreach (var g in _tabGroups) g.ServerIds.Remove(serverId);
            _tabGroups.RemoveAll(g => g.ServerIds.Count == 0);
        }

        private void CreateGroupFromTab(Session seed)
        {
            string suggested = string.IsNullOrWhiteSpace(seed.Server.Group) ? L("S.group.default") : seed.Server.Group;
            var dlg = new InputDialog(L("S.group.newtitle"), L("S.group.nameprompt"), suggested) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            DetachServerFromGroups(seed.Server.Id);
            var group = new TabGroup { Name = string.IsNullOrWhiteSpace(dlg.Value) ? suggested : dlg.Value, Color = NextGroupColor() };
            group.ServerIds.Add(seed.Server.Id);
            _tabGroups.Add(group);
            SaveTabGroups();
            RebuildTabStrip();
        }

        private void AddToGroup(Session s, TabGroup g)
        {
            if (s == null || g == null) return;
            DetachServerFromGroups(s.Server.Id);          // przenieś z ewentualnej innej grupy
            if (!_tabGroups.Contains(g)) return;          // (gdyby odpięcie ją opróżniło)
            if (!g.ServerIds.Contains(s.Server.Id)) g.ServerIds.Add(s.Server.Id);
            SaveTabGroups();
            RebuildTabStrip();
        }

        // Upuszczenie karty NA środek innej (jak w Vivaldi): tworzy grupę z obu (gdy cel luzem) albo
        // dokłada przeciąganą do grupy celu. Bez pytania o nazwę — nazwę zmienia się z menu pastylki.
        private void GroupTabs(Session target, Session dragged)
        {
            if (target == null || dragged == null || target == dragged || target.Server.Id == dragged.Server.Id) return;
            var g = GroupOf(target);
            if (g == null)
            {
                g = new TabGroup { Name = AutoGroupName(target), Color = NextGroupColor() };
                g.ServerIds.Add(target.Server.Id);
                _tabGroups.Add(g);
            }
            DetachServerFromGroups(dragged.Server.Id);    // wyjmij z ewentualnej starej grupy
            if (!_tabGroups.Contains(g)) return;
            if (!g.ServerIds.Contains(dragged.Server.Id)) g.ServerIds.Add(dragged.Server.Id);
            SaveTabGroups();
            RebuildTabStrip();
        }

        private string AutoGroupName(Session seed) =>
            string.IsNullOrWhiteSpace(seed.Server.Group) ? L("S.group.default") : seed.Server.Group;

        private void RemoveFromGroup(Session s)
        {
            if (s == null) return;
            DetachServerFromGroups(s.Server.Id);
            SaveTabGroups();
            RebuildTabStrip();
        }

        private void Ungroup(TabGroup g)
        {
            _tabGroups.Remove(g);
            SaveTabGroups();
            RebuildTabStrip();
        }

        // Zapis/odczyt grup w ustawieniach (kolor jako #AARRGGBB) — grupy przeżywają restart aplikacji.
        private void SaveTabGroups()
        {
            _settings.TabGroups = _tabGroups.Select(g => new TabGroupDef
            {
                Name = g.Name,
                Color = string.Format("#{0:X2}{1:X2}{2:X2}{3:X2}", g.Color.A, g.Color.R, g.Color.G, g.Color.B),
                Collapsed = g.Collapsed,
                ServerIds = g.ServerIds.ToList()
            }).ToList();
            SettingsStore.Save(_settings);
        }

        private void LoadTabGroups()
        {
            _tabGroups.Clear();
            foreach (var d in _settings.TabGroups ?? new List<TabGroupDef>())
            {
                Color color;
                try { color = (Color)ColorConverter.ConvertFromString(d.Color); }
                catch { color = GroupColors[0]; }
                _tabGroups.Add(new TabGroup
                {
                    Name = d.Name, Color = color, Collapsed = d.Collapsed,
                    ServerIds = (d.ServerIds ?? new List<string>()).ToList()
                });
            }
        }

        private static void DetachTab(Session s)
        {
            if (s?.TabButton is FrameworkElement fe && fe.Parent is Panel p) p.Children.Remove(fe);
        }

        // Minimal: niższy pasek kart (mniejszy margines) i drobniejsze ikony sesji po prawej stronie paska.
        private void ApplyTabStripStyle()
        {
            bool min = IsMinimalList;
            TabStrip.Margin = new Thickness(8, min ? 2 : 6, 8, min ? 2 : 6);
            foreach (var b in SessionActions.Children.OfType<Button>())
            {
                b.Width = min ? 24 : 28;
                b.Height = min ? 24 : 28;
            }
        }

        // Podpowiedź przy przeciąganiu karty: środek celu = podświetlenie („zgrupuj"), brzeg = pionowa
        // krawędź („wstaw przed/za"). Czyszczenie przywraca style wszystkich kart (RefreshTabStyles).
        private Border _tabDropTarget;

        private void ShowTabDropIndicator(Border tab, bool group, bool after)
        {
            ClearTabDropIndicator();
            _tabDropTarget = tab;
            if (group)
            {
                tab.Background = Res("AccentSoft");
                tab.BorderBrush = Res("Accent");
                tab.BorderThickness = new Thickness(1);
            }
            else
            {
                tab.BorderBrush = Res("Accent");
                tab.BorderThickness = after ? new Thickness(0, 0, 2, 0) : new Thickness(2, 0, 0, 0);
            }
        }

        private void ClearTabDropIndicator()
        {
            if (_tabDropTarget == null) return;
            _tabDropTarget.BorderThickness = new Thickness(1);   // domyślna grubość z BuildTab
            _tabDropTarget = null;
            RefreshTabStyles();
        }

        /// <summary>Porządkuje _sessions tak, by członkowie każdej grupy stali obok siebie (stabilnie, wg
        /// pierwszego wystąpienia) — dzięki temu grupa renderuje się jako jeden kontener.</summary>
        private void NormalizeGroupOrder()
        {
            var ordered = new List<Session>(_sessions.Count);
            var emitted = new HashSet<TabGroup>();
            foreach (var s in _sessions)
            {
                var g = GroupOf(s);
                if (g == null) { ordered.Add(s); continue; }
                if (emitted.Add(g)) ordered.AddRange(_sessions.Where(x => GroupOf(x) == g));
            }
            _sessions.Clear();
            _sessions.AddRange(ordered);
        }

        /// <summary>Przebudowuje pasek: karty luzem trafiają wprost do paska, a ciągi kart tej samej grupy —
        /// do wspólnego kontenera (z możliwością zwinięcia do liczby). Odłącza karty od starych rodziców.</summary>
        private void RebuildTabStrip()
        {
            ApplyTabStripStyle();
            foreach (var s in _sessions) DetachTab(s);   // karta = jeden rodzic naraz
            TabStrip.Children.Clear();
            NormalizeGroupOrder();

            int i = 0;
            while (i < _sessions.Count)
            {
                var g = GroupOf(_sessions[i]);
                if (g == null) { TabStrip.Children.Add(_sessions[i].TabButton); i++; continue; }

                var members = new List<Session>();
                while (i < _sessions.Count && GroupOf(_sessions[i]) == g) { members.Add(_sessions[i]); i++; }
                TabStrip.Children.Add(BuildGroupContainer(g, members));
            }

            RefreshTabTitles();
            RefreshTabStyles();
        }

        private FrameworkElement BuildGroupContainer(TabGroup g, List<Session> members)
        {
            var color = g.Color;
            var tint = new SolidColorBrush(Color.FromArgb(0x22, color.R, color.G, color.B));
            var strong = new SolidColorBrush(Color.FromArgb(0x3A, color.R, color.G, color.B));

            var box = new Border
            {
                CornerRadius = new CornerRadius(8), Background = tint, BorderBrush = strong, BorderThickness = new Thickness(1),
                Padding = new Thickness(3, 0, 4, 0), Margin = new Thickness(0, 0, 5, 0)
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            box.Child = row;

            // Pastylka z nazwą: klik = zwiń/rozwiń; prawy klik = menu (nazwa / kolor / rozgrupuj).
            var pill = new Border
            {
                CornerRadius = new CornerRadius(5), Background = strong, Cursor = Cursors.Hand,
                Padding = new Thickness(6, IsMinimalList ? 1 : 2, 7, IsMinimalList ? 1 : 3),
                Margin = new Thickness(1, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
                ContextMenu = BuildGroupMenu(g)
            };
            var pillRow = new StackPanel { Orientation = Orientation.Horizontal };
            pillRow.Children.Add(new TextBlock
            {
                Text = g.Collapsed ? "▸" : "▾", Foreground = new SolidColorBrush(color), FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0)
            });
            pillRow.Children.Add(new TextBlock
            {
                Text = g.Name, Foreground = new SolidColorBrush(color), FontSize = IsMinimalList ? 11 : 11.5, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            if (g.Collapsed)
                pillRow.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(9), Background = Res("Elevated"),
                    Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = members.Count.ToString(), Foreground = Res("TextSec"), FontSize = 10.5 }
                });
            pill.Child = pillRow;
            // e.Handled: klik przebudowuje pasek (usuwa tę pastylkę) — nie pozwól zdarzeniu bąbelkować dalej.
            pill.MouseLeftButtonUp += (s, e) => { e.Handled = true; g.Collapsed = !g.Collapsed; SaveTabGroups(); RebuildTabStrip(); };
            row.Children.Add(pill);

            // Rozwinięta: wszystkie karty. Zwinięta: pastylka + licznik, ale aktywna karta „wychodzi" ze
            // stosu (jak w Vivaldi) — widać, którą sesję się ogląda. Przełączenie aktywnej odświeża pasek.
            foreach (var m in members)
                if (!g.Collapsed || m == _active) row.Children.Add(m.TabButton);

            return box;
        }

        private ContextMenu BuildGroupMenu(TabGroup g)
        {
            var menu = new ContextMenu();

            var rename = new MenuItem { Header = L("S.m.grp.rename") };
            rename.Click += (s, e) =>
            {
                var dlg = new InputDialog(L("S.group.renametitle"), L("S.group.nameprompt"), g.Name) { Owner = this };
                if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Value)) { g.Name = dlg.Value; SaveTabGroups(); RebuildTabStrip(); }
            };
            menu.Items.Add(rename);

            var colorItem = new MenuItem { Header = L("S.m.grp.color") };
            foreach (var c in GroupColors)
            {
                var cc = c;
                var swatch = new MenuItem { Header = new TextBlock { Text = "●", Foreground = new SolidColorBrush(cc), FontSize = 15 } };
                swatch.Click += (s, e) => { g.Color = cc; SaveTabGroups(); RebuildTabStrip(); };
                colorItem.Items.Add(swatch);
            }
            menu.Items.Add(colorItem);

            var toggle = new MenuItem { Header = g.Collapsed ? L("S.m.grp.expand") : L("S.m.grp.collapse") };
            toggle.Click += (s, e) => { g.Collapsed = !g.Collapsed; SaveTabGroups(); RebuildTabStrip(); };
            menu.Items.Add(toggle);

            menu.Items.Add(new Separator());
            var ungroup = new MenuItem { Header = L("S.m.grp.ungroup") };
            ungroup.Click += (s, e) => Ungroup(g);
            menu.Items.Add(ungroup);
            return menu;
        }

        // Wstrzykuje na górę menu karty pozycje dot. grup (lista grup zmienia się w czasie — stąd przy otwarciu).
        private void PopulateTabGroupItems(ContextMenu menu, Session session)
        {
            for (int k = menu.Items.Count - 1; k >= 0; k--)
                if (menu.Items[k] is FrameworkElement fe && (fe.Tag as string) == GroupMenuMark)
                    menu.Items.RemoveAt(k);

            var inject = new List<Control>();
            var g = GroupOf(session);
            if (g == null)
            {
                var ng = new MenuItem { Header = L("S.m.newgroup"), Tag = GroupMenuMark };
                ng.Click += (s, e) => CreateGroupFromTab(session);
                inject.Add(ng);

                if (_tabGroups.Count > 0)
                {
                    var add = new MenuItem { Header = L("S.m.addtogroup"), Tag = GroupMenuMark };
                    foreach (var grp in _tabGroups)
                    {
                        var gg = grp;
                        var gi = new MenuItem
                        {
                            Header = grp.Name,
                            Icon = new Rectangle { Width = 10, Height = 10, RadiusX = 3, RadiusY = 3, Fill = new SolidColorBrush(grp.Color) }
                        };
                        gi.Click += (s, e) => AddToGroup(session, gg);
                        add.Items.Add(gi);
                    }
                    inject.Add(add);
                }
            }
            else
            {
                var rm = new MenuItem { Header = L("S.m.removefromgroup"), Tag = GroupMenuMark };
                rm.Click += (s, e) => RemoveFromGroup(session);
                inject.Add(rm);
            }

            inject.Add(new Separator { Tag = GroupMenuMark });
            for (int k = inject.Count - 1; k >= 0; k--) menu.Items.Insert(0, inject[k]);
        }

        private void RequestCloseSession(Session session)
        {
            if (session.Connected && _settings.ConfirmCloseConnected &&
                MessageBox.Show(string.Format(L("S.msg.closesession"), session.Server.Name), L("S.msg.closesession.title"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            CloseSession(session);
        }

        private void CloseSession(Session session)
        {
            if (session.IsTerm)
            {
                try { session.Term.DisposeTerminal(); } catch { }
                SessionContainer.Children.Remove(session.Term);
            }
            else if (session.IsVnc)
            {
                // Wyzeruj Client PRZED Close/Dispose — zakolejkowany OnVncEnded (Closed z wątku
                // roboczego) zobaczy null i stanie się no-opem, zamiast dotykać zniszczonej kontrolki.
                var vc = session.Vnc.Client;
                session.Vnc.Client = null;
                try { vc?.Close(); } catch { }
                SessionContainer.Children.Remove(session.Host);
                try { session.Host.Dispose(); } catch { }
            }
            else if (session.IsFiles)
            {
                try { session.Files.DisposePanel(); } catch { }
                SessionContainer.Children.Remove(session.Files);
            }
            else if (session.IsRest)
            {
                try { session.Rest.DisposeConsole(); } catch { }
                SessionContainer.Children.Remove(session.Rest);
            }
            else
            {
                try { session.Rdp.Disconnect(); } catch { /* nie połączona */ }
                session.Resizer?.Dispose();

                SessionContainer.Children.Remove(session.Host);
                try { session.Host.Dispose(); } catch { }   // zwalnia hosta i kontrolkę ActiveX (HWND)
            }
            DetachTab(session);              // odłącz kartę od paska / kontenera grupy (grupa serwera zostaje)
            _tabUnderline.Remove(session);
            _tabStatus.Remove(session);
            _tabName.Remove(session);
            _tabClose.Remove(session);
            _sessions.Remove(session);
            RebuildTabStrip();               // przebuduj pasek (kontenery grup + tytuły)
            PersistOpenSessions();

            // Zamknięcie panelu podziału → zakończ podział i pokaż drugi panel pojedynczo.
            Session survivingPane = session == _paneLeft ? _paneRight : (session == _paneRight ? _paneLeft : null);
            if (survivingPane != null)
            {
                _paneLeft = null; _paneRight = null;
                if (_sessions.Contains(survivingPane)) { _active = null; Activate(survivingPane); return; }
            }

            if (_active == session)
            {
                _active = null;
                if (_sessions.Count > 0) Activate(_sessions[_sessions.Count - 1]);
                else
                {
                    UpdateActiveRows();
                    UpdateToolbarEnabled();
                    UpdateToolbarMode();
                    UpdateCanvas();
                    SetStatus("—", StatusKind.Info);
                }
            }
            UpdateImmersive();
        }

        // ---------- Połączenie ----------

        private void Connect_Click(object sender, RoutedEventArgs e)
        {
            if (_active == null) return;
            var s = _active;
            s.Server.UseWindowsAccount = WinAuthCheck.IsChecked == true;
            if (!s.Server.UseWindowsAccount) s.Password = PassBox.Password;
            ConnectSession(s);
        }

        /// <summary>Łączy sesję; gdy brak poświadczeń (i nie konto Windows) — pyta o nie promptem.</summary>
        private void BeginConnect(Session s)
        {
            if (s.IsRest) return;   // REST: narzędzie bez cyklu łączenia (wysyłka per żądanie w konsoli)
            if (CanAuto(s)) ConnectSession(s);
            else PromptAndConnect(s, null);
        }

        /// <summary>Pokazuje prompt poświadczeń i po zatwierdzeniu łączy (np. „Połącz jako…" lub po błędzie logowania).</summary>
        private void PromptAndConnect(Session s, string reason)
        {
            var dlg = new CredentialPromptWindow(s.Server, s.Password, reason) { Owner = this };
            if (dlg.ShowDialog() != true) return;

            s.Server.UseWindowsAccount = false;
            // Jawne „Połącz jako…" (reason != null) = porzuć profil i użyj wpisanych poświadczeń. Prompt-fallback
            // przy braku hasła (reason == null) profil ZOSTAWIA — łączymy loginem z profilu + wpisanym hasłem.
            if (reason != null) s.Server.CredentialProfileId = "";
            s.Server.Username = dlg.EnteredUser;
            s.Server.Domain = dlg.EnteredDomain;
            s.Password = dlg.EnteredPassword;
            if (dlg.SavePassword)
            {
                s.Server.SavePassword = true;
                SaveCredential(s.Server, dlg.EnteredPassword);
            }
            else
            {
                // Odznaczenie „zapisz" ma być honorowane: usuń też ewentualny stary wpis z sejfu.
                s.Server.SavePassword = false;
                CredentialStore.Delete(s.Server.CredTarget);
            }
            PersistServers();
            if (s == _active) LoadToolbar(s);
            ConnectSession(s);
        }

        private void PassBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter && ConnectBtn.IsEnabled) Connect_Click(sender, e);
        }

        /// <summary>Łączy sesję na podstawie jej modelu (bez odczytu z formularza) — używane też z flyoutu.</summary>
        private void ConnectSession(Session s)
        {
            if (s.IsRest) return;   // REST: brak łączenia; wysyłka odbywa się w konsoli
            if (s.IsTerm) { ConnectTerm(s); return; }
            if (s.IsVnc) { ConnectVnc(s); return; }
            if (s.IsFiles) { ConnectFiles(s); return; }

            try { s.Rdp.Disconnect(); } catch { /* nie połączona */ }
            s.LoggedIn = false;

            try
            {
                RdpConnect.Apply(s.Rdp, s.Server, _settings, EffUser(s.Server), EffDomain(s.Server), s.Password);

                // RemoteApp: program/alias zamiast pełnego pulpitu (ustawiane PRZED Connect).
                try
                {
                    var rp = s.Rdp.RemoteProgram2;
                    bool useApp = !string.IsNullOrWhiteSpace(s.Server.RemoteAppProgram);
                    rp.RemoteProgramMode = useApp;
                    if (useApp)
                    {
                        rp.RemoteApplicationName = string.IsNullOrWhiteSpace(s.Server.Name)
                            ? s.Server.RemoteAppProgram.Trim() : s.Server.Name.Trim();
                        rp.RemoteApplicationProgram = s.Server.RemoteAppProgram.Trim();
                        rp.RemoteApplicationArgs = s.Server.RemoteAppArgs ?? "";
                    }
                }
                catch { /* starsza kontrolka bez RemoteProgram2 — łączymy jako pełny pulpit */ }

                s.Rdp.Connect();
                SetSessionStatus(s, string.Format(L("S.st.connecting"), s.Server.Host), StatusKind.Connecting);
            }
            catch (Exception ex)
            {
                SetSessionStatus(s, string.Format(L("S.st.exception"), ex.Message), StatusKind.Error);
            }
        }

        /// <summary>Jednorazowe (per instalacja) ostrzeżenie o braku szyfrowania: Telnet / klasyczne VNC.</summary>
        private void WarnUnencrypted(RemoteProtocol proto)
        {
            bool already = proto == RemoteProtocol.Telnet ? _settings.TelnetWarned
                         : proto == RemoteProtocol.Vnc ? _settings.VncWarned
                         : proto == RemoteProtocol.Ftp ? _settings.FtpWarned : true;
            if (already) return;

            if (proto == RemoteProtocol.Telnet) _settings.TelnetWarned = true;
            else if (proto == RemoteProtocol.Vnc) _settings.VncWarned = true;
            else _settings.FtpWarned = true;
            SettingsStore.Save(_settings);

            string k = proto == RemoteProtocol.Telnet ? "telnet" : proto == RemoteProtocol.Vnc ? "vnc" : "ftp";
            MessageBox.Show(L("S.warn." + k), L("S.warn." + k + ".title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // ---------- VNC (RemoteViewing) ----------

        /// <summary>Łączy sesję VNC: nowy VncClient, zdarzenia na wątek UI, handshake w tle (blokuje).</summary>
        private void ConnectVnc(Session s)
        {
            WarnUnencrypted(RemoteProtocol.Vnc);
            try
            {
                var client = new RemoteViewing.Vnc.VncClient();
                client.Connected += (o, e) => Dispatcher.BeginInvoke(new Action(() => { if (ReferenceEquals(s.Vnc?.Client, client)) OnVncConnected(s); }));
                client.ConnectionFailed += (o, e) => Dispatcher.BeginInvoke(new Action(() => OnVncEnded(s, client)));
                client.Closed += (o, e) => Dispatcher.BeginInvoke(new Action(() => OnVncEnded(s, client)));
                s.Vnc.Client = client;
                s.LoggedIn = false;

                char[] pw = (s.Password ?? "").ToCharArray();
                var opts = new RemoteViewing.Vnc.VncClientConnectOptions { ShareDesktop = true, Password = pw };
                opts.PasswordRequiredCallback = c => pw;   // gdy serwer poprosi — to samo hasło (puste => auth padnie)

                SetTabStatus(s, ServerStatus.Idle);
                SetSessionStatus(s, string.Format(L("S.st.connecting"), s.Server.Host), StatusKind.Connecting);
                if (s == _active) UpdateCanvas();

                string host = s.Server.Host;
                int port = s.Server.Port > 0 ? s.Server.Port : 5900;
                System.Threading.Tasks.Task.Run(() =>
                {
                    try { client.Connect(host, port, opts); }
                    catch (Exception ex)
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            SetSessionStatus(s, string.Format(L("S.st.exception"), ex.Message), StatusKind.Error);
                            OnVncEnded(s, client);
                        }));
                    }
                });
            }
            catch (Exception ex)
            {
                SetSessionStatus(s, string.Format(L("S.st.exception"), ex.Message), StatusKind.Error);
            }
        }

        private void OnVncConnected(Session s)
        {
            s.Connected = true;
            s.LoggedIn = true;
            RecordRecent(s.Server);
            ConnectionLog.Append("CONNECTED", s.Server);
            SetTabStatus(s, ServerStatus.Online);
            SetSessionStatus(s, L("S.connected"), StatusKind.Ok);
            if (s == _active) { UpdateToolbarMode(); UpdateCanvas(); try { s.Vnc.Focus(); } catch { } }
        }

        // Failed i Closed mogą przyjść oba — strażnik po tożsamości klienta wykonuje obsługę raz.
        private void OnVncEnded(Session s, RemoteViewing.Vnc.VncClient client)
        {
            if (s.Vnc == null || !ReferenceEquals(s.Vnc.Client, client)) return;
            s.Vnc.Client = null;
            bool was = s.Connected;
            s.Connected = false;
            SetTabStatus(s, ServerStatus.Offline);
            ConnectionLog.Append(was ? "DISCONNECTED" : "FAILED", s.Server);
            if (!s.Server.SavePassword) s.Password = "";
            if (was) SetSessionStatus(s, string.Format(L("S.st.disconnected"), "VNC"), StatusKind.Error);
            else if (s.StatusKind != StatusKind.Error) SetSessionStatus(s, L("S.st.disconnectedShort"), StatusKind.Error);
            if (s == _active) { UpdateToolbarMode(); UpdateCanvas(); }
        }

        // Panel plików SFTP przy aktywnej sesji SSH (przycisk folderu na pasku stanu).
        private void Files_Click(object sender, RoutedEventArgs e)
        {
            if (_active?.IsSsh == true) _active.Ssh.ToggleFiles();
        }

        private void Disconnect_Click(object sender, RoutedEventArgs e)
        {
            if (_active == null) return;
            if (_active.IsRest) return;   // REST: brak połączenia do zerwania
            if (_active.IsTerm) { _active.Term.Disconnect(); return; }
            if (_active.IsVnc) { try { _active.Vnc.Client?.Close(); } catch { } return; }
            try { _active.Rdp.Disconnect(); } catch (Exception ex) { SetSessionStatus(_active, string.Format(L("S.st.disconnecting"), ex.Message), StatusKind.Error); }
        }

        private void SendCtrlAltDel_Click(object sender, RoutedEventArgs e) => SendCtrlAltDel(_active);

        // Wysyła zdalne Ctrl+Alt+Del. OCX RDP nie ma na to scriptowalnej metody, więc — jak w mstsc — dajemy
        // klientowi Ctrl+Alt+End, który z fokusem na kontrolce tłumaczy je na zdalną sekwencję SAS.
        private void SendCtrlAltDel(Session s)
        {
            if (s == null || s.Server.Protocol != RemoteProtocol.Rdp || !s.Connected) return;
            if (s != _active && s != _paneLeft && s != _paneRight) Activate(s);   // sesja musi być widoczna
            // Fokus MUSI trafić na kontrolkę OCX, nie na przycisk WPF — inaczej globalny keybd_event poszedłby
            // w próżnię. WindowsFormsHost.Focus() + WinForms Focus() nie zawsze przenoszą fokus za granicę hosta.
            try { s.Host?.Focus(); s.Rdp.Focus(); } catch { }
            // Po przetworzeniu fokusu (Input) wstrzykujemy Ctrl↓ Alt↓ End↓ End↑ Alt↑ Ctrl↑.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // keybd_event jest globalny — wyślij TYLKO gdy nasze okno jest na pierwszym planie, inaczej
                // klawisze trafiłyby do aplikacji, na którą użytkownik zdążył przełączyć (iniekcja jest odroczona).
                if (GetForegroundWindow() != new WindowInteropHelper(this).Handle) return;
                // Natywne SetFocus na uchwyt OCX tuż przed iniekcją — najpewniejszy sposób, by klawisze
                // trafiły do sesji RDP (nasze okno jest na pierwszym planie, więc SetFocus na dziecko działa).
                try { SetFocus(s.Rdp.Handle); } catch { }
                // End = klawisz ROZSZERZONY; bez KEYEVENTF_EXTENDEDKEY bywa mylony z numpad-1 (zależnie od NumLock),
                // przez co OCX nie rozpoznaje Ctrl+Alt+End jako zdalnego SAS.
                keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
                keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
                keybd_event(VK_END, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
                keybd_event(VK_END, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        // ---------- SSH ----------

        /// <summary>Łączy sesję terminalową (SSH/Telnet/Serial): inicjalizuje xterm i transport w tle.</summary>
        private async void ConnectTerm(Session s)
        {
            // SSH wymaga loginu (nie ma odpowiednika konta Windows) — dopytaj, jeśli brak.
            if (s.IsSsh && string.IsNullOrWhiteSpace(EffUser(s.Server))) { PromptAndConnect(s, null); return; }
            if (s.Server.Protocol == RemoteProtocol.Telnet) WarnUnencrypted(RemoteProtocol.Telnet);
            try
            {
                SetTabStatus(s, ServerStatus.Idle);
                SetSessionStatus(s, string.Format(L("S.st.connecting"), s.Server.Host), StatusKind.Connecting);
                if (s == _active) UpdateCanvas();

                var (cols, rows) = await s.Term.InitAsync();
                string target = s.IsSsh
                    ? EffUser(s.Server) + "@" + s.Server.Host + ":" + s.Server.Port
                    : s.Server.Host + (s.Server.Protocol == RemoteProtocol.Serial ? " @" + s.Server.Port : ":" + s.Server.Port);
                s.Term.WriteLocal("\x1b[90m" + string.Format(L("S.st.connecting"), target) + "\x1b[0m\r\n");

                switch (s.Server.Protocol)
                {
                    case RemoteProtocol.Telnet:
                        await ((TelnetTerminalControl)s.Term).ConnectAsync(s.Server.Host, s.Server.Port);
                        break;
                    case RemoteProtocol.Serial:
                        await ((SerialTerminalControl)s.Term).ConnectAsync(s.Server.Host, s.Server.Port);
                        break;
                    default:
                        await s.Ssh.ConnectAsync(ConnectIdentity(s.Server), s.Password, cols, rows);
                        break;
                }
            }
            catch (Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException)
            {
                SetTabStatus(s, ServerStatus.Offline);
                SetSessionStatus(s, L("S.ssh.nowebview"), StatusKind.Error);
                if (s == _active) { UpdateToolbarMode(); UpdateCanvas(); }
            }
            catch (Renci.SshNet.Common.SshAuthenticationException ex)
            {
                SetTabStatus(s, ServerStatus.Offline);
                s.Term.WriteLocal("\r\n\x1b[91m" + ex.Message + "\x1b[0m\r\n");
                SetSessionStatus(s, ex.Message + "  " + L("S.st.hint.creds"), StatusKind.Error);
                if (s == _active) { UpdateToolbarMode(); UpdateCanvas(); }
            }
            catch (Exception ex)
            {
                SetTabStatus(s, ServerStatus.Offline);
                s.Term.WriteLocal("\r\n\x1b[91m" + ex.Message + "\x1b[0m\r\n");
                SetSessionStatus(s, string.Format(L("S.st.exception"), ex.Message), StatusKind.Error);
                if (s == _active) { UpdateToolbarMode(); UpdateCanvas(); }
            }
        }

        /// <summary>Zdarzenia terminala (SSH/Telnet/Serial) → stan sesji/karty (marshalowane na wątek UI).</summary>
        private void WireTermEvents(Session s)
        {
            s.Term.Connected += () => Dispatcher.BeginInvoke(new Action(() =>
            {
                s.Connected = true;
                RecordRecent(s.Server);
                ConnectionLog.Append("CONNECTED", s.Server);
                SetTabStatus(s, ServerStatus.Online);
                SetSessionStatus(s, L("S.connected"), StatusKind.Ok);
                if (s == _active) { UpdateToolbarMode(); UpdateCanvas(); s.Term.FocusTerminal(); }
            }));
            if (s.Ssh != null)
                s.Ssh.TunnelStatus += (spec, ok, err) => Dispatcher.BeginInvoke(new Action(() =>
                    s.Term.WriteLocal(ok
                        ? "\x1b[92m" + string.Format(L("S.ssh.tunnel.up"), spec) + "\x1b[0m\r\n"
                        : "\x1b[91m" + string.Format(L("S.ssh.tunnel.fail"), spec, err) + "\x1b[0m\r\n")));
            s.Term.Disconnected += reason => Dispatcher.BeginInvoke(new Action(() =>
            {
                bool was = s.Connected;
                s.Connected = false;
                SetTabStatus(s, ServerStatus.Offline);
                ConnectionLog.Append(was ? "DISCONNECTED" : "FAILED", s.Server);
                if (!s.Server.SavePassword) s.Password = "";   // jak przy RDP: hasło nie zostaje w pamięci

                string msg = string.Format(L("S.st.disconnected"),
                    string.IsNullOrWhiteSpace(reason) ? s.Server.Protocol.ToString().ToLowerInvariant() : reason);
                s.Term.WriteLocal("\r\n\x1b[91m" + msg + "\x1b[0m\r\n");
                SetSessionStatus(s, msg, StatusKind.Error);
                if (s == _active) { UpdateToolbarMode(); UpdateCanvas(); }
            }));
        }

        // Wspólne prompty klucza hosta (TOFU) i passphrase — używane przez sesje SSH i SFTP.
        private bool AskTrustHostKey(string hostPort, string fp, bool changed) => (bool)Dispatcher.Invoke(new Func<bool>(() =>
            MessageBox.Show(this,
                string.Format(L(changed ? "S.ssh.hostkey.changed" : "S.ssh.hostkey.new"), hostPort, fp),
                L("S.ssh.hostkey.title"), MessageBoxButton.YesNo,
                changed ? MessageBoxImage.Warning : MessageBoxImage.Question,
                changed ? MessageBoxResult.No : MessageBoxResult.Yes) == MessageBoxResult.Yes));

        private string AskKeyPassphrase(string path) => (string)Dispatcher.Invoke(new Func<string>(() =>
        {
            var dlg = new InputDialog(L("S.ssh.keypass.title"),
                string.Format(L("S.ssh.keypass.label"), System.IO.Path.GetFileName(path)),
                "", masked: true) { Owner = this };
            return dlg.ShowDialog() == true ? dlg.Value : null;
        }));

        // TOFU certyfikatu FTPS — ten sam wzorzec co AskTrustHostKey (SSH), inny magazyn (FtpsCertPinning).
        private bool AskTrustFtpsCert(string hostPort, string fp, bool changed) => (bool)Dispatcher.Invoke(new Func<bool>(() =>
            MessageBox.Show(this,
                string.Format(L(changed ? "S.ftps.cert.changed" : "S.ftps.cert.new"), hostPort, fp),
                L("S.ftps.cert.title"), MessageBoxButton.YesNo,
                changed ? MessageBoxImage.Warning : MessageBoxImage.Question,
                changed ? MessageBoxResult.No : MessageBoxResult.Yes) == MessageBoxResult.Yes));

        // SFTP jako osobny protokół: panel łączy się leniwie; identyczność (login z profilu) + hasło ustawiamy tutaj.
        private void ConnectFiles(Session s)
        {
            bool anon = s.Server.Protocol == RemoteProtocol.Ftp && s.Server.FtpAnonymous;
            if (!anon && string.IsNullOrWhiteSpace(EffUser(s.Server))) { PromptAndConnect(s, null); return; }
            if (s.Server.Protocol == RemoteProtocol.Ftp && s.Server.FtpEncryption == 2) WarnUnencrypted(RemoteProtocol.Ftp);
            SetTabStatus(s, ServerStatus.Idle);
            SetSessionStatus(s, string.Format(L("S.st.connecting"), s.Server.Host), StatusKind.Connecting);
            if (s == _active) UpdateCanvas();
            s.FilesConn.SetIdentity(ConnectIdentity(s.Server), s.Password);
            s.Files.RefreshAsync();   // łączy leniwie; Connected/Failed aktualizują kartę i status
        }

        /// <summary>Zdarzenia panelu plików (SFTP/FTP) → stan sesji/karty (marshalowane na wątek UI).</summary>
        private void WireFilesEvents(Session s)
        {
            s.Files.Connected += () => Dispatcher.BeginInvoke(new Action(() =>
            {
                s.Connected = true;
                RecordRecent(s.Server);
                ConnectionLog.Append("CONNECTED", s.Server);
                SetTabStatus(s, ServerStatus.Online);
                SetSessionStatus(s, L("S.connected"), StatusKind.Ok);
                if (s == _active) { UpdateToolbarMode(); UpdateCanvas(); }
            }));
            s.Files.Failed += reason => Dispatcher.BeginInvoke(new Action(() =>
            {
                bool was = s.Connected;
                s.Connected = false;
                SetTabStatus(s, ServerStatus.Offline);
                ConnectionLog.Append(was ? "DISCONNECTED" : "FAILED", s.Server);
                if (!s.Server.SavePassword) s.Password = "";   // hasło nie zostaje w pamięci (jak przy RDP/SSH)
                SetSessionStatus(s, string.Format(L("S.st.disconnected"),
                    string.IsNullOrWhiteSpace(reason) ? "sftp" : reason), StatusKind.Error);
                if (s == _active) { UpdateToolbarMode(); UpdateCanvas(); }
            }));
        }

        private void WireEvents(Session s)
        {
            s.Rdp.OnConnecting += (o, a) =>
            {
                SetSessionStatus(s, L("S.st.connectingShort"), StatusKind.Connecting);
                SetTabStatus(s, ServerStatus.Idle);
                if (s == _active) UpdateCanvas();
            };
            s.Rdp.OnConnected += (o, a) =>
            {
                s.Connected = true;
                RecordRecent(s.Server);
                ConnectionLog.Append("CONNECTED", s.Server);
                SetTabStatus(s, ServerStatus.Online);
                SetSessionStatus(s, L("S.connected"), StatusKind.Ok);
                if (s == _active) { UpdateToolbarMode(); UpdateCanvas(); }
            };
            s.Rdp.OnLoginComplete += (o, a) =>
            {
                s.LoggedIn = true;
                s.Resizer?.ApplyInitial();
                // Odśwież kanwę także dla panelu podziału (nie tylko aktywnej sesji), by po zalogowaniu stał się widoczny.
                if (s == _active || s == _paneLeft || s == _paneRight) { UpdateToolbarMode(); UpdateCanvas(); try { s.Rdp.Focus(); } catch { } }
            };
            s.Rdp.OnDisconnected += (o, a) =>
            {
                bool wasLoggedIn = s.LoggedIn;
                s.Connected = false;
                s.LoggedIn = false;
                SetTabStatus(s, ServerStatus.Offline);

                ConnectionLog.Append(wasLoggedIn ? "DISCONNECTED" : "FAILED", s.Server);

                // Bezpieczeństwo: nie trzymaj hasła w pamięci po rozłączeniu, jeśli nie jest
                // zapisane w Credential Managerze. Ponowne połączenie wymaga wpisania go na nowo.
                if (!s.Server.SavePassword) s.Password = "";

                string msg = string.Format(L("S.st.disconnected"), DescribeDisconnect(s.Rdp, a.discReason));
                if (!wasLoggedIn)
                {
                    msg += "  " + (s.Server.UseWindowsAccount
                        ? L("S.st.hint.winauth")
                        : L("S.st.hint.creds"));
                }
                SetSessionStatus(s, msg, StatusKind.Error);
                if (s == _active) { UpdateToolbarMode(); UpdateCanvas(); }
            };
            s.Rdp.OnFatalError += (o, a) =>
            {
                s.Connected = false;
                SetTabStatus(s, ServerStatus.Offline);
                SetSessionStatus(s, string.Format(L("S.st.fatal"), a.errorCode), StatusKind.Error);
                if (s == _active) { UpdateToolbarMode(); UpdateCanvas(); }
            };
            // Fullscreen kontrolki (ścieżka multimon) — tylko komunikaty statusu.
            s.Rdp.OnEnterFullScreenMode += (o, a) =>
                SetSessionStatus(s, L("S.st.multimon"), StatusKind.Info);
            s.Rdp.OnLeaveFullScreenMode += (o, a) =>
                SetSessionStatus(s, L("S.connected"), StatusKind.Ok);
        }

        private void SetTabStatus(Session s, ServerStatus status)
        {
            if (_tabStatus.TryGetValue(s, out var dot)) dot.Fill = StatusBrush(status);
        }

        // ---------- Konto Windows ----------

        private void WinAuth_Changed(object sender, RoutedEventArgs e) => UpdatePassVisibility();

        private bool ActiveHasNoCreds()
            => _active != null && (_active.Server.Protocol == RemoteProtocol.Telnet
                                   || _active.Server.Protocol == RemoteProtocol.Serial);

        private void UpdatePassVisibility()
        {
            CfPassGroup.Visibility = (ActiveHasNoCreds() || WinAuthCheck.IsChecked == true)
                ? Visibility.Collapsed : Visibility.Visible;
        }

        // ---------- Pasek sesji: dwa stany ----------

        private void UpdateToolbarMode()
        {
            // W pełnym ekranie widocznością paska/zakładek steruje Enter/ExitFullscreen — nie dotykamy jej tutaj.
            if (!_isFullscreen)
            {
                bool has = _active != null;
                bool conn = has && _active.Connected;
                bool immersive = IsImmersive();
                // Pasek połączenia (z hasłem) tylko PRZED połączeniem; po połączeniu — brak paska (więcej miejsca),
                // a akcje sesji przenoszą się na prawy koniec paska kart (SessionActions). W skupieniu: FocusControls.
                SessionToolbar.Visibility = (has && !immersive && !conn) ? Visibility.Visible : Visibility.Collapsed;
                SessionActions.Visibility = (conn && !immersive) ? Visibility.Visible : Visibility.Collapsed;
                TabStripHost.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            }
            if (_active == null) return;

            FilesBtn.Visibility = _active.IsSsh ? Visibility.Visible : Visibility.Collapsed;   // SFTP tylko dla SSH
            CadBtn.Visibility = _active.Server.Protocol == RemoteProtocol.Rdp ? Visibility.Visible : Visibility.Collapsed;   // Ctrl+Alt+Del tylko dla RDP
        }

        private void UpdateToolbarEnabled()
        {
            bool has = _active != null;
            WinAuthCheck.IsEnabled = has;
            PassBox.IsEnabled = has;
            ConnectBtn.IsEnabled = has;
        }

        // ---------- Pełny ekran ----------

        // Otwiera serwer w OSOBNYM oknie sesji (model jak mstsc — kontrolka żyje w tym oknie na stałe).
        // Domyślne otwieranie idzie do zakładki w oknie głównym; to jest opcja na drugi monitor.
        // password != null → przenosimy hasło z zakładki przy „wyciąganiu" (bez ponownego pytania).
        private void OpenInNewWindow(ServerInfo server, string password = null)
        {
            if (server == null) return;
            RecordRecent(server);
            string pw = password ?? "";
            if (string.IsNullOrEmpty(pw) && EffSavedPw(server)) CredentialStore.TryRead(EffCredTarget(server), out pw);
            var win = new SessionWindow(server, _settings, pw, EffUser(server), EffDomain(server), PersistServers, DockSessionFromWindow);
            _sessionWindows.Add(win);
            win.Closed += (s, e) => _sessionWindows.Remove(win);
            win.Show();
            win.Activate();
        }

        /// <summary>„Wyciąga" zakładkę do osobnego okna: zamyka kartę i otwiera okno sesji tego serwera
        /// (RDP łączy ponownie — wraca do tej samej sesji po stronie serwera). Przenosi hasło z pamięci.</summary>
        private void TearOffToWindow(Session s)
        {
            if (s == null) return;
            var server = s.Server;
            var pw = s.Password;
            CloseSession(s);
            OpenInNewWindow(server, pw);
        }

        /// <summary>„Dokuje" okno sesji z powrotem jako kartę w managerze (callback wołany z SessionWindow):
        /// otwiera nową kartę tego serwera i łączy (reconnect wznawia sesję serwera). Hasło przeniesione z okna.</summary>
        private void DockSessionFromWindow(ServerInfo server, string password)
        {
            OpenServer(server, autoConnect: false, forceNew: true);
            if (_active != null && _active.Server == server)
            {
                _active.Password = password;
                ConnectSession(_active);
            }
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();   // Window.Activate — wysuń manager na wierzch
        }

        private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

        private void ToggleFullscreen()
        {
            if (_active == null && !_isFullscreen) return;
            if (!_isFullscreen) EnterFullscreen();
            else ExitFullscreen();
        }

        private void EnterFullscreen()
        {
            _prevStyle = WindowStyle;
            _prevState = WindowState;
            _prevResize = ResizeMode;
            _prevTopmost = Topmost;
            _prevLeft = Left; _prevTop = Top; _prevWidth = Width; _prevHeight = Height;
            _prevScale = RootScale.ScaleX;
            RootScale.ScaleX = RootScale.ScaleY = 1.0;   // zdalny pulpit ostro 1:1 w pełnym ekranie
            _isFullscreen = true;   // wcześnie: StateChanged w trakcie przełączania nie ruszy trybu skupienia

            AppTitleBar.Visibility = Visibility.Collapsed;
            Rail.Visibility = Visibility.Collapsed;
            Sidebar.Visibility = Visibility.Collapsed;
            TabStripHost.Visibility = Visibility.Collapsed;
            SessionToolbar.Visibility = Visibility.Collapsed;
            SessionHotZoneRow.Height = new GridLength(0);   // host wypełnia CAŁY monitor → rozdzielczość 1:1

            WindowState = WindowState.Normal;   // trzeba być Normal, żeby ręcznie ustawić granice
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;

            // Pełny ekran na monitorze, na którym stoi okno (na inny ekran = osobne okno + przeciągnięcie).
            // SetWindowPos jest poprawny także między monitorami o różnym DPI; zakrywa pasek zadań.
            var hwnd = new WindowInteropHelper(this).Handle;
            var screen = System.Windows.Forms.Screen.FromHandle(hwnd);
            var b = screen.Bounds;
            SetWindowPos(hwnd, IntPtr.Zero, b.Left, b.Top, b.Width, b.Height, SWP_SHOWWINDOW);
            Topmost = true;

            _fsCursorPoll.Start();

            // Rozdzielczość dokładnie = natywne piksele monitora (jak w oknie sesji) — deterministycznie, bez wyścigu DPI.
            IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (GetMonitorInfo(mon, ref mi))
            {
                _fsMonRect = mi.rcMonitor;
                int pw = mi.rcMonitor.right - mi.rcMonitor.left, ph = mi.rcMonitor.bottom - mi.rcMonitor.top;
                var sess = _active;
                Dispatcher.BeginInvoke(new Action(() => { if (_isFullscreen) sess?.Resizer?.ApplyExact(pw, ph); }), DispatcherPriority.Background);
            }
            else
            {
                _fsMonRect = new RECT { left = b.Left, top = b.Top, right = b.Right, bottom = b.Bottom };
            }
        }

        private void ExitFullscreen()
        {
            _fsCursorPoll.Stop();
            _fsBarDelay.Stop();
            RootScale.ScaleX = RootScale.ScaleY = _prevScale;
            Topmost = _prevTopmost;
            WindowStyle = _prevStyle;
            ResizeMode = _prevResize;
            Left = _prevLeft; Top = _prevTop; Width = _prevWidth; Height = _prevHeight;
            WindowState = _prevState;

            AppTitleBar.Visibility = Visibility.Visible;
            Rail.Visibility = Visibility.Visible;
            Sidebar.Visibility = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;   // respektuj ręczne zwinięcie
            TabStripHost.Visibility = Visibility.Visible;
            SessionToolbar.Visibility = Visibility.Visible;
            SessionHotZoneRow.Height = new GridLength(0);   // brak odstępu pod paskiem kart (kotwica popupu ma 0)

            FsPopup.IsOpen = false;
            _isFullscreen = false;
            _fsPinned = false;
            PinBtn.Content = L("S.fs.pin");
            UpdateImmersive();
        }

        /// <summary>
        /// Prostokąt pełnego ekranu w DIP: bieżący monitor albo — przy span — cały wirtualny
        /// pulpit (wszystkie monitory). Zapisuje też prostokąt w pikselach do pollingu krawędzi.
        /// </summary>
        private bool TryGetFullscreenRectDip(bool allMonitors, out Rect rect)
        {
            rect = default;
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return false;

            RECT r;
            if (allMonitors)
            {
                r.left = GetSystemMetrics(SM_XVIRTUALSCREEN);
                r.top = GetSystemMetrics(SM_YVIRTUALSCREEN);
                r.right = r.left + GetSystemMetrics(SM_CXVIRTUALSCREEN);
                r.bottom = r.top + GetSystemMetrics(SM_CYVIRTUALSCREEN);
                if (r.right <= r.left || r.bottom <= r.top) return false;
            }
            else
            {
                IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (!GetMonitorInfo(mon, ref mi)) return false;
                r = mi.rcMonitor;
            }
            _fsMonRect = r;   // piksele — do pollingu górnej krawędzi (działa też dla span)

            var src = PresentationSource.FromVisual(this);
            if (src?.CompositionTarget == null) return false;
            Matrix toDip = src.CompositionTarget.TransformFromDevice;

            Point tl = toDip.Transform(new Point(r.left, r.top));
            Point br = toDip.Transform(new Point(r.right, r.bottom));
            rect = new Rect(tl, br);
            return true;
        }

        /// <summary>Liczba monitorów w systemie (bramkuje ścieżkę multimon).</summary>
        private static int MonitorCount()
        {
            try { return System.Windows.Forms.Screen.AllScreens.Length; }
            catch { return 1; }
        }

        private const int MONITOR_DEFAULTTONEAREST = 0x2;
        private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77,
                          SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

        private const uint SWP_SHOWWINDOW = 0x0040;

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        private const byte VK_CONTROL = 0x11, VK_MENU = 0x12, VK_END = 0x23;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
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

        // Polling kursora w pełnym ekranie: łapie SAMĄ górną krawędź (y=0), niezależnie od
        // nibeklienckiego brzegu okna i od "airspace" kontrolki RDP.
        private void FsCursorPollTick(object sender, EventArgs e)
        {
            if (!_isFullscreen) { _fsCursorPoll.Stop(); return; }
            if (WindowState == WindowState.Minimized) return;   // zminimalizowane — nie pokazuj paska
            if (_fsPinned) { if (!FsPopup.IsOpen) FsPopup.IsOpen = true; return; }   // przypięty: zawsze widoczny
            if (!GetCursorPos(out POINT p)) return;

            bool withinX = p.X >= _fsMonRect.left && p.X < _fsMonRect.right;
            bool atTop = withinX && p.Y <= _fsMonRect.top + 2;

            if (atTop)
            {
                // Nie od razu — dopiero gdy kursor chwilę postoi przy krawędzi (jak w mstsc).
                if (!FsPopup.IsOpen && !_fsBarDelay.IsEnabled) _fsBarDelay.Start();
            }
            else
            {
                if (_fsBarDelay.IsEnabled) _fsBarDelay.Stop();
                // zabezpieczenie: gdy kursor zjedzie wyraźnie poniżej paska (a flyout zwinięty) — zamknij
                if (FsPopup.IsOpen && FsFlyout.Visibility != Visibility.Visible && p.Y > _fsMonRect.top + 140)
                    FsPopup.IsOpen = false;
            }
        }

        private void FsPopup_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_fsPinned) return;   // przypięty pasek nie chowa się po zjechaniu myszą
            FsPopup.IsOpen = false;
            CollapseFlyout();
        }

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

        private void TogglePin_Click(object sender, RoutedEventArgs e)
        {
            _fsPinned = !_fsPinned;
            PinBtn.Content = _fsPinned ? L("S.fs.pinned") : L("S.fs.pin");
            if (_fsPinned) FsPopup.IsOpen = true;   // przypięty pasek pozostaje widoczny
        }

        private void TabScroller_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) return;   // Ctrl+kółko = zoom
            if (sender is ScrollViewer sv)
            {
                sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
                e.Handled = true;
            }
        }

        private void CollapseFlyout()
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
            if (e.Key == Key.F11) { ToggleFullscreen(); return; }
            if (e.Key == Key.Escape && _isFullscreen) { ToggleFullscreen(); return; }

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

        private void BuildEmptyRecent()
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
        private void ImportRdpFiles(IEnumerable<string> paths)
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

        // Upuszczenie plików .rdp z Eksploratora na drzewo serwerów. Wewnętrzny drag kolejności używa
        // typeof(ServerInfo) (patrz WireServerRow), więc pliki się z nim nie gryzą — bąbelkują tutaj.
        private void WireTreeFileDrop()
        {
            ServerTree.Background = Brushes.Transparent;   // hit-test także w pustym obszarze drzewa
            ServerTree.AllowDrop = true;
            ServerTree.DragOver += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; e.Handled = true; }
            };
            ServerTree.Drop += (s, e) =>
            {
                if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
                if (!(e.Data.GetData(DataFormats.FileDrop) is string[] files)) return;
                var rdps = files.Where(f => f.EndsWith(".rdp", StringComparison.OrdinalIgnoreCase)).ToArray();
                ImportRdpFiles(rdps);
                e.Handled = true;
            };
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

        private void ExportRdp(ServerInfo server)
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
        private void DuplicateServer(ServerInfo src)
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

        private void EditServer(ServerInfo server)
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

        private void DeleteServer(ServerInfo server)
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
        private ContextMenu BuildBulkContextMenu(List<ServerInfo> servers)
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

        private void PersistServers() => ServerRepository.Save(_vm.Servers.ToList());

        private void SaveCredential(ServerInfo server, string password)
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
        private Brush AvatarBrush(ServerInfo s)
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

        private static string ServerInitials(ServerInfo s) => RdpUtils.MakeInitials(s?.Name);

        private void CopyToClipboard(string text)
        {
            if (string.IsNullOrEmpty(text)) { SetStatus(L("S.st.copyempty"), StatusKind.Ok); return; }
            try { Clipboard.SetText(text); SetStatus(L("S.st.copied"), StatusKind.Ok); }
            catch { /* schowek chwilowo zajęty przez inny proces */ }
        }

        private static string ReadPassword(ServerInfo s)
            => CredentialStore.TryRead(s.CredTarget, out var p) ? (p ?? "") : "";

        // Jak ReadPassword, ale z EFEKTYWNEGO celu (profil poświadczeń albo własny) — do kopiowania z menu.
        private string ReadEffPassword(ServerInfo s)
            => CredentialStore.TryRead(EffCredTarget(s), out var p) ? (p ?? "") : "";

        // ---------- Profile poświadczeń ----------
        // Serwer może wskazywać współdzielony profil (CredentialProfileId). Gdy wskazuje, login/domena/hasło
        // przy łączeniu pochodzą z PROFILU, nie z pól serwera. Poniższe „Eff*" rozwiązują to w jednym miejscu.
        private CredentialProfile ProfileFor(ServerInfo s)
            => string.IsNullOrEmpty(s?.CredentialProfileId) ? null
               : _credProfiles.FirstOrDefault(p => p.Id == s.CredentialProfileId);

        private string EffUser(ServerInfo s)       { var p = ProfileFor(s); return p != null ? p.Username : s.Username; }
        private string EffDomain(ServerInfo s)     { var p = ProfileFor(s); return p != null ? p.Domain   : s.Domain; }
        private string EffCredTarget(ServerInfo s) { var p = ProfileFor(s); return p != null ? p.CredTarget : s.CredTarget; }
        private bool   EffSavedPw(ServerInfo s)    { var p = ProfileFor(s); return p != null || s.SavePassword; }

        // Tożsamość do łączenia: kontrolka SSH czyta server.Username WEWNĘTRZNIE (auth + SFTP), więc gdy jest
        // profil, podajemy płytką kopię serwera z podmienionym loginem/domeną (transient) — bez ruszania kodu
        // uwierzytelniania w SshTerminalControl. Dla RDP ustawiamy UserName/Domain wprost (EffUser/EffDomain).
        private ServerInfo ConnectIdentity(ServerInfo s)
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

        private Brush GroupDotBrush(string group)
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

        private Brush StatusBrush(ServerStatus status)
        {
            switch (status)
            {
                case ServerStatus.Online: return Res("Online");
                case ServerStatus.Idle: return Res("Idle");
                default: return Res("Offline");
            }
        }

        /// <summary>Tekstowy odpowiednik statusu (dla czytników ekranu — status nie tylko kolorem).</summary>
        private static string StatusLabel(ServerStatus status)
        {
            switch (status)
            {
                case ServerStatus.Online: return LocalizationManager.S("S.status.online");
                case ServerStatus.Idle: return LocalizationManager.S("S.status.idle");
                default: return LocalizationManager.S("S.status.offline");
            }
        }

        // ---------- Osiągalność serwerów (kropki statusu w drzewie) ----------

        private async void CheckReachabilityAsync()
        {
            if (_reachBusy) return;
            _reachBusy = true;
            try
            {
                var servers = _vm.Servers.ToList();
                var results = await Task.WhenAll(servers.Select(async srv =>
                {
                    // Serial (COM), WWW i REST (URL) — sonda TCP host:port nie ma sensu, zostaw bieżący status/opóźnienie.
                    var r = srv.Protocol == RemoteProtocol.Serial || srv.Protocol == RemoteProtocol.Http || srv.Protocol == RemoteProtocol.Rest
                        ? (srv.Status, srv.LatencyMs) : await ProbeAsync(srv.Host, srv.Port);
                    return new KeyValuePair<ServerInfo, (ServerStatus status, int rttMs)>(srv, r);
                }));

                foreach (var kv in results)
                {
                    kv.Key.Status = kv.Value.status;
                    kv.Key.LatencyMs = kv.Value.rttMs;
                    if (_serverStatusDot.TryGetValue(kv.Key, out var dot))
                        dot.Fill = StatusBrush(kv.Value.status);
                    if (_serverLatency.TryGetValue(kv.Key, out var lat))
                        lat.Text = RdpUtils.FormatLatency(kv.Value.rttMs);
                }
                // Zapamiętaj średnie opóźnienie osiągalnych hostów z tego cyklu — do wykresu na pulpicie.
                var reachable = results.Where(kv => kv.Value.rttMs >= 0).Select(kv => (double)kv.Value.rttMs).ToList();
                if (reachable.Count > 0)
                {
                    _latencySamples.Add(reachable.Average());
                    if (_latencySamples.Count > 48) _latencySamples.RemoveAt(0);
                }
                _vm.RaiseCounts();   // odśwież liczniki pulpitu (Osiągalne)
            }
            catch
            {
                // problemy sieciowe nie mogą wywrócić UI
            }
            finally
            {
                _reachBusy = false;
            }
        }

        // Wake-on-LAN: magic packet broadcastem na podstawie MAC z ustawień serwera.
        private void WakeServer(ServerInfo server)
        {
            if (!Core.WakeOnLan.TryParseMac(server.MacAddress, out var mac))
            {
                MessageBox.Show(string.Format(L("S.se.mac.bad"), server.MacAddress),
                    L("S.m.wol"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                Core.WakeOnLan.Send(mac);
                SetStatus(string.Format(L("S.st.wolsent"), server.Name), StatusKind.Ok);
            }
            catch (Exception ex)
            {
                SetStatus(string.Format(L("S.st.exception"), ex.Message), StatusKind.Error);
            }
        }

        private async void DiagnoseServer(ServerInfo server)
        {
            string host = server.Host;
            int port = server.Port;
            if (string.IsNullOrWhiteSpace(host))
            {
                MessageBox.Show(L("S.msg.diag.nohost"), L("S.msg.diag.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SetStatus(string.Format(L("S.st.diagnosing"), host, port), StatusKind.Connecting);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var probe = await ProbeAsync(host, port);
            sw.Stop();
            bool ok = probe.status == ServerStatus.Online;
            long elapsed = ok && probe.rttMs >= 0 ? probe.rttMs : sw.ElapsedMilliseconds;

            string msg = RdpUtils.FormatDiagnostics(host, port, ok, elapsed,
                L("S.diag.open"), L("S.diag.closed"));
            SetStatus(msg, ok ? StatusKind.Ok : StatusKind.Error);
            MessageBox.Show(msg, string.Format(L("S.msg.diag.titlefmt"), server.Name ?? host),
                MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        // Limit jednoczesnych sond — setki serwerów naraz zalewałyby pulę wątków/gniazd (A4 z przeglądu).
        private static readonly SemaphoreSlim ProbeConcurrency = new SemaphoreSlim(32);

        // Zwraca status oraz zmierzone opóźnienie połączenia TCP (ms); rttMs = -1 gdy nieosiągalny/nieznany.
        // Limit czasu sondy (ms) — z Ustawień → Połączenie (ProbeTimeoutSeconds). Domyślnie 1500 do czasu
        // wczytania ustawień; aktualizowany w ApplySettings/Window_Loaded.
        private static int _probeTimeoutMs = 1500;

        private static async Task<(ServerStatus status, int rttMs)> ProbeAsync(string host, int port)
        {
            if (string.IsNullOrWhiteSpace(host)) return (ServerStatus.Offline, -1);
            await ProbeConcurrency.WaitAsync();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using (var c = new TcpClient())
                {
                    // Task.WaitAsync (nie blokujący wątek WaitOne) — Dispose przy timeout/wyjątku ubija
                    // wciąż-trwające ConnectAsync pod spodem (zamknięcie gniazda przerywa próbę połączenia).
                    await c.ConnectAsync(host, port).WaitAsync(TimeSpan.FromMilliseconds(_probeTimeoutMs));
                    sw.Stop();
                    return c.Connected ? (ServerStatus.Online, (int)sw.ElapsedMilliseconds) : (ServerStatus.Offline, -1);
                }
            }
            catch
            {
                return (ServerStatus.Offline, -1);   // timeout (TimeoutException) albo błąd połączenia
            }
            finally { ProbeConcurrency.Release(); }
        }

        private static string DescribeDisconnect(AxMsRdpClient11NotSafeForScripting rdp, int reason)
        {
            uint ext = 0;
            try { ext = (uint)rdp.ExtendedDisconnectReason; } catch { }
            string d = null;
            try { d = rdp.GetErrorDescription((uint)reason, ext); } catch { }
            return RdpUtils.FormatDisconnectReason(d, reason, ext);
        }

        private void SetSessionStatus(Session s, string text, StatusKind kind = StatusKind.Info)
        {
            s.Status = text;
            s.StatusKind = kind;
            if (s == _active) SetStatus(text, kind);
        }

        private void SetStatus(string text, StatusKind kind = StatusKind.Info)
        {
            StatusText.Text = text;
            StatusText.ToolTip = (string.IsNullOrEmpty(text) || text == "—") ? null : text;
            var b = KindBrush(kind);
            StatusText.Foreground = b;
            CfStatusDot.Fill = b;
            CfStatusDot.Visibility = kind == StatusKind.Info ? Visibility.Collapsed : Visibility.Visible;
        }

        private Brush KindBrush(StatusKind kind)
        {
            switch (kind)
            {
                case StatusKind.Connecting: return Res("Idle");
                case StatusKind.Ok: return Res("Online");
                case StatusKind.Error: return Res("Danger");
                default: return Res("TextSec");
            }
        }
    }
}
