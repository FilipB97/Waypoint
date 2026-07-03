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

        private readonly Dictionary<ServerInfo, Border> _serverRows = new Dictionary<ServerInfo, Border>();
        private readonly Dictionary<ServerInfo, Rectangle> _serverAccent = new Dictionary<ServerInfo, Rectangle>();
        private readonly Dictionary<ServerInfo, Ellipse> _serverStatusDot = new Dictionary<ServerInfo, Ellipse>();
        private readonly Dictionary<Session, Rectangle> _tabUnderline = new Dictionary<Session, Rectangle>();
        private readonly Dictionary<Session, Ellipse> _tabStatus = new Dictionary<Session, Ellipse>();
        private readonly Dictionary<Session, TextBlock> _tabName = new Dictionary<Session, TextBlock>();
        private readonly Dictionary<Session, TextBlock> _tabClose = new Dictionary<Session, TextBlock>();
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
        private InsertionAdorner _dropAdorner;   // linia „tu wyląduje" na krawędzi wiersza
        private Border _dropRow;                  // wiersz, do którego przypięty jest adorner

        // Klucz sekcji „Przypięte" w AppSettings.CollapsedGroups (nie koliduje z nazwami grup użytkownika).
        private const string PinnedGroupKey = "__pinned__";

        // Skrót do lokalizowanego tekstu (dla UI budowanego w kodzie: menu, komunikaty).
        private static string L(string key) => LocalizationManager.S(key);

        // Otwarte, samodzielne okna sesji (model wielookienny).
        private readonly System.Collections.Generic.List<SessionWindow> _sessionWindows = new System.Collections.Generic.List<SessionWindow>();

        // Opóźnienie pojawienia się paska pełnoekranowego (jak w mstsc) + polling pozycji kursora.
        private DispatcherTimer _fsBarDelay;
        private DispatcherTimer _fsCursorPoll;
        private DispatcherTimer _focusPeekPoll;    // wykrywa najechanie na lewą krawędź w trybie skupienia
        private DispatcherTimer _focusPeekDelay;   // opóźnienie przytrzymania (jak pasek pełnoekranowy)
        private bool _focusPeeking;                // panel boczny chwilowo wysunięty w trybie skupienia
        private bool? _focusOverride;              // ręczne wł/wył skupienia (null = wg ustawienia); reset po un-maximize
        private double _savedCaptionHeight = double.NaN;   // CaptionHeight sprzed skupienia (do przywrócenia)
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
            ConnectionLog.Enabled = _settings.ConnectionLogEnabled;
            _vm.UseRecentIds(_settings.RecentIds);   // współdziel listę „ostatnich" z ustawieniami
            RootScale.ScaleX = RootScale.ScaleY = Math.Clamp(_settings.UiScale, 0.7, 1.8);

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
            UpdateToolbarEnabled();
            UpdateToolbarMode();

            _reachTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(Math.Clamp(_settings.ReachabilityIntervalSec, 5, 3600))
            };
            _reachTimer.Tick += (s, a) => CheckReachabilityAsync();
            if (_settings.ReachabilityEnabled) { _reachTimer.Start(); CheckReachabilityAsync(); }

            ShowView("Sessions");

            InitTray();
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndHook);
            ApplyHotkey();

            CheckForUpdatesAsync();
            // Po wyrenderowaniu okna (modal przywracania potrzebuje widocznego właściciela).
            Dispatcher.BeginInvoke(new Action(StartupConnect), DispatcherPriority.Loaded);
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
                    var cur = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                    var current = new Version(cur.Major, cur.Minor, Math.Max(cur.Build, 0));
                    if (info != null && Core.UpdateCheck.IsNewer(info.Version, current))
                    {
                        _update = info;
                        UpdateBtn.Content = string.Format(L("S.update.available"), info.Version);
                        UpdateBtn.Visibility = Visibility.Visible;
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

            if (MessageBox.Show(string.Format(L("S.update.confirm"), _update.Version), L("S.update.title"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

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
            else if (view == "Settings") LoadSettingsForm();

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

        private void RestoreFromTray()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        private const int WM_HOTKEY = 0x0312;
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
            ApplyImmersiveCaption(immersive);
            UpdateToolbarMode();
        }

        // W skupieniu AppTitleBar znika, ale WindowChrome zostawia strefę caption (~32px) na górze,
        // więc pasek kart z przyciskami okna w nią wpada. Hit-test caption jest błędny przy DPI≠100%
        // (znany bug WPF: regiony IsHitTestVisibleInChrome źle skalowane — środek ikon nie łapie,
        // trafienie tylko przy dolno-bocznym rogu). Zerujemy caption na czas skupienia → cała góra to
        // obszar klienta, przyciski działają zwykłym hit-testem WPF. Poza skupieniem przywracamy wartość.
        private void ApplyImmersiveCaption(bool immersive)
        {
            ApplyCaptionCore(immersive);
            // WPF-UI przy PIERWSZEJ maksymalizacji przywraca CaptionHeight PO naszym handlerze (stąd „działa
            // dopiero po przywróceniu i ponownej maksymalizacji"). Re-asercja po jego synchronicznych handlerach.
            Dispatcher.BeginInvoke(new Action(() => ApplyCaptionCore(IsImmersive())), DispatcherPriority.Loaded);
        }

        private void ApplyCaptionCore(bool immersive)
        {
            var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
            if (chrome == null) return;   // brak chrome = brak strefy caption do naprawy
            if (immersive)
            {
                if (double.IsNaN(_savedCaptionHeight)) _savedCaptionHeight = chrome.CaptionHeight;
                if (chrome.CaptionHeight != 0) chrome.CaptionHeight = 0;
            }
            else if (!double.IsNaN(_savedCaptionHeight) && chrome.CaptionHeight != _savedCaptionHeight)
            {
                chrome.CaptionHeight = _savedCaptionHeight;
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
            _focusPeeking = true;
            BodyGrid.Children.Remove(Rail);
            BodyGrid.Children.Remove(Sidebar);
            Rail.Visibility = Visibility.Visible;
            Sidebar.Visibility = Visibility.Visible;
            FocusPeekHost.Children.Add(Rail);
            FocusPeekHost.Children.Add(Sidebar);
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
            b.Background = active ? (Brush)TryFindResource("AccentSoft") : Brushes.Transparent;
            ico.Foreground = active ? (Brush)TryFindResource("Accent") : (Brush)TryFindResource("TextTer");
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

        private void ZoomTo(double scale)
        {
            scale = Math.Round(Math.Clamp(scale, 0.7, 1.8), 2);
            _settings.UiScale = scale;
            RootScale.ScaleX = RootScale.ScaleY = scale;
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

        private void LoadSettingsForm()
        {
            SetUiScale.Text = ((int)Math.Round(_settings.UiScale * 100)).ToString();
            SetBarDelay.Text = _settings.FullscreenBarDelayMs.ToString();
            SetTheme.SelectedIndex = _settings.Theme == "Light" ? 1 : _settings.Theme == "System" ? 2 : 0;
            SetLanguage.SelectedIndex = _settings.Language == "en" ? 1 : 0;
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
            SetDataPath.Text = SettingsStore.Dir;
            SettingsStatus.Text = "";
        }

        // Lista serwerów do „Połącz na starcie" — checkbox per serwer, zaznaczone = auto-połączenie.
        private void BuildAutoConnectList()
        {
            AutoConnectList.Children.Clear();
            var selected = new HashSet<string>(_settings.AutoConnectServerIds ?? new List<string>());
            var any = false;
            foreach (var s in _vm.Servers.OrderBy(v => v.Group).ThenBy(v => v.Name))
            {
                any = true;
                AutoConnectList.Children.Add(new CheckBox
                {
                    Content = (string.IsNullOrWhiteSpace(s.Name) ? s.Host : s.Name) + "  —  " + DisplayHost(s),
                    Tag = s.Id,
                    IsChecked = selected.Contains(s.Id),
                    Foreground = (Brush)TryFindResource("TextPrim"),
                    Margin = new Thickness(0, 3, 0, 3)
                });
            }
            if (!any)
                AutoConnectList.Children.Add(new TextBlock
                {
                    Text = L("S.set.autoconnect.empty"),
                    Foreground = (Brush)TryFindResource("TextTer"), FontSize = 12
                });
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
            _settings.AutoConnectServerIds = AutoConnectList.Children.OfType<CheckBox>()
                .Where(cb => cb.IsChecked == true).Select(cb => cb.Tag as string)
                .Where(id => !string.IsNullOrEmpty(id)).ToList();
            _settings.Theme = (SetTheme.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Dark";
            _settings.Language = (SetLanguage.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "pl";

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
            RootScale.ScaleX = RootScale.ScaleY = Math.Clamp(_settings.UiScale, 0.7, 1.8);
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
                RecentPanel.Children.Add(new TextBlock { Text = L("S.dash.norecent"), Foreground = (Brush)TryFindResource("TextTer") });
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

            // Najczęściej używane serwery.
            if (stats.TopServers.Count > 0)
            {
                DashboardPanel.Children.Add(DashSection(L("S.dash.top")));
                DashboardPanel.Children.Add(DashCard(BuildTopServers(stats.TopServers)));
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
            Text = text, Foreground = (Brush)TryFindResource("TextSec"),
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 8)
        };

        private FrameworkElement DashCard(FrameworkElement content) => new Border
        {
            Background = (Brush)TryFindResource("Panel"), BorderBrush = (Brush)TryFindResource("Border"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(0, 0, 0, 22), Child = content
        };

        private FrameworkElement DashHint(string text) => new TextBlock
        {
            Text = text, Foreground = (Brush)TryFindResource("TextTer"), Margin = new Thickness(2, 0, 0, 22)
        };

        // Wykres słupkowy „połączenia / dzień" — rysowany prostokątami (bez zależności od bibliotek).
        private FrameworkElement BuildBarChart(int[] values, DateTime endDate)
        {
            int max = Math.Max(1, values.Max());
            var row = new StackPanel { Orientation = Orientation.Horizontal, Height = 108,
                VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left };
            var accent = (Brush)TryFindResource("Accent");
            var dim = (Brush)TryFindResource("Elevated");
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
            var accent = (Brush)TryFindResource("Accent");
            var track = (Brush)TryFindResource("Elevated");
            var panel = new StackPanel();
            foreach (var kv in top)
            {
                var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var name = new TextBlock { Text = kv.Key, Foreground = (Brush)TryFindResource("TextPrim"),
                    FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(name, 0); grid.Children.Add(name);

                var barGrid = new Grid { Width = 200, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 10, 0) };
                barGrid.Children.Add(new Border { Height = 8, Background = track, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Stretch });
                barGrid.Children.Add(new Border { Height = 8, Width = 200.0 * kv.Value / max, Background = accent,
                    CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left });
                Grid.SetColumn(barGrid, 1); grid.Children.Add(barGrid);

                var count = new TextBlock { Text = kv.Value.ToString(), Foreground = (Brush)TryFindResource("TextSec"),
                    FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, MinWidth = 24, TextAlignment = TextAlignment.Right };
                Grid.SetColumn(count, 2); grid.Children.Add(count);

                panel.Children.Add(grid);
            }
            return panel;
        }

        private FrameworkElement StatCard(string label, string value)
        {
            var card = new Border
            {
                Background = (Brush)TryFindResource("Panel"), BorderBrush = (Brush)TryFindResource("Border"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10), Padding = new Thickness(18, 14, 18, 14), Margin = new Thickness(0, 0, 12, 0), MinWidth = 130
            };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = value, Foreground = (Brush)TryFindResource("Accent"), FontSize = 26, FontWeight = FontWeights.Bold });
            sp.Children.Add(new TextBlock { Text = label, Foreground = (Brush)TryFindResource("TextSec"), FontSize = 12 });
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
            _serverAccent.Clear();
            _serverStatusDot.Clear();

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
                    foreach (var s in pinned) ServerTree.Children.Add(BuildServerRow(s));
            }

            // Zwykłe grupy (bez przypiętych).
            var order = new List<string>();
            var byGroup = new Dictionary<string, List<ServerInfo>>();
            foreach (var s in _vm.Servers)
            {
                if (s.Pinned) continue;
                if (!RdpUtils.MatchesFilter(s, filter)) continue;
                var g = string.IsNullOrWhiteSpace(s.Group) ? "Serwery" : s.Group;
                if (!byGroup.ContainsKey(g)) { order.Add(g); byGroup[g] = new List<ServerInfo>(); }
                byGroup[g].Add(s);
            }
            foreach (var g in order)
            {
                bool collapsed = _settings.CollapsedGroups.Contains(g);
                ServerTree.Children.Add(BuildGroupHeader(g, byGroup[g].Count, collapsed, isPinned: false));
                if (!collapsed)
                    foreach (var s in byGroup[g])
                        ServerTree.Children.Add(BuildServerRow(s));
            }
            UpdateActiveRows();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RenderTree(SearchBox.Text);

        private FrameworkElement BuildGroupHeader(string name, int count, bool collapsed, bool isPinned)
        {
            var row = new Border
            {
                Padding = new Thickness(6, 10, 6, 4),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };

            // Strzałka zwijania (▸ zwinięte / ▾ rozwinięte).
            sp.Children.Add(new TextBlock
            {
                Text = collapsed ? "▸" : "▾",
                Foreground = (Brush)TryFindResource("TextTer"), FontSize = 10, Width = 12,
                VerticalAlignment = VerticalAlignment.Center
            });

            if (isPinned)
                sp.Children.Add(new TextBlock
                {
                    Text = "★", Foreground = (Brush)TryFindResource("Idle"), FontSize = 11,
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
                Foreground = (Brush)TryFindResource("TextSec"),
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
                if ((string.IsNullOrWhiteSpace(s.Group) ? "Serwery" : s.Group) == oldName)
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

        private FrameworkElement BuildServerRow(ServerInfo server)
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
                Width = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = (Brush)TryFindResource("Accent"),
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
            meta.Children.Add(new TextBlock { Text = server.Name, Foreground = (Brush)TryFindResource("TextPrim"), FontSize = 12.5 });
            meta.Children.Add(new TextBlock
            {
                Text = DisplayHost(server), Foreground = (Brush)TryFindResource("TextTer"), FontSize = 10.5,
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
                    Text = "★", Foreground = (Brush)TryFindResource("Idle"), FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
                });
            right.Children.Add(status);
            Grid.SetColumn(right, 3);
            grid.Children.Add(right);

            row.Child = grid;

            // Dostępność: wiersz jest fokusowalny (nawigacja klawiaturą po drzewie), ma nazwę dla
            // czytnika ekranu (nazwa + host + status tekstowo), a kropka statusu — swój tekst.
            row.Focusable = true;
            System.Windows.Automation.AutomationProperties.SetName(row,
                server.Name + " — " + DisplayHost(server) + " — " + StatusText(server.Status));
            System.Windows.Automation.AutomationProperties.SetName(status, StatusText(server.Status));

            row.MouseEnter += (s, e) => { if (_active?.Server != server) row.Background = (Brush)TryFindResource("Elevated"); };
            row.MouseLeave += (s, e) => { if (_active?.Server != server && !row.IsKeyboardFocused) row.Background = Brushes.Transparent; };
            row.GotKeyboardFocus += (s, e) => { if (_active?.Server != server) row.Background = (Brush)TryFindResource("Elevated"); };
            row.LostKeyboardFocus += (s, e) => { if (_active?.Server != server) row.Background = Brushes.Transparent; };
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
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
                var dragged = e.Data.GetData(typeof(ServerInfo)) as ServerInfo;
                if (dragged == null || dragged == server) { ClearDropIndicator(); return; }
                bool bottom = e.GetPosition(row).Y > row.ActualHeight / 2;
                ShowDropIndicator(row, bottom);
            };
            row.Drop += (s, e) =>
            {
                ClearDropIndicator();
                bool bottom = e.GetPosition(row).Y > row.ActualHeight / 2;
                ReorderServer(e.Data.GetData(typeof(ServerInfo)) as ServerInfo, server, bottom);
                e.Handled = true;
            };
            row.MouseLeftButtonUp += (s, e) =>
            {
                if (_didDrag) { _didDrag = false; return; }   // to było przeciąganie, nie klik
                LaunchServer(server, true);
            };

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
                AddCopy("S.m.copy.user", () => server.Username);
                if (rdp) AddCopy("S.m.copy.domain", () => server.Domain);
                copyMenu.Items.Add(new Separator());
                AddCopy("S.m.copy.pass", () => ReadPassword(server));
                AddCopy("S.m.copy.userpass", () => server.Username + "\t" + ReadPassword(server));
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
            row.ContextMenu = menu;

            _serverRows[server] = row;
            _serverAccent[server] = accent;
            return row;
        }

        private void UpdateActiveRows()
        {
            foreach (var kv in _serverRows)
            {
                bool active = _active != null && _active.Server == kv.Key;
                kv.Value.Background = active ? (Brush)TryFindResource("AccentSoft") : Brushes.Transparent;
                _serverAccent[kv.Key].Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            }
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
            _dropAdorner = new InsertionAdorner(row, (Brush)TryFindResource("Accent")) { AtBottom = bottom };
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
                row.Background = active ? (Brush)TryFindResource("AccentSoft") : Brushes.Transparent;
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
            }
            if (server.SavePassword && CredentialStore.TryRead(server.CredTarget, out var savedPw))
                session.Password = savedPw;

            _sessions.Add(session);
            session.TabButton = BuildTab(session);
            TabStrip.Children.Add(session.TabButton);
            RefreshTabTitles();

            Activate(session);
            if (autoConnect) BeginConnect(session);
            PersistOpenSessions();
        }

        private static bool CanAuto(Session s)
        {
            switch (s.Server.Protocol)
            {
                case RemoteProtocol.Telnet:
                case RemoteProtocol.Serial:
                    return true;   // logowanie (jeśli jest) dzieje się w terminalu
                case RemoteProtocol.Ssh:
                    return !string.IsNullOrWhiteSpace(s.Server.Username)
                           && (!string.IsNullOrEmpty(s.Password) || !string.IsNullOrWhiteSpace(s.Server.PrivateKeyPath));
                default:
                    return s.Server.UseWindowsAccount || !string.IsNullOrEmpty(s.Password);
            }
        }

        private void Activate(Session session)
        {
            HideFocusPeek();   // aktywacja sesji (np. klik z wysuniętego panelu) chowa peek (i przenosi panel z powrotem)
            _active = session;
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

        /// <summary>
        /// Steruje kanwą: aktywna kontrolka RDP widoczna tylko gdy połączona; w przeciwnym razie
        /// nakładka (spinner „Łączenie…" albo „Rozłączono" + przycisk ponownego połączenia).
        /// </summary>
        private void UpdateCanvas()
        {
            bool has = _active != null;
            EmptyHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;

            // Terminale (SSH/Telnet/Serial): widoczne od razu — statusy łączenia piszą do siebie.
            foreach (var s in _sessions)
                s.View.Visibility = (s == _active && (s.Connected || s.IsTerm)) ? Visibility.Visible : Visibility.Collapsed;

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
        {
            // Pastylka w stylu Windows Terminal: pełne zaokrąglenie, aktywna = wypełnienie + obrys,
            // najechanie podświetla nieaktywną i pokazuje ✕ (Hidden, nie Collapsed — szerokość stała).
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
                Text = session.Server.Name, Foreground = (Brush)TryFindResource("TextPrim"), FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0)
            };
            _tabName[session] = tabName;
            content.Children.Add(tabName);
            // Adres na karcie (pasek stanu z adresem znika w trybie skupienia).
            content.Children.Add(new TextBlock
            {
                Text = DisplayHost(session.Server), Foreground = (Brush)TryFindResource("TextTer"),
                FontFamily = (System.Windows.Media.FontFamily)TryFindResource("Mono"), FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 1, 0, 0)
            });
            // Kropka odzwierciedla ŻYWY stan sesji (nie statyczny status serwera): startowo rozłączona.
            var tabDot = new Ellipse
            {
                Width = 6, Height = 6, Fill = StatusBrush(ServerStatus.Offline),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0)
            };
            _tabStatus[session] = tabDot;
            content.Children.Add(tabDot);
            var close = new TextBlock
            {
                Text = "✕", Foreground = (Brush)TryFindResource("TextTer"), FontSize = 11,
                Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(3, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand,
                Visibility = Visibility.Hidden   // Hidden (nie Collapsed): karta nie zmienia szerokości na hover
            };
            close.MouseEnter += (s, e) => close.Foreground = (Brush)TryFindResource("Danger");
            close.MouseLeave += (s, e) => close.Foreground = (Brush)TryFindResource("TextTer");
            close.MouseLeftButtonUp += (s, e) => { e.Handled = true; RequestCloseSession(session); };
            _tabClose[session] = close;
            content.Children.Add(close);
            Grid.SetRow(content, 0);
            grid.Children.Add(content);

            var underline = new Rectangle
            {
                Height = 2, Fill = (Brush)TryFindResource("Accent"), RadiusX = 1, RadiusY = 1,
                Margin = new Thickness(2, 4, 2, 0),
                Visibility = Visibility.Hidden   // Hidden (nie Collapsed): karta ma stałą wysokość aktywna/nie
            };
            Grid.SetRow(underline, 1);
            grid.Children.Add(underline);

            tab.Child = grid;

            // Hover: podświetlenie nieaktywnej + pokazanie ✕; zejście przywraca stan z RefreshTabStyles.
            tab.MouseEnter += (s, e) =>
            {
                if (session != _active)
                    tab.Background = (Brush)TryFindResource("Elevated") ?? Brushes.Transparent;
                close.Visibility = Visibility.Visible;
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

            // Drag&drop kart: przeciągnięcie zmienia kolejność (upuszczenie na lewą/prawą połowę celu).
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
                try { DragDrop.DoDragDrop(tab, session, DragDropEffects.Move); }
                catch { }
                finally { tab.Opacity = 1.0; _tabDragSession = null; }
            };
            tab.DragOver += (s, e) =>
            {
                if (e.Data.GetData(typeof(Session)) is Session) { e.Effects = DragDropEffects.Move; e.Handled = true; }
            };
            tab.Drop += (s, e) =>
            {
                var dragged = e.Data.GetData(typeof(Session)) as Session;
                if (dragged == null || dragged == session) return;
                MoveTabTo(dragged, session, after: e.GetPosition(tab).X > tab.ActualWidth / 2);
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
            if (session.Server.Protocol == RemoteProtocol.Rdp) tabMenu.Items.Add(tearItem);   // wyciąganie do okna jest RDP-owe
            tabMenu.Items.Add(dupItem);
            tabMenu.Items.Add(new Separator());
            tabMenu.Items.Add(moveLeft);
            tabMenu.Items.Add(moveRight);
            tabMenu.Items.Add(new Separator());
            tabMenu.Items.Add(closeOthers);
            tabMenu.Items.Add(closeThis);
            tab.ContextMenu = tabMenu;

            _tabUnderline[session] = underline;
            return tab;
        }

        private void RefreshTabStyles()
        {
            foreach (var s in _sessions)
            {
                if (!(s.TabButton is Border b)) continue;
                bool active = s == _active;
                // Lżej: aktywna = subtelne tło + akcent (underline), bez „pudełkowego" obrysu.
                b.Background = active ? (Brush)TryFindResource("Panel") : Brushes.Transparent;
                b.BorderBrush = Brushes.Transparent;
                // Hierarchia: nieaktywne karty przygaszone (spokojniejszy pasek).
                if (_tabName.TryGetValue(s, out var nm))
                    nm.Foreground = (Brush)TryFindResource(active ? "TextPrim" : "TextSec");
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
            TabStrip.Children.Remove(dragged.TabButton);
            TabStrip.Children.Insert(to, dragged.TabButton);
            RefreshTabTitles();   // numeracja duplikatów (#2) podąża za kolejnością
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
            TabStrip.Children.Remove(s.TabButton);
            TabStrip.Children.Insert(j, s.TabButton);
            RefreshTabTitles();   // numeracja duplikatów (#2) podąża za kolejnością
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
            TabStrip.Children.Remove(session.TabButton);
            _tabUnderline.Remove(session);
            _tabStatus.Remove(session);
            _tabName.Remove(session);
            _tabClose.Remove(session);
            _sessions.Remove(session);
            PersistOpenSessions();
            RefreshTabTitles();

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
                IMsRdpClientAdvancedSettings8 adv = s.Rdp.AdvancedSettings9;
                adv.RDPPort = s.Server.Port;
                // Weryfikacja tożsamości serwera (domyślnie 2 = ostrzegaj) — chroni przed MITM.
                adv.AuthenticationLevel = (uint)Math.Clamp(s.Server.AuthenticationLevel, 0, 2);
                adv.EnableCredSspSupport = true;
                adv.ConnectToAdministerServer = s.Server.AdminSession;   // sesja konsolowa (mstsc /admin)
                adv.SmartSizing = false;   // dynamiczna rozdzielczość zajmie się dopasowaniem
                adv.EnableAutoReconnect = _settings.AutoReconnect;
                s.Rdp.ColorDepth = _settings.ColorDepth;
                adv.RedirectClipboard = s.Server.RedirectClipboard;
                adv.RedirectDrives = s.Server.RedirectDrives;
                adv.RedirectPrinters = s.Server.RedirectPrinters;
                adv.AudioRedirectionMode = (uint)Math.Clamp(s.Server.AudioMode, 0, 2);
                try { s.Rdp.SecuredSettings2.KeyboardHookMode = 2; } catch { }  // Alt+Tab/Win -> zdalna w pełnym ekranie

                // Multi-monitor realizujemy przez rozpięcie NASZEGO okna na wirtualny pulpit
                // (span, w EnterFullscreen) — pełny ekran kontrolki (FullScreen + UseMultimon)
                // crashuje w WindowsFormsHost (SEH w DispatchMessage, 2026-07-02). UseMultimon
                // trzymamy na false, żeby Ctrl+Alt+Break nie wszedł w tę ścieżkę przypadkiem.
                try { ((IMsRdpClientNonScriptable5)s.Rdp.GetOcx()).UseMultimon = false; }
                catch { /* starsza kontrolka bez multimon — pomijamy */ }

                ApplyGateway(s);

                s.Rdp.Server = s.Server.Host;
                if (s.Server.UseWindowsAccount)
                {
                    s.Rdp.UserName = "";
                    s.Rdp.Domain = "";
                    adv.ClearTextPassword = "";
                }
                else
                {
                    s.Rdp.UserName = s.Server.Username;
                    s.Rdp.Domain = s.Server.Domain;
                    adv.ClearTextPassword = s.Password;
                }

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

        /// <summary>Konfiguruje bramę RD Gateway / jump-host, jeśli serwer ją ma. Bezpieczne dla starszych kontrolek.</summary>
        private static void ApplyGateway(Session s)
        {
            try
            {
                var ts = s.Rdp.TransportSettings;
                if (string.IsNullOrWhiteSpace(s.Server.GatewayHostname))
                {
                    ts.GatewayUsageMethod = 0; // brak bramy
                    return;
                }
                ts.GatewayHostname = s.Server.GatewayHostname;
                ts.GatewayUsageMethod = (uint)(s.Server.GatewayUsageMethod == 0 ? 1 : s.Server.GatewayUsageMethod);
                ts.GatewayProfileUsageMethod = 1; // 1 = jawnie z ustawień połączenia
                ts.GatewayCredsSource = 0;        // 0 = login/hasło (TSC_PROXY_CREDS_MODE_USERPASS)
            }
            catch (Exception) { /* kontrolka bez obsługi bramy — pomijamy */ }
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

        // ---------- SSH ----------

        /// <summary>Łączy sesję terminalową (SSH/Telnet/Serial): inicjalizuje xterm i transport w tle.</summary>
        private async void ConnectTerm(Session s)
        {
            // SSH wymaga loginu (nie ma odpowiednika konta Windows) — dopytaj, jeśli brak.
            if (s.IsSsh && string.IsNullOrWhiteSpace(s.Server.Username)) { PromptAndConnect(s, null); return; }
            if (s.Server.Protocol == RemoteProtocol.Telnet) WarnUnencrypted(RemoteProtocol.Telnet);
            try
            {
                SetTabStatus(s, ServerStatus.Idle);
                SetSessionStatus(s, string.Format(L("S.st.connecting"), s.Server.Host), StatusKind.Connecting);
                if (s == _active) UpdateCanvas();

                var (cols, rows) = await s.Term.InitAsync();
                string target = s.IsSsh
                    ? s.Server.Username + "@" + s.Server.Host + ":" + s.Server.Port
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
                        await s.Ssh.ConnectAsync(s.Server, s.Password, cols, rows);
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
                if (s == _active) { UpdateToolbarMode(); UpdateCanvas(); try { s.Rdp.Focus(); } catch { } }
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
                // Pasek połączenia: chowany też w trybie skupienia (adres jest na karcie).
                SessionToolbar.Visibility = (has && !IsImmersive()) ? Visibility.Visible : Visibility.Collapsed;
                TabStripHost.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            }
            if (_active == null) return;

            bool connected = _active.Connected;
            ConnectForm.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
            StatusPanel.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
            FilesBtn.Visibility = _active.IsSsh ? Visibility.Visible : Visibility.Collapsed;   // SFTP tylko dla SSH
            if (connected)
                StatusHost.Text = _active.Server.Host + ":" + _active.Server.Port;
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
            if (string.IsNullOrEmpty(pw) && server.SavePassword) CredentialStore.TryRead(server.CredTarget, out pw);
            var win = new SessionWindow(server, _settings, pw, PersistServers, DockSessionFromWindow);
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
            SessionHotZoneRow.Height = new GridLength(6);

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

        private Action _qsFirstAction;   // Enter w szukajce = pierwsze trafienie

        private void QuickSwitch_Click(object sender, RoutedEventArgs e)
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
            if (e.Key == Key.Enter && _qsFirstAction != null) { _qsFirstAction(); e.Handled = true; }
            else if (e.Key == Key.Escape) { QuickSwitchPopup.IsOpen = false; e.Handled = true; }
        }

        private void BuildQuickSwitchLists(string filter)
        {
            filter = (filter ?? "").Trim().ToLowerInvariant();
            QsSessions.Children.Clear();
            QsServers.Children.Clear();
            _qsFirstAction = null;

            foreach (var s in _sessions)
            {
                if (!RdpUtils.MatchesFilter(s.Server, filter)) continue;
                var session = s;
                Action go = () => { QuickSwitchPopup.IsOpen = false; Activate(session); };
                if (_qsFirstAction == null && s != _active) _qsFirstAction = go;
                QsSessions.Children.Add(BuildFlyoutRow(s.Server,
                    s.Connected ? ServerStatus.Online : ServerStatus.Offline, s == _active, go));
            }

            foreach (var server in _vm.Servers)
            {
                if (!RdpUtils.MatchesFilter(server, filter)) continue;
                var srv = server;
                Action go = () => { QuickSwitchPopup.IsOpen = false; LaunchServer(srv, true); };
                if (_qsFirstAction == null) _qsFirstAction = go;
                QsServers.Children.Add(BuildFlyoutRow(server, server.Status, false, go));
            }
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
                Background = isActive ? (Brush)TryFindResource("AccentSoft") : Brushes.Transparent,
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
                Text = server.Name, Foreground = (Brush)TryFindResource("TextPrim"), FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(name, 1);
            grid.Children.Add(name);

            var dot = new Ellipse { Width = 7, Height = 7, Fill = StatusBrush(dotStatus), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(dot, 2);
            grid.Children.Add(dot);

            row.Child = grid;
            row.MouseEnter += (s, e) => { if (!isActive) row.Background = (Brush)TryFindResource("Elevated"); };
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
                Group = "Szybkie", Status = ServerStatus.Offline
            };
            srv.Initials = RdpUtils.MakeInitials(srv.Name);

            // Tymczasowy — nie trafia do _vm.Servers ani do JSON; otwieramy sesję i łączymy
            // (jeśli brak poświadczeń, zapyta o nie prompt).
            LaunchServer(srv, autoConnect: true, forceNew: true);
        }

        private void AddServer_Click(object sender, RoutedEventArgs e)
        {
            var server = new ServerInfo { Group = "Serwery", Status = ServerStatus.Offline, Port = _settings.DefaultPort };
            var dlg = new ServerEditWindow(server, "") { Owner = this };
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

            int imported = 0;
            foreach (var path in dlg.FileNames)
            {
                try
                {
                    var server = RdpFile.Parse(System.IO.File.ReadAllText(path));
                    server.Group = "Zaimportowane";
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
                    Group = "Zaimportowane z mstsc",
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

            var dlg = new ServerEditWindow(copy, pw) { Owner = this };
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

            var dlg = new ServerEditWindow(server, current) { Owner = this };
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

        private void PersistServers() => ServerRepository.Save(_vm.Servers.ToList());

        private void SaveCredential(ServerInfo server, string password)
        {
            if (server.SavePassword && !string.IsNullOrEmpty(password))
                CredentialStore.Save(server.CredTarget, server.Username, password);
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

        private Brush AvatarBrush(string group)
        {
            switch (group)
            {
                case "Produkcja": return (Brush)TryFindResource("AvProd");
                case "Staging": return (Brush)TryFindResource("AvStaging");
                case "Klienci": return (Brush)TryFindResource("AvClient");
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
                case "Produkcja": return (Brush)TryFindResource("GdProd");
                case "Staging": return (Brush)TryFindResource("GdStaging");
                case "Klienci": return (Brush)TryFindResource("GdClient");
            }
            return AvatarBrush(group) is LinearGradientBrush g
                ? new SolidColorBrush(g.GradientStops[0].Color)
                : (Brush)TryFindResource("GdClient");
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
                case ServerStatus.Online: return (Brush)TryFindResource("Online");
                case ServerStatus.Idle: return (Brush)TryFindResource("Idle");
                default: return (Brush)TryFindResource("Offline");
            }
        }

        /// <summary>Tekstowy odpowiednik statusu (dla czytników ekranu — status nie tylko kolorem).</summary>
        private static string StatusText(ServerStatus status)
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
                case StatusKind.Connecting: return (Brush)TryFindResource("Idle");
                case StatusKind.Ok: return (Brush)TryFindResource("Online");
                case StatusKind.Error: return (Brush)TryFindResource("Danger");
                default: return (Brush)TryFindResource("TextSec");
            }
        }
    }
}
