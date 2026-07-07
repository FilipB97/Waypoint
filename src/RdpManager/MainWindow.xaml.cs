using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
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
        private readonly Dictionary<Session, Rectangle> _tabUnderline = new Dictionary<Session, Rectangle>();
        private readonly Dictionary<Session, Ellipse> _tabStatus = new Dictionary<Session, Ellipse>();
        private readonly Dictionary<Session, TextBlock> _tabName = new Dictionary<Session, TextBlock>();
        private readonly Dictionary<Session, TextBlock> _tabClose = new Dictionary<Session, TextBlock>();
        // Grupy kart (stosy jak w Vivaldi). Przynależność po Id serwera (w TabGroup.ServerIds), więc
        // grupy zapisują się do ustawień i wracają po restarcie. Runtime-lista niżej ładowana z _settings.
        private readonly List<TabGroup> _tabGroups = new List<TabGroup>();
        private readonly MainViewModel _vm = new MainViewModel();

        private AppSettings _settings = new AppSettings();

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

        /// <summary>Skrót do pędzla z zasobów motywu; null gdy brak (te same semantyki co dotychczasowy rzut).</summary>
        private Brush Res(string key) => TryFindResource(key) as Brush;

        // Otwarte, samodzielne okna sesji (model wielookienny).
        private readonly System.Collections.Generic.List<SessionWindow> _sessionWindows = new System.Collections.Generic.List<SessionWindow>();

        // Opóźnienie pojawienia się paska pełnoekranowego (jak w mstsc) + polling pozycji kursora.
        private DispatcherTimer _fsBarDelay;
        private DispatcherTimer _fsCursorPoll;
        private DispatcherTimer _focusPeekPoll;    // wykrywa najechanie na lewą krawędź w trybie skupienia
        private DispatcherTimer _focusPeekDelay;   // opóźnienie przytrzymania (jak pasek pełnoekranowy)
        private bool _focusPeeking;                // panel boczny chwilowo wysunięty w trybie skupienia
        private bool? _focusOverride;              // ręczne wł/wył skupienia (null = wg ustawienia); reset po un-maximize
        private Core.UpdateCheck.ReleaseInfo _update;      // dostępna nowsza wersja (z URL assetu .exe); null gdy brak
        private bool _updating;                            // trwa auto-aktualizacja — pomiń potwierdzenie zamknięcia
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
            _prevRunVersion = _settings.LastRunVersion ?? "";           // zapamiętaj wersję sprzed tego startu
            var curVer = CurrentVersion().ToString();
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

            CheckForUpdatesAsync();
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
                    case Core.HealthNoticeKind.SettingsRestoredAfterUpdate: lines.Add(L("S.heal.settingsAfterUpdate")); break;
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

        // ---------- Aktualizacje ----------

        // Ciche sprawdzenie GitHub releases/latest; nowsza wersja → przycisk w panelu bocznym.
        // Bez sieci / rate limitu / złego JSON-a — po prostu nic się nie pokazuje.
        private string _prevRunVersion = "";   // wersja z poprzedniego startu (do wykrycia „właśnie zaktualizowano")

        private static Version CurrentVersion()
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }

        private async void CheckForUpdatesAsync()
        {
            if (!_settings.CheckUpdates) return;
            try
            {
                using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(6) })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("Waypoint");
                    string json = await http.GetStringAsync(
                        "https://api.github.com/repos/FilipB97/Waypoint/releases/latest");
                    var info = Core.UpdateCheck.ParseRelease(json);
                    if (info == null) return;
                    var current = CurrentVersion();
                    if (Core.UpdateCheck.IsNewer(info.Version, current))
                    {
                        _update = info;
                        UpdateBtn.Content = string.Format(L("S.update.available"), info.Version);
                        UpdateBtn.Visibility = Visibility.Visible;
                    }
                    else if (Core.UpdateCheck.ParseTag(_prevRunVersion) is Version prev && prev < current
                             && !string.IsNullOrWhiteSpace(info.Notes))
                    {
                        // Wersja wzrosła od ostatniego startu → właśnie zaktualizowano: pokaż „co nowego" (raz).
                        new ReleaseNotesWindow(string.Format(L("S.update.whatsnew"), current),
                            current, info.Notes, info.HtmlUrl, confirm: false) { Owner = this }.ShowDialog();
                    }
                }
            }
            catch { /* offline / proxy / rate limit — sprawdzimy przy kolejnym starcie */ }
        }

        private async void Update_Click(object sender, RoutedEventArgs e)
        {
            // Brak assetu .exe w release → tak jak dawniej: otwórz stronę wydania w przeglądarce.
            if (_update == null || string.IsNullOrEmpty(_update.ExeUrl))
            {
                OpenReleasePage();
                return;
            }

            // Pokaż changelog i potwierdzenie PRZED aktualizacją (zamiast zwykłego MessageBox).
            var notesDlg = new ReleaseNotesWindow(string.Format(L("S.update.newtitle"), _update.Version),
                _update.Version, _update.Notes, _update.HtmlUrl, confirm: true) { Owner = this };
            if (notesDlg.ShowDialog() != true) return;

            string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "Waypoint-update-" + _update.Version + ".exe");
            string label = UpdateBtn.Content as string;
            UpdateBtn.IsEnabled = false;
            try
            {
                await DownloadFileAsync(_update.ExeUrl, temp, _update.ExeSize);
                if (!IsValidExe(temp, _update.ExeSize)) throw new Exception("plik pobrany niepoprawnie");
            }
            catch (Exception ex)
            {
                UpdateBtn.IsEnabled = true;
                UpdateBtn.Content = label;
                MessageBox.Show(L("S.update.faildl") + "\n" + ex.Message, L("S.update.title"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Weryfikacja wydawcy (Authenticode „publisher pinning"): pobrany plik musi być podpisany
            // tym samym certyfikatem, co bieżąca aplikacja. Odrzucamy podmieniony/niepodpisany plik.
            var verdict = Core.CodeSign.VerifyPublisher(temp, Environment.ProcessPath);
            if (!Core.CodeSign.IsAcceptable(verdict))
            {
                try { System.IO.File.Delete(temp); } catch { }
                UpdateBtn.IsEnabled = true;
                UpdateBtn.Content = label;
                MessageBox.Show(L("S.update.badsig"), L("S.update.title"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Migawka ustawień PRZED podmianą exe (z pamięci — źródło prawdy); po restarcie na nową wersję
            // ConsumeUpdateSnapshot (w Window_Loaded) przywróci je, nawet jeśli settings.json ucierpi w trakcie.
            SettingsStore.SnapshotForUpdate(_settings);

            // Uruchom pobrany exe jako „installer": poczeka aż ten proces zniknie, podmieni plik docelowy i wystartuje go.
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(temp)
                {
                    UseShellExecute = false,
                    Arguments = "--apply-update \"" + Environment.ProcessPath + "\" "
                                + System.Diagnostics.Process.GetCurrentProcess().Id
                });
            }
            catch (Exception ex)
            {
                UpdateBtn.IsEnabled = true;
                UpdateBtn.Content = label;
                MessageBox.Show(L("S.update.faildl") + "\n" + ex.Message, L("S.update.title"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _updating = true;   // Window_Closing pominie potwierdzenie i zapisze otwarte sesje do przywrócenia
            Close();
        }

        private void OpenReleasePage()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    _update?.HtmlUrl ?? "https://github.com/FilipB97/Waypoint/releases/latest") { UseShellExecute = true });
            }
            catch { /* brak przeglądarki — ignoruj */ }
        }

        // Pobiera plik strumieniowo z paskiem % na przycisku aktualizacji (kontynuacje async wracają na wątek UI).
        private async System.Threading.Tasks.Task DownloadFileAsync(string url, string dest, long knownSize)
        {
            using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Waypoint");
                using (var resp = await http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    long total = resp.Content.Headers.ContentLength ?? knownSize;
                    using (var src = await resp.Content.ReadAsStreamAsync())
                    using (var fs = new System.IO.FileStream(dest, System.IO.FileMode.Create,
                                        System.IO.FileAccess.Write, System.IO.FileShare.None))
                    {
                        var buf = new byte[81920];
                        long done = 0; int read, lastPct = -1;
                        while ((read = await src.ReadAsync(buf, 0, buf.Length)) > 0)
                        {
                            await fs.WriteAsync(buf, 0, read);
                            done += read;
                            if (total > 0)
                            {
                                int pct = (int)(done * 100 / total);
                                if (pct != lastPct) { lastPct = pct; UpdateBtn.Content = string.Format(L("S.update.downloading"), pct); }
                            }
                        }
                    }
                }
            }
        }

        // Zabezpieczenie przed podmianą na uszkodzony/częściowy plik: sensowny rozmiar + nagłówek PE „MZ".
        private static bool IsValidExe(string path, long expectedSize)
        {
            try
            {
                var fi = new System.IO.FileInfo(path);
                if (!fi.Exists || fi.Length < 1_000_000) return false;
                if (expectedSize > 0 && fi.Length != expectedSize) return false;
                using (var fs = System.IO.File.OpenRead(path))
                    return fs.ReadByte() == 'M' && fs.ReadByte() == 'Z';
            }
            catch { return false; }
        }

        // ---------- Nawigacja (rail) ----------

        private void Nav_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string v) ShowView(v);
        }

        private void ShowView(string view)
        {
            SessionsView.Visibility = view == "Sessions" ? Visibility.Visible : Visibility.Collapsed;
            DashboardView.Visibility = view == "Dashboard" ? Visibility.Visible : Visibility.Collapsed;
            RecentView.Visibility = view == "Recent" ? Visibility.Visible : Visibility.Collapsed;
            SettingsView.Visibility = view == "Settings" ? Visibility.Visible : Visibility.Collapsed;

            SetNav(NavDashboard, IcoDashboard, view == "Dashboard");
            SetNav(NavSessions, IcoSessions, view == "Sessions");
            SetNav(NavRecent, IcoRecent, view == "Recent");
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
                var sideVis = immersive ? Visibility.Collapsed : Visibility.Visible;
                Rail.Visibility = sideVis;
                Sidebar.Visibility = sideVis;
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
            var vis = IsImmersive() ? Visibility.Collapsed : Visibility.Visible;
            Rail.Visibility = vis;
            Sidebar.Visibility = vis;
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

        private void Avatar_Click(object sender, RoutedEventArgs e)
            => new AboutWindow { Owner = this }.ShowDialog();

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

        private void Window_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || _isFullscreen) return;
            ZoomTo(_settings.UiScale + (e.Delta > 0 ? 0.1 : -0.1));
            e.Handled = true;
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_updating && _settings.ConfirmCloseConnected &&
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
            SetUiScale.Text = ((int)Math.Round(_settings.UiScale * 100)).ToString();
            SetBarDelay.Text = _settings.FullscreenBarDelayMs.ToString();
            SetTheme.SelectedIndex = _settings.Theme == "Light" ? 1 : _settings.Theme == "System" ? 2 : 0;
            SetBorder.SelectedIndex = _settings.WindowBorderColor == "System" ? 2
                                    : string.IsNullOrEmpty(_settings.WindowBorderColor) ? 0 : 1;
            SetLanguage.SelectedIndex = _settings.Language == "en" ? 1 : 0;
            SetListStyle.SelectedIndex = _settings.ListStyle == "Minimal" ? 1 : 0;
            SetDefaultPort.Text = _settings.DefaultPort.ToString();
            SetColorDepth.SelectedIndex = _settings.ColorDepth == 16 ? 0 : _settings.ColorDepth == 24 ? 1 : 2;
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
            SettingsStatus.Text = "";
            _loadingSettings = false;
        }

        // Lista serwerów do „Połącz na starcie": checkbox = auto-połączenie, przeciąganie (uchwyt ⠿) ustala
        // KOLEJNOŚĆ uruchamiania. Wyświetlane najpierw zaznaczone (w zapisanej kolejności), potem reszta.
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
            var ordered = new List<ServerInfo>();
            foreach (var id in ids)
            {
                var s = servers.FirstOrDefault(v => v.Id == id);
                if (s != null && !ordered.Contains(s)) ordered.Add(s);
            }
            foreach (var s in servers.OrderBy(v => v.Group).ThenBy(v => v.Name))
                if (!ordered.Contains(s)) ordered.Add(s);

            foreach (var s in ordered)
                AutoConnectList.Children.Add(BuildAutoConnectRow(s, selected.Contains(s.Id)));
        }

        // ---------- Profile poświadczeń (lista w Ustawieniach) ----------
        private void BuildProfilesList()
        {
            ProfilesList.Children.Clear();
            if (_credProfiles.Count == 0)
            {
                ProfilesList.Children.Add(new TextBlock
                {
                    Text = L("S.prof.empty"),
                    Foreground = Res("TextTer"), FontSize = 12
                });
                return;
            }
            foreach (var pr in _credProfiles)
                ProfilesList.Children.Add(BuildProfileRow(pr));
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
            _settings.DefaultPort = int.TryParse(SetDefaultPort.Text.Trim(), out var p) ? Math.Clamp(p, 1, 65535) : 3389;
            _settings.ColorDepth = ParseColorDepth();
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
            _settings.Theme = (SetTheme.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Dark";
            _settings.Language = (SetLanguage.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "pl";
            _settings.ListStyle = (SetListStyle.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Default";

            SettingsStore.Save(_settings);
            ApplySettings();
            SettingsStatus.Text = L("S.st.saved");
        }

        private int ParseColorDepth()
        {
            var text = (SetColorDepth.SelectedItem as ComboBoxItem)?.Content?.ToString();
            return RdpUtils.ParseColorDepth(text);
        }

        private void ApplySettings()
        {
            ConnectionLog.Enabled = _settings.ConnectionLogEnabled;
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

            ThemeManager.Apply(_settings.Theme);
            LocalizationManager.Apply(_settings.Language);
            BuildTrayMenu();   // etykiety menu zasobnika w nowym języku
            ApplyHotkey();
            UpdateImmersive();
            // Styl widoku (Domyślny/Minimalny) i motyw zmieniają wygląd wierszy/kart — przerysuj oba na żywo.
            RenderTree(SearchBox.Text);
            RebuildTabStrip();
        }

        // Filtr Ustawień: chowa karty, których zagregowany (zlokalizowany) tekst nie zawiera zapytania.
        // Tekst czytamy z drzewa LOGICZNEGO (działa bez rozwijania list i bez renderu; łapie etykiety,
        // treści checkboxów i pozycje list rozwijanych). Kilka kart — koszt pomijalny, bez cache.
        private void SettingsSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            string q = (SettingsSearch.Text ?? "").Trim().ToLowerInvariant();
            foreach (var child in SettingsPanel.Children)
            {
                if (!(child is Border card)) continue;   // pomiń tytuł i samo pole wyszukiwania
                if (q.Length == 0) { card.Visibility = Visibility.Visible; continue; }
                var sb = new System.Text.StringBuilder();
                CollectSettingsText(card, sb);
                card.Visibility = sb.ToString().ToLowerInvariant().Contains(q) ? Visibility.Visible : Visibility.Collapsed;
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

        // Ustawienia interfejsu (motyw / język / styl listy) działają OD RAZU po zmianie — bez scrollowania do
        // „Zapisz". Zapis pliku jest odroczony (debounce). Pozostałe ustawienia nadal zatwierdza przycisk Zapisz.
        private void InterfaceSetting_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingSettings || _settings == null) return;
            _settings.Theme = (SetTheme.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Dark";
            _settings.Language = (SetLanguage.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "pl";
            _settings.ListStyle = (SetListStyle.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Default";
            _settings.WindowBorderColor = (SetBorder.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
            WindowBorder.SetSpec(_settings.WindowBorderColor);
            ApplySettings();
            QueueSettingsSave();
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

            int open = _sessions.Count + _sessionWindows.Count;
            var stats = LoadConnectionStats(14);
            int last7 = stats.PerDay.Length >= 7
                ? stats.PerDay.Skip(stats.PerDay.Length - 7).Sum() : stats.PerDay.Sum();

            var cards = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 22) };
            cards.Children.Add(StatCard(L("S.dash.servers"), _vm.Total.ToString()));
            cards.Children.Add(StatCard(L("S.dash.reachable"), _vm.OnlineCount.ToString()));
            cards.Children.Add(StatCard(L("S.dash.opensessions"), open.ToString()));
            cards.Children.Add(StatCard(L("S.dash.conns7"), last7.ToString()));
            DashboardPanel.Children.Add(cards);

            // Połączenia / dzień (14 dni) — z dziennika audytu.
            DashboardPanel.Children.Add(DashSection(L("S.dash.perday")));
            DashboardPanel.Children.Add(stats.TotalConnects > 0
                ? DashCard(BuildBarChart(stats.PerDay, DateTime.Now))
                : DashHint(L("S.dash.nodata")));

            // Aktywność wg dnia tygodnia (z dziennika audytu) — te same poziome słupki co „najczęściej używane".
            if (stats.TotalConnects > 0)
            {
                var wdLabels = L("S.dash.weekdays").Split(',');
                var wd = new List<KeyValuePair<string, int>>();
                for (int i = 0; i < 7 && i < stats.PerWeekday.Length; i++)
                    wd.Add(new KeyValuePair<string, int>(i < wdLabels.Length ? wdLabels[i].Trim() : (i + 1).ToString(), stats.PerWeekday[i]));
                DashboardPanel.Children.Add(DashSection(L("S.dash.weekday")));
                DashboardPanel.Children.Add(DashCard(BuildTopServers(wd)));
            }

            // Najczęściej używane serwery.
            if (stats.TopServers.Count > 0)
            {
                DashboardPanel.Children.Add(DashSection(L("S.dash.top")));
                DashboardPanel.Children.Add(DashCard(BuildTopServers(stats.TopServers)));
            }

            // Rozkład protokołów (z konfiguracji serwerów) — poziome słupki jak wyżej.
            if (_vm.Total > 0)
            {
                var protos = _vm.Servers.GroupBy(s => s.Protocol)
                    .Select(g => new KeyValuePair<string, int>(ProtocolLabel(g.Key), g.Count()))
                    .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).ToList();
                DashboardPanel.Children.Add(DashSection(L("S.dash.protocols")));
                DashboardPanel.Children.Add(DashCard(BuildTopServers(protos)));
            }

            // Ostatnie połączenia.
            DashboardPanel.Children.Add(DashSection(L("S.dash.recent")));
            int shown = 0;
            foreach (var srv in _vm.RecentServers())
            {
                var s = srv;
                DashboardPanel.Children.Add(BuildFlyoutRow(s, s.Status, false, () => LaunchServer(s, true)));
                if (++shown >= 5) break;
            }
            if (shown == 0) DashboardPanel.Children.Add(DashHint(L("S.dash.nohistory")));
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
            _ => p.ToString()
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
            filter = (filter ?? "").Trim().ToLowerInvariant();
            ServerTree.Children.Clear();
            _serverRows.Clear();
            _serverActivate.Clear();
            _serverStatusDot.Clear();
            _multiSelect.Clear();
            _selectAnchor = null;
            _visibleOrder.Clear();

            // Dostępność: strzałki i Tab przenoszą fokus między wierszami serwerów.
            System.Windows.Input.KeyboardNavigation.SetDirectionalNavigation(ServerTree, System.Windows.Input.KeyboardNavigationMode.Continue);
            System.Windows.Input.KeyboardNavigation.SetTabNavigation(ServerTree, System.Windows.Input.KeyboardNavigationMode.Continue);

            // Sekcja „Przypięte" na górze — ulubione serwery (kolejność z listy), niezależnie od grupy.
            var pinned = _vm.Servers.Where(s => s.Pinned && RdpUtils.MatchesFilter(s, filter)).ToList();
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
                if (s.Pinned) continue;
                if (!RdpUtils.MatchesFilter(s, filter)) continue;
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
                FontSize = 11.5, FontWeight = FontWeights.SemiBold,
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
                Margin = new Thickness(0, 1, 0, 1),
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

            var meta = new StackPanel { Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            meta.Children.Add(new TextBlock { Text = server.Name, Foreground = Res("TextPrim"), FontSize = 12.5 });
            meta.Children.Add(new TextBlock
            {
                Text = DisplayHost(server), Foreground = Res("TextTer"), FontSize = 10.5,
                FontFamily = (FontFamily)TryFindResource("Mono"), TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetColumn(meta, 2);
            grid.Children.Add(meta);

            var status = new Ellipse
            {
                Width = 7, Height = 7, Fill = StatusBrush(server.Status),
                VerticalAlignment = VerticalAlignment.Center
            };
            _serverStatusDot[server] = status;

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
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
                Margin = new Thickness(0, 1, 0, 1),
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
                Text = server.Name, Foreground = Res("TextPrim"), FontSize = 13, FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(name, 2);
            grid.Children.Add(name);

            // Host dosunięty do prawej (z opcjonalną gwiazdką przypięcia), przycinany przy długich nazwach.
            var rightPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
            };
            if (server.Pinned)
                rightPanel.Children.Add(new TextBlock
                {
                    Text = "★", Foreground = Res("Idle"), FontSize = 9,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
                });
            rightPanel.Children.Add(new TextBlock
            {
                Text = DisplayHost(server), Foreground = Res("TextTer"), FontSize = 11.5,
                FontFamily = (FontFamily)TryFindResource("Mono"), VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 100, TextTrimming = TextTrimming.CharacterEllipsis
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

        // Wspólne zachowanie wiersza (hover / przeciąganie-zmiana kolejności / klik / menu) — jednakowe w obu stylach.
        private void WireServerRow(Border row, ServerInfo server)
        {
            // Dostępność (z PR #21): wiersz fokusowalny (nawigacja klawiaturą), nazwa dla czytnika ekranu
            // (nazwa — host — status), a kropka statusu — swój tekst. Wspólne dla obu stylów listy.
            row.Focusable = true;
            string tagText = (server.Tags != null && server.Tags.Count > 0) ? "  #" + string.Join(" #", server.Tags) : "";
            System.Windows.Automation.AutomationProperties.SetName(row,
                server.Name + " — " + DisplayHost(server) + " — " + StatusLabel(server.Status) + tagText);
            string noteText = string.IsNullOrWhiteSpace(server.Notes) ? "" : "\n" + server.Notes.Trim();
            if (tagText.Length > 0 || noteText.Length > 0) row.ToolTip = server.Name + tagText + noteText;   // tagi + notatka po najechaniu
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
            var dupItem = new MenuItem { Header = L("S.m.dupserver") };
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
            if (server.Protocol != RemoteProtocol.Http)
                AddCopy("S.m.copy.port", () => server.Port.ToString());
            if (rdp || server.Protocol == RemoteProtocol.Ssh)
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
            menu.Items.Add(pinItem);
            menu.Items.Add(new Separator());
            if (rdp) menu.Items.Add(newWinItem);       // osobne okno sesji jest RDP-owe
            if (rdp || server.Protocol == RemoteProtocol.Ssh) menu.Items.Add(connectAsItem);
            menu.Items.Add(editItem);
            menu.Items.Add(dupItem);
            menu.Items.Add(copyMenu);
            if (server.Protocol != RemoteProtocol.Serial && server.Protocol != RemoteProtocol.Http)
                menu.Items.Add(diagItem);   // sonda TCP — nie dla COM/URL
            menu.Items.Add(wolItem);
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

        // Wpis WWW: nie ma sesji — otwieramy panel webowy w domyślnej przeglądarce.
        private void OpenUrl(ServerInfo server)
        {
            string url = (server.Host ?? "").Trim();
            if (url.Length == 0) return;
            if (!url.Contains("://")) url = "https://" + url;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                RecordRecent(server);
            }
            catch (Exception ex) { SetStatus(string.Format(L("S.st.exception"), ex.Message), StatusKind.Error); }
        }

        private void OpenServer(ServerInfo server, bool autoConnect = false, bool forceNew = false)
        {
            if (server.Protocol == RemoteProtocol.Http) { OpenUrl(server); return; }

            ShowView("Sessions");   // kontrolka RDP musi powstać przy widocznym widoku sesji
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
                // TOFU: pytanie o klucz hosta przychodzi z wątku SSH — pokaż dialog na wątku UI.
                term.TrustHostKey = (hostPort, fp, changed) => (bool)Dispatcher.Invoke(new Func<bool>(() =>
                    MessageBox.Show(this,
                        string.Format(L(changed ? "S.ssh.hostkey.changed" : "S.ssh.hostkey.new"), hostPort, fp),
                        L("S.ssh.hostkey.title"), MessageBoxButton.YesNo,
                        changed ? MessageBoxImage.Warning : MessageBoxImage.Question,
                        changed ? MessageBoxResult.No : MessageBoxResult.Yes) == MessageBoxResult.Yes));
                // Zaszyfrowany klucz prywatny → maskowany prompt o passphrase (null = anuluj).
                term.RequestKeyPassphrase = path => (string)Dispatcher.Invoke(new Func<string>(() =>
                {
                    var dlg = new InputDialog(L("S.ssh.keypass.title"),
                        string.Format(L("S.ssh.keypass.label"), System.IO.Path.GetFileName(path)),
                        "", masked: true) { Owner = this };
                    return dlg.ShowDialog() == true ? dlg.Value : null;
                }));
                SessionContainer.Children.Add(term);
                session = new Session(server, term);
                WireTermEvents(session);
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
                    return !string.IsNullOrWhiteSpace(EffUser(s.Server))
                           && (!string.IsNullOrEmpty(s.Password) || !string.IsNullOrWhiteSpace(s.Server.PrivateKeyPath));
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
            EmptyHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;

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
                s.View.Visibility = (s == _active && (s.Connected || s.IsTerm)) ? Visibility.Visible : Visibility.Collapsed;
            }

            if (!has)
            {
                SessionOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            // Nakładka nie dla terminali — ich HWND i tak by ją zakrył; komunikaty idą do terminala.
            if (_active.Connected || _active.IsTerm)
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
            if (s.IsTerm) { ConnectTerm(s); return; }
            if (s.IsVnc) { ConnectVnc(s); return; }

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
                         : proto == RemoteProtocol.Vnc ? _settings.VncWarned : true;
            if (already) return;

            if (proto == RemoteProtocol.Telnet) _settings.TelnetWarned = true; else _settings.VncWarned = true;
            SettingsStore.Save(_settings);

            string k = proto == RemoteProtocol.Telnet ? "telnet" : "vnc";
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
            if (_active.IsTerm) { _active.Term.Disconnect(); return; }
            if (_active.IsVnc) { try { _active.Vnc.Client?.Close(); } catch { } return; }
            try { _active.Rdp.Disconnect(); } catch (Exception ex) { SetSessionStatus(_active, string.Format(L("S.st.disconnecting"), ex.Message), StatusKind.Error); }
        }

        private void SendCtrlAltDel_Click(object sender, RoutedEventArgs e) => SendCtrlAltDel(_active);

        // Wysyła zdalne Ctrl+Alt+Del. OCX RDP nie ma na to scriptowalnej metody, więc — jak w mstsc — dajemy
        // klientowi Ctrl+Alt+End, który (przy KeyboardHookMode=2 i z fokusem na kontrolce) tłumaczy je na
        // zdalną sekwencję SAS. Iniekcja przez keybd_event po ustawieniu fokusu na kontrolce sesji.
        private void SendCtrlAltDel(Session s)
        {
            if (s == null || s.Server.Protocol != RemoteProtocol.Rdp || !s.Connected) return;
            if (s != _active && s != _paneLeft && s != _paneRight) Activate(s);   // sesja musi być widoczna
            try { s.Rdp.Focus(); } catch { }
            // Po przetworzeniu fokusu (Input) wstrzykujemy Ctrl↓ Alt↓ End↓ End↑ Alt↑ Ctrl↑.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // keybd_event jest globalny — wyślij TYLKO gdy nasze okno jest na pierwszym planie, inaczej
                // klawisze trafiłyby do aplikacji, na którą użytkownik zdążył przełączyć (iniekcja jest odroczona).
                if (GetForegroundWindow() != new WindowInteropHelper(this).Handle) return;
                keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
                keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
                keybd_event(VK_END, 0, 0, UIntPtr.Zero);
                keybd_event(VK_END, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
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
            Sidebar.Visibility = Visibility.Visible;
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

            foreach (var server in RankServers(_vm.Servers, x => x, f))
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
            ThemeManager.Apply(_settings.Theme);
            RenderTree(SearchBox.Text);   // wiersze/karty zależą od motywu
            RebuildTabStrip();
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

            var (host, port, user, domain) = RdpUtils.ParseQuickConnect(dlg.Value, _settings.DefaultPort);
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

        private void AddServer_Click(object sender, RoutedEventArgs e)
        {
            // Pusta grupa = domyślny „kosz" lokalizowany przy wyświetlaniu (RenderTree), nie zapisujemy tu nazwy PL.
            var server = new ServerInfo { Group = "", Status = ServerStatus.Offline, Port = _settings.DefaultPort };
            var dlg = new ServerEditWindow(server, "", _credProfiles) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                _vm.Add(server);
                PersistServers();
                SaveCredential(server, dlg.EnteredPassword);
                RenderTree(SearchBox.Text);
                CheckReachabilityAsync();
            }
        }

        // Przycisk „Importuj…" rozwija menu źródeł (mstsc / .rdp / mRemoteNG / RDCMan).
        private void ImportMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.ContextMenu != null)
            {
                b.ContextMenu.PlacementTarget = b;
                b.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                b.ContextMenu.IsOpen = true;
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

        // Wspólny przebieg importu z innego menedżera: plik → parser → dedup po host:port → zapis.
        // Hasła nie są przenoszone (mRemoteNG/RDCMan szyfrują je własnymi kluczami).
        private void ImportExternal(string title, string filter, Func<string, ExternalImport.Result> parse)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter };
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
            int added = 0, skipped = 0;
            foreach (var srv in result.Servers)
            {
                if (!existing.Add(srv.Host + ":" + srv.Port)) { skipped++; continue; }
                _vm.Add(srv);
                added++;
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
                + "\n\n" + L("S.msg.import.nopass"),
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
            PersistServers();
            SaveCredential(copy, dlg.EnteredPassword);
            RenderTree(SearchBox.Text);
            CheckReachabilityAsync();
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
            PersistServers();
            RenderTree(SearchBox.Text);
            CheckReachabilityAsync();
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
                var results = await Task.WhenAll(servers.Select(srv =>
                    Task.Run(() => new KeyValuePair<ServerInfo, ServerStatus>(srv,
                        // Serial (COM) i WWW (URL) — sonda TCP host:port nie ma sensu, zostaw bieżący status.
                        srv.Protocol == RemoteProtocol.Serial || srv.Protocol == RemoteProtocol.Http
                            ? srv.Status : Probe(srv.Host, srv.Port)))));

                foreach (var kv in results)
                {
                    kv.Key.Status = kv.Value;
                    if (_serverStatusDot.TryGetValue(kv.Key, out var dot))
                        dot.Fill = StatusBrush(kv.Value);
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
            bool ok = await Task.Run(() => Probe(host, port) == ServerStatus.Online);
            sw.Stop();

            string msg = RdpUtils.FormatDiagnostics(host, port, ok, sw.ElapsedMilliseconds,
                L("S.diag.open"), L("S.diag.closed"));
            SetStatus(msg, ok ? StatusKind.Ok : StatusKind.Error);
            MessageBox.Show(msg, string.Format(L("S.msg.diag.titlefmt"), server.Name ?? host),
                MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        private static ServerStatus Probe(string host, int port)
        {
            if (string.IsNullOrWhiteSpace(host)) return ServerStatus.Offline;
            try
            {
                using (var c = new TcpClient())
                {
                    var ar = c.BeginConnect(host, port, null, null);
                    if (ar.AsyncWaitHandle.WaitOne(1500) && c.Connected)
                    {
                        c.EndConnect(ar);
                        return ServerStatus.Online;
                    }
                    return ServerStatus.Offline;
                }
            }
            catch
            {
                return ServerStatus.Offline;
            }
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
