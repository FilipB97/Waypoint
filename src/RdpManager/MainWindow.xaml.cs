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
using System.Windows.Forms.Integration;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using AxMSTSCLib;
using MSTSCLib;
using RdpManager.Core;
using RdpManager.Models;

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
        private readonly List<ServerInfo> _allServers = new List<ServerInfo>();

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

        // Opóźnienie pojawienia się paska pełnoekranowego (jak w mstsc) + polling pozycji kursora.
        private DispatcherTimer _fsBarDelay;
        private DispatcherTimer _fsCursorPoll;
        private RECT _fsMonRect;   // prostokąt monitora w pikselach fizycznych (do wykrycia górnej krawędzi)

        public MainWindow()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _settings = SettingsStore.Load();
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
        }

        private void SetNav(Button b, Wpf.Ui.Controls.SymbolIcon ico, bool active)
        {
            b.Background = active ? (Brush)Resources["AccentSoft"] : Brushes.Transparent;
            ico.Foreground = active ? (Brush)Resources["Accent"] : (Brush)Resources["TextTer"];
        }

        private void Avatar_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "RDP Manager\nNowoczesny menedżer połączeń RDP (WPF / Fluent).\n\nFolder danych:\n" + SettingsStore.Dir,
                "O aplikacji", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ---------- Zoom interfejsu (Ctrl + kółko / Ctrl +/- / Ctrl 0) ----------

        private void ZoomTo(double scale)
        {
            scale = Math.Round(Math.Clamp(scale, 0.7, 1.8), 2);
            _settings.UiScale = scale;
            RootScale.ScaleX = RootScale.ScaleY = scale;
            if (SettingsView.Visibility == Visibility.Visible)
                SetUiScale.Text = ((int)Math.Round(scale * 100)).ToString();
            SettingsStore.Save(_settings);
        }

        private void Window_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || _isFullscreen) return;
            ZoomTo(_settings.UiScale + (e.Delta > 0 ? 0.1 : -0.1));
            e.Handled = true;
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_settings.ConfirmCloseConnected && _sessions.Any(s => s.Connected) &&
                MessageBox.Show("Są aktywne połączenia. Zamknąć aplikację?", "Zamknij",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            foreach (var s in _sessions)
            {
                try { s.Resizer?.Dispose(); } catch { }
                try { s.Rdp.Disconnect(); } catch { }
            }
        }

        // ---------- Ustawienia ----------

        private void LoadSettingsForm()
        {
            SetUiScale.Text = ((int)Math.Round(_settings.UiScale * 100)).ToString();
            SetBarDelay.Text = _settings.FullscreenBarDelayMs.ToString();
            SetDefaultPort.Text = _settings.DefaultPort.ToString();
            SetColorDepth.SelectedIndex = _settings.ColorDepth == 16 ? 0 : _settings.ColorDepth == 24 ? 1 : 2;
            SetAutoReconnect.IsChecked = _settings.AutoReconnect;
            SetReachEnabled.IsChecked = _settings.ReachabilityEnabled;
            SetReachInterval.Text = _settings.ReachabilityIntervalSec.ToString();
            SetConfirmClose.IsChecked = _settings.ConfirmCloseConnected;
            SetDataPath.Text = SettingsStore.Dir;
            SettingsStatus.Text = "";
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

            SettingsStore.Save(_settings);
            ApplySettings();
            SettingsStatus.Text = "Zapisano ✓";
        }

        private int ParseColorDepth()
        {
            var text = (SetColorDepth.SelectedItem as ComboBoxItem)?.Content?.ToString();
            return RdpUtils.ParseColorDepth(text);
        }

        private void ApplySettings()
        {
            RootScale.ScaleX = RootScale.ScaleY = Math.Clamp(_settings.UiScale, 0.7, 1.8);
            _fsBarDelay.Interval = TimeSpan.FromMilliseconds(_settings.FullscreenBarDelayMs);
            _reachTimer.Interval = TimeSpan.FromSeconds(_settings.ReachabilityIntervalSec);
            if (_settings.ReachabilityEnabled)
            {
                if (!_reachTimer.IsEnabled) _reachTimer.Start();
                CheckReachabilityAsync();
            }
            else
            {
                _reachTimer.Stop();
            }
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

        // ---------- Ostatnie / Pulpit ----------

        private void RecordRecent(ServerInfo server)
        {
            if (string.IsNullOrEmpty(server?.Id)) return;
            _settings.RecentIds.Remove(server.Id);
            _settings.RecentIds.Insert(0, server.Id);
            if (_settings.RecentIds.Count > 15)
                _settings.RecentIds.RemoveRange(15, _settings.RecentIds.Count - 15);
            SettingsStore.Save(_settings);
        }

        private void BuildRecent()
        {
            RecentPanel.Children.Clear();
            bool any = false;
            foreach (var id in _settings.RecentIds)
            {
                var server = _allServers.Find(x => x.Id == id);
                if (server == null) continue;
                any = true;
                var srv = server;
                RecentPanel.Children.Add(BuildFlyoutRow(srv, srv.Status, false, () => OpenServer(srv, true)));
            }
            if (!any)
                RecentPanel.Children.Add(new TextBlock { Text = "Brak ostatnich połączeń.", Foreground = (Brush)Resources["TextTer"] });
        }

        private void BuildDashboard()
        {
            DashboardPanel.Children.Clear();

            int total = _allServers.Count;
            int online = _allServers.Count(s => s.Status == ServerStatus.Online);
            int open = _sessions.Count;

            var cards = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 22) };
            cards.Children.Add(StatCard("Serwery", total.ToString()));
            cards.Children.Add(StatCard("Osiągalne", online.ToString()));
            cards.Children.Add(StatCard("Otwarte sesje", open.ToString()));
            DashboardPanel.Children.Add(cards);

            DashboardPanel.Children.Add(new TextBlock
            {
                Text = "Ostatnio używane", Foreground = (Brush)Resources["TextSec"],
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8)
            });

            int shown = 0;
            foreach (var id in _settings.RecentIds)
            {
                var server = _allServers.Find(x => x.Id == id);
                if (server == null) continue;
                var srv = server;
                DashboardPanel.Children.Add(BuildFlyoutRow(srv, srv.Status, false, () => OpenServer(srv, true)));
                if (++shown >= 5) break;
            }
            if (shown == 0)
                DashboardPanel.Children.Add(new TextBlock { Text = "Brak historii.", Foreground = (Brush)Resources["TextTer"] });
        }

        private FrameworkElement StatCard(string label, string value)
        {
            var card = new Border
            {
                Background = (Brush)Resources["Panel"], BorderBrush = (Brush)Resources["Border"], BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10), Padding = new Thickness(18, 14, 18, 14), Margin = new Thickness(0, 0, 12, 0), MinWidth = 130
            };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = value, Foreground = (Brush)Resources["Accent"], FontSize = 26, FontWeight = FontWeights.Bold });
            sp.Children.Add(new TextBlock { Text = label, Foreground = (Brush)Resources["TextSec"], FontSize = 12 });
            card.Child = sp;
            return card;
        }

        // Wyśrodkuj pasek u samej góry względem celu (HotZone rozciąga się na całą szerokość obszaru).
        private CustomPopupPlacement[] PlaceFsPopup(Size popupSize, Size targetSize, Point offset)
        {
            double x = (targetSize.Width - popupSize.Width) / 2.0;
            return new[] { new CustomPopupPlacement(new Point(x, 0), PopupPrimaryAxis.Horizontal) };
        }

        // ---------- Drzewo serwerów ----------

        private void BuildServerTree()
        {
            _allServers.Clear();
            _allServers.AddRange(ServerRepository.Load());
            RenderTree();
        }

        private void RenderTree(string filter = null)
        {
            filter = (filter ?? "").Trim().ToLowerInvariant();
            ServerTree.Children.Clear();
            _serverRows.Clear();
            _serverAccent.Clear();
            _serverStatusDot.Clear();

            var order = new List<string>();
            var byGroup = new Dictionary<string, List<ServerInfo>>();
            foreach (var s in _allServers)
            {
                if (!RdpUtils.MatchesFilter(s, filter)) continue;
                var g = string.IsNullOrWhiteSpace(s.Group) ? "Serwery" : s.Group;
                if (!byGroup.ContainsKey(g)) { order.Add(g); byGroup[g] = new List<ServerInfo>(); }
                byGroup[g].Add(s);
            }
            foreach (var g in order)
            {
                ServerTree.Children.Add(BuildGroupHeader(new ServerGroup { Name = g }));
                foreach (var s in byGroup[g])
                    ServerTree.Children.Add(BuildServerRow(s));
            }
            UpdateActiveRows();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RenderTree(SearchBox.Text);

        private FrameworkElement BuildGroupHeader(ServerGroup group)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 10, 6, 4) };
            sp.Children.Add(new Ellipse
            {
                Width = 6, Height = 6, Fill = GroupDotBrush(group.Name),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            });
            sp.Children.Add(new TextBlock
            {
                Text = group.Name.ToUpperInvariant(),
                Foreground = (Brush)Resources["TextSec"],
                FontSize = 11.5, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            return sp;
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
                Width = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = (Brush)Resources["Accent"],
                VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(0, 2, 0, 2),
                Visibility = Visibility.Collapsed
            };
            Grid.SetColumn(accent, 0);
            grid.Children.Add(accent);

            var avatar = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(6),
                Background = AvatarBrush(server.Group), Margin = new Thickness(8, 0, 0, 0),
                Child = new TextBlock
                {
                    Text = server.Initials, Foreground = Brushes.White, FontSize = 9.5, FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            };
            Grid.SetColumn(avatar, 1);
            grid.Children.Add(avatar);

            var meta = new StackPanel { Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            meta.Children.Add(new TextBlock { Text = server.Name, Foreground = (Brush)Resources["TextPrim"], FontSize = 12.5 });
            meta.Children.Add(new TextBlock
            {
                Text = server.Host, Foreground = (Brush)Resources["TextTer"], FontSize = 10.5,
                FontFamily = (FontFamily)Resources["Mono"], TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetColumn(meta, 2);
            grid.Children.Add(meta);

            var status = new Ellipse
            {
                Width = 7, Height = 7, Fill = StatusBrush(server.Status),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(status, 3);
            grid.Children.Add(status);
            _serverStatusDot[server] = status;

            row.Child = grid;

            row.MouseEnter += (s, e) => { if (_active?.Server != server) row.Background = (Brush)Resources["Elevated"]; };
            row.MouseLeave += (s, e) => { if (_active?.Server != server) row.Background = Brushes.Transparent; };
            row.MouseLeftButtonUp += (s, e) => OpenServer(server, true);

            var menu = new ContextMenu();
            var connectAsItem = new MenuItem { Header = "Połącz jako…" };
            connectAsItem.Click += (s, e) =>
            {
                OpenServer(server);
                if (_active?.Server == server) PromptAndConnect(_active, "Połącz z innymi poświadczeniami.");
            };
            var editItem = new MenuItem { Header = "Edytuj…" };
            editItem.Click += (s, e) => EditServer(server);
            var exportItem = new MenuItem { Header = "Eksportuj .rdp…" };
            exportItem.Click += (s, e) => ExportRdp(server);
            var delItem = new MenuItem { Header = "Usuń" };
            delItem.Click += (s, e) => DeleteServer(server);
            menu.Items.Add(connectAsItem);
            menu.Items.Add(editItem);
            menu.Items.Add(exportItem);
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
                kv.Value.Background = active ? (Brush)Resources["AccentSoft"] : Brushes.Transparent;
                _serverAccent[kv.Key].Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // ---------- Otwieranie / przełączanie sesji ----------

        private void OpenServer(ServerInfo server, bool autoConnect = false)
        {
            ShowView("Sessions");   // kontrolka RDP musi powstać przy widocznym widoku sesji
            var existing = _sessions.Find(x => x.Server == server);
            if (existing != null)
            {
                Activate(existing);
                if (autoConnect && !existing.Connected) BeginConnect(existing);
                return;
            }

            var rdp = new AxMsRdpClient11NotSafeForScripting();
            var host = new WindowsFormsHost();

            ((ISupportInitialize)rdp).BeginInit();
            rdp.Dock = System.Windows.Forms.DockStyle.Fill;
            host.Child = rdp;
            ((ISupportInitialize)rdp).EndInit();

            SessionContainer.Children.Add(host);
            host.UpdateLayout();
            try { ((System.Windows.Forms.Control)rdp).CreateControl(); } catch { }  // wymuś utworzenie kontrolki ActiveX

            var session = new Session(server, rdp, host);
            if (server.SavePassword && CredentialStore.TryRead(server.CredTarget, out var savedPw))
                session.Password = savedPw;
            session.Resizer = new RdpDynamicResolution(session, host);
            WireEvents(session);

            _sessions.Add(session);
            session.TabButton = BuildTab(session);
            TabStrip.Children.Add(session.TabButton);
            RefreshTabTitles();

            Activate(session);
            if (autoConnect) BeginConnect(session);
        }

        private static bool CanAuto(Session s) => s.Server.UseWindowsAccount || !string.IsNullOrEmpty(s.Password);

        private void Activate(Session session)
        {
            _active = session;
            RefreshTabStyles();
            UpdateActiveRows();
            LoadToolbar(session);
            UpdateToolbarEnabled();
            UpdateToolbarMode();
            UpdateCanvas();
            SetStatus(session.Status, session.StatusKind);
            FsName.Text = session.Server.Name + " · " + session.Server.Host;
        }

        /// <summary>
        /// Steruje kanwą: aktywna kontrolka RDP widoczna tylko gdy połączona; w przeciwnym razie
        /// nakładka (spinner „Łączenie…" albo „Rozłączono" + przycisk ponownego połączenia).
        /// </summary>
        private void UpdateCanvas()
        {
            bool has = _active != null;
            EmptyHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;

            foreach (var s in _sessions)
                s.Host.Visibility = (s == _active && s.Connected) ? Visibility.Visible : Visibility.Collapsed;

            if (!has || _active.Connected)
            {
                SessionOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            SessionOverlay.Visibility = Visibility.Visible;
            bool connecting = _active.StatusKind == StatusKind.Connecting;
            OverlaySpinner.Visibility = connecting ? Visibility.Visible : Visibility.Collapsed;
            OverlayReconnect.Visibility = connecting ? Visibility.Collapsed : Visibility.Visible;
            OverlayTitle.Text = connecting
                ? "Łączenie z " + _active.Server.Host + "…"
                : (_active.StatusKind == StatusKind.Error ? "Rozłączono" : "Gotowe do połączenia");
            OverlayMsg.Text = connecting ? "" : _active.Status;
        }

        private void LoadToolbar(Session s)
        {
            CfAvatar.Background = AvatarBrush(s.Server.Group);
            CfAvatarText.Text = s.Server.Initials;
            CfName.Text = s.Server.Name;
            CfHost.Text = s.Server.Host + ":" + s.Server.Port;
            WinAuthCheck.IsChecked = s.Server.UseWindowsAccount;
            PassBox.Password = s.Password ?? "";
            UpdatePassVisibility();
        }

        // ---------- Pasek zakładek ----------

        private FrameworkElement BuildTab(Session session)
        {
            var tab = new Border
            {
                CornerRadius = new CornerRadius(6, 6, 0, 0),
                BorderThickness = new Thickness(1, 1, 1, 0),
                BorderBrush = Brushes.Transparent,
                Background = Brushes.Transparent,
                Padding = new Thickness(10, 7, 8, 7),
                Margin = new Thickness(0, 0, 2, 0),
                Cursor = Cursors.Hand,
                Tag = session
            };

            var grid = new Grid();

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new Border
            {
                Width = 16, Height = 16, CornerRadius = new CornerRadius(4),
                Background = AvatarBrush(session.Server.Group), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = session.Server.Initials, Foreground = Brushes.White, FontSize = 7, FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            });
            var tabName = new TextBlock
            {
                Text = session.Server.Name, Foreground = (Brush)Resources["TextPrim"], FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0)
            };
            _tabName[session] = tabName;
            content.Children.Add(tabName);
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
                Text = "✕", Foreground = (Brush)Resources["TextTer"], FontSize = 12,
                Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand
            };
            close.MouseLeftButtonUp += (s, e) => { e.Handled = true; RequestCloseSession(session); };
            content.Children.Add(close);
            grid.Children.Add(content);

            var underline = new Rectangle
            {
                Height = 2, Fill = (Brush)Resources["Accent"], RadiusX = 1, RadiusY = 1,
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6, 0, 6, 0),
                Visibility = Visibility.Collapsed
            };
            grid.Children.Add(underline);

            tab.Child = grid;
            tab.MouseLeftButtonUp += (s, e) => Activate(session);

            var tabMenu = new ContextMenu();
            var closeOthers = new MenuItem { Header = "Zamknij pozostałe" };
            closeOthers.Click += (s, e) => CloseOtherSessions(session);
            var closeThis = new MenuItem { Header = "Zamknij" };
            closeThis.Click += (s, e) => RequestCloseSession(session);
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
                b.Background = active ? (Brush)Resources["Panel"] : Brushes.Transparent;
                b.BorderBrush = active ? (Brush)Resources["Border"] : Brushes.Transparent;
                if (_tabUnderline.TryGetValue(s, out var u))
                    u.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        /// <summary>Gdy kilka otwartych sesji ma tę samą nazwę serwera, dopisuje host, by je rozróżnić.</summary>
        private void RefreshTabTitles()
        {
            foreach (var s in _sessions)
            {
                if (!_tabName.TryGetValue(s, out var tn)) continue;
                bool dup = _sessions.Any(o => o != s &&
                    string.Equals(o.Server.Name, s.Server.Name, StringComparison.OrdinalIgnoreCase));
                tn.Text = dup ? s.Server.Name + " (" + s.Server.Host + ")" : s.Server.Name;
            }
        }

        private void CloseOtherSessions(Session keep)
        {
            foreach (var s in _sessions.ToList())
                if (s != keep) RequestCloseSession(s);
        }

        private void RequestCloseSession(Session session)
        {
            if (session.Connected && _settings.ConfirmCloseConnected &&
                MessageBox.Show("Zamknąć połączoną sesję \"" + session.Server.Name + "\"?", "Zamknij sesję",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            CloseSession(session);
        }

        private void CloseSession(Session session)
        {
            try { session.Rdp.Disconnect(); } catch { /* nie połączona */ }
            session.Resizer?.Dispose();

            SessionContainer.Children.Remove(session.Host);
            TabStrip.Children.Remove(session.TabButton);
            _tabUnderline.Remove(session);
            _tabStatus.Remove(session);
            _tabName.Remove(session);
            _sessions.Remove(session);
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
            try { s.Rdp.Disconnect(); } catch { /* nie połączona */ }
            s.LoggedIn = false;

            try
            {
                IMsRdpClientAdvancedSettings8 adv = s.Rdp.AdvancedSettings9;
                adv.RDPPort = s.Server.Port;
                // Weryfikacja tożsamości serwera (domyślnie 2 = ostrzegaj) — chroni przed MITM.
                adv.AuthenticationLevel = (uint)Math.Clamp(s.Server.AuthenticationLevel, 0, 2);
                adv.EnableCredSspSupport = true;
                adv.SmartSizing = false;   // dynamiczna rozdzielczość zajmie się dopasowaniem
                adv.EnableAutoReconnect = _settings.AutoReconnect;
                s.Rdp.ColorDepth = _settings.ColorDepth;
                adv.RedirectClipboard = s.Server.RedirectClipboard;
                adv.RedirectDrives = s.Server.RedirectDrives;
                adv.RedirectPrinters = s.Server.RedirectPrinters;
                adv.AudioRedirectionMode = (uint)Math.Clamp(s.Server.AudioMode, 0, 2);
                try { s.Rdp.SecuredSettings2.KeyboardHookMode = 2; } catch { }  // Alt+Tab/Win -> zdalna w pełnym ekranie

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

                s.Rdp.Connect();
                SetSessionStatus(s, "Łączenie z " + s.Server.Host + "…", StatusKind.Connecting);
            }
            catch (Exception ex)
            {
                SetSessionStatus(s, "Wyjątek: " + ex.Message, StatusKind.Error);
            }
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

        private void Disconnect_Click(object sender, RoutedEventArgs e)
        {
            if (_active == null) return;
            try { _active.Rdp.Disconnect(); } catch (Exception ex) { SetSessionStatus(_active, "Rozłączanie: " + ex.Message, StatusKind.Error); }
        }

        private void WireEvents(Session s)
        {
            s.Rdp.OnConnecting += (o, a) =>
            {
                SetSessionStatus(s, "Łączenie…", StatusKind.Connecting);
                SetTabStatus(s, ServerStatus.Idle);
                if (s == _active) UpdateCanvas();
            };
            s.Rdp.OnConnected += (o, a) =>
            {
                s.Connected = true;
                RecordRecent(s.Server);
                SetTabStatus(s, ServerStatus.Online);
                SetSessionStatus(s, "● Połączono", StatusKind.Ok);
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

                // Bezpieczeństwo: nie trzymaj hasła w pamięci po rozłączeniu, jeśli nie jest
                // zapisane w Credential Managerze. Ponowne połączenie wymaga wpisania go na nowo.
                if (!s.Server.SavePassword) s.Password = "";

                string msg = "Rozłączono: " + DescribeDisconnect(s.Rdp, a.discReason);
                if (!wasLoggedIn)
                {
                    msg += s.Server.UseWindowsAccount
                        ? "  Wskazówka: konto Windows może nie mieć dostępu do hosta — odznacz „Konto Windows” i podaj login/hasło."
                        : "  Wskazówka: sprawdź login, hasło, domenę i dostępność hosta.";
                }
                SetSessionStatus(s, msg, StatusKind.Error);
                if (s == _active) { UpdateToolbarMode(); UpdateCanvas(); }
            };
            s.Rdp.OnFatalError += (o, a) =>
            {
                s.Connected = false;
                SetTabStatus(s, ServerStatus.Offline);
                SetSessionStatus(s, "Błąd krytyczny (errorCode " + a.errorCode + ")", StatusKind.Error);
                if (s == _active) { UpdateToolbarMode(); UpdateCanvas(); }
            };
        }

        private void SetTabStatus(Session s, ServerStatus status)
        {
            if (_tabStatus.TryGetValue(s, out var dot)) dot.Fill = StatusBrush(status);
        }

        // ---------- Konto Windows ----------

        private void WinAuth_Changed(object sender, RoutedEventArgs e) => UpdatePassVisibility();

        private void UpdatePassVisibility()
        {
            CfPassGroup.Visibility = WinAuthCheck.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        }

        // ---------- Pasek sesji: dwa stany ----------

        private void UpdateToolbarMode()
        {
            // W pełnym ekranie widocznością paska/zakładek steruje Enter/ExitFullscreen — nie dotykamy jej tutaj.
            if (!_isFullscreen)
            {
                bool has = _active != null;
                SessionToolbar.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
                TabStripHost.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            }
            if (_active == null) return;

            bool connected = _active.Connected;
            ConnectForm.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
            StatusPanel.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
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

            AppTitleBar.Visibility = Visibility.Collapsed;
            Rail.Visibility = Visibility.Collapsed;
            Sidebar.Visibility = Visibility.Collapsed;
            TabStripHost.Visibility = Visibility.Collapsed;
            SessionToolbar.Visibility = Visibility.Collapsed;

            WindowState = WindowState.Normal;   // trzeba być Normal, żeby ręcznie ustawić granice
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;

            // Cały prostokąt monitora (rcMonitor, NIE rcWork) => zakrywa pasek zadań; Topmost trzyma nad nim.
            if (TryGetMonitorRectDip(out Rect r))
            {
                Left = r.Left; Top = r.Top; Width = r.Width; Height = r.Height;
                Topmost = true;
            }
            else
            {
                WindowState = WindowState.Maximized;   // awaryjnie (bez zakrycia paska)
            }

            _isFullscreen = true;
            _fsCursorPoll.Start();
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

            FsPopup.IsOpen = false;
            _isFullscreen = false;
        }

        /// <summary>Pełny prostokąt monitora, na którym jest okno, przeliczony na DIP.</summary>
        private bool TryGetMonitorRectDip(out Rect rect)
        {
            rect = default;
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return false;

            IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(mon, ref mi)) return false;
            _fsMonRect = mi.rcMonitor;   // piksele fizyczne — do pollingu górnej krawędzi

            var src = PresentationSource.FromVisual(this);
            if (src?.CompositionTarget == null) return false;
            Matrix toDip = src.CompositionTarget.TransformFromDevice;

            Point tl = toDip.Transform(new Point(mi.rcMonitor.left, mi.rcMonitor.top));
            Point br = toDip.Transform(new Point(mi.rcMonitor.right, mi.rcMonitor.bottom));
            rect = new Rect(tl, br);
            return true;
        }

        private const int MONITOR_DEFAULTTONEAREST = 0x2;

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

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
            InnyBtn.Content = "inne połączenia  ▴";
        }

        private void CollapseFlyout()
        {
            FsFlyout.Visibility = Visibility.Collapsed;
            InnyBtn.Content = "inne połączenia  ▾";
        }

        private void FsFlyoutSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            BuildFlyoutLists(FsFlyoutSearch.Text);
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
                FlyoutSessions.Children.Add(BuildFlyoutRow(s.Server, dot, s == _active, () => HandleFlyoutClick(session.Server)));
            }

            foreach (var server in _allServers)
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
                Background = isActive ? (Brush)Resources["AccentSoft"] : Brushes.Transparent,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 1, 0, 1)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var avatar = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(5), Background = AvatarBrush(server.Group),
                Child = new TextBlock
                {
                    Text = server.Initials, Foreground = Brushes.White, FontSize = 7.5, FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            };
            Grid.SetColumn(avatar, 0);
            grid.Children.Add(avatar);

            var name = new TextBlock
            {
                Text = server.Name, Foreground = (Brush)Resources["TextPrim"], FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(name, 1);
            grid.Children.Add(name);

            var dot = new Ellipse { Width = 7, Height = 7, Fill = StatusBrush(dotStatus), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(dot, 2);
            grid.Children.Add(dot);

            row.Child = grid;
            row.MouseEnter += (s, e) => { if (!isActive) row.Background = (Brush)Resources["Elevated"]; };
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

        private void AddServer_Click(object sender, RoutedEventArgs e)
        {
            var server = new ServerInfo { Group = "Serwery", Status = ServerStatus.Offline, Port = _settings.DefaultPort };
            var dlg = new ServerEditWindow(server, "") { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                _allServers.Add(server);
                PersistServers();
                SaveCredential(server, dlg.EnteredPassword);
                RenderTree(SearchBox.Text);
                CheckReachabilityAsync();
            }
        }

        private void ImportRdp_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Importuj plik .rdp",
                Filter = "Pliki Podłączania pulpitu zdalnego (*.rdp)|*.rdp|Wszystkie pliki (*.*)|*.*",
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
                    _allServers.Add(server);
                    imported++;
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Nie udało się zaimportować \"" + System.IO.Path.GetFileName(path) + "\":\n" + ex.Message,
                        "Import .rdp", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

            if (imported > 0)
            {
                PersistServers();
                RenderTree(SearchBox.Text);
                CheckReachabilityAsync();
                SetStatus("Zaimportowano serwerów: " + imported, StatusKind.Ok);
            }
        }

        private void ExportRdp(ServerInfo server)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Eksportuj do .rdp",
                Filter = "Pliki Podłączania pulpitu zdalnego (*.rdp)|*.rdp",
                FileName = MakeSafeFileName(server.Name ?? server.Host ?? "serwer") + ".rdp"
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                System.IO.File.WriteAllText(dlg.FileName, RdpFile.Serialize(server));
                SetStatus("Wyeksportowano do " + System.IO.Path.GetFileName(dlg.FileName), StatusKind.Ok);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Nie udało się zapisać pliku:\n" + ex.Message,
                    "Eksport .rdp", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static string MakeSafeFileName(string name)
        {
            foreach (var c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "serwer" : name;
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

                // odśwież otwartą sesję tego serwera (etykieta zakładki + pasek)
                var open = _sessions.Find(x => x.Server == server);
                if (open != null)
                {
                    RefreshTabTitles();
                    if (open == _active)
                    {
                        LoadToolbar(open);
                        UpdateToolbarMode();
                        FsName.Text = server.Name + " · " + server.Host;
                    }
                }
            }
        }

        private void DeleteServer(ServerInfo server)
        {
            if (MessageBox.Show("Usunąć serwer \"" + server.Name + "\"?", "Usuń serwer",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            var open = _sessions.Find(x => x.Server == server);
            if (open != null) CloseSession(open);

            _allServers.Remove(server);
            CredentialStore.Delete(server.CredTarget);
            PersistServers();
            RenderTree(SearchBox.Text);
            CheckReachabilityAsync();
        }

        private void PersistServers() => ServerRepository.Save(_allServers);

        private void SaveCredential(ServerInfo server, string password)
        {
            if (server.SavePassword && !string.IsNullOrEmpty(password))
                CredentialStore.Save(server.CredTarget, server.Username, password);
            else
                CredentialStore.Delete(server.CredTarget);   // nie zapisujemy / kasujemy stare
        }

        private Brush AvatarBrush(string group)
        {
            switch (group)
            {
                case "Produkcja": return (Brush)Resources["AvProd"];
                case "Staging": return (Brush)Resources["AvStaging"];
                case "Klienci": return (Brush)Resources["AvClient"];
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
                case "Produkcja": return (Brush)Resources["GdProd"];
                case "Staging": return (Brush)Resources["GdStaging"];
                case "Klienci": return (Brush)Resources["GdClient"];
            }
            return AvatarBrush(group) is LinearGradientBrush g
                ? new SolidColorBrush(g.GradientStops[0].Color)
                : (Brush)Resources["GdClient"];
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
                case ServerStatus.Online: return (Brush)Resources["Online"];
                case ServerStatus.Idle: return (Brush)Resources["Idle"];
                default: return (Brush)Resources["Offline"];
            }
        }

        // ---------- Osiągalność serwerów (kropki statusu w drzewie) ----------

        private async void CheckReachabilityAsync()
        {
            if (_reachBusy) return;
            _reachBusy = true;
            try
            {
                var servers = _allServers.ToList();
                var results = await Task.WhenAll(servers.Select(srv =>
                    Task.Run(() => new KeyValuePair<ServerInfo, ServerStatus>(srv, Probe(srv.Host, srv.Port)))));

                foreach (var kv in results)
                {
                    kv.Key.Status = kv.Value;
                    if (_serverStatusDot.TryGetValue(kv.Key, out var dot))
                        dot.Fill = StatusBrush(kv.Value);
                }
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
                case StatusKind.Connecting: return (Brush)Resources["Idle"];
                case StatusKind.Ok: return (Brush)Resources["Online"];
                case StatusKind.Error: return (Brush)Resources["Danger"];
                default: return (Brush)Resources["TextSec"];
            }
        }
    }
}
