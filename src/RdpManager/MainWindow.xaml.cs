using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AxMSTSCLib;
using MSTSCLib;
using RdpManager.Models;

namespace RdpManager
{
    public partial class MainWindow
    {
        private readonly List<Session> _sessions = new List<Session>();
        private Session _active;

        private readonly Dictionary<ServerInfo, Border> _serverRows = new Dictionary<ServerInfo, Border>();
        private readonly Dictionary<ServerInfo, Rectangle> _serverAccent = new Dictionary<ServerInfo, Rectangle>();
        private readonly Dictionary<Session, Rectangle> _tabUnderline = new Dictionary<Session, Rectangle>();

        private WindowStyle _prevStyle;
        private WindowState _prevState;
        private ResizeMode _prevResize;
        private bool _isFullscreen;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            BuildServerTree();
            UpdateToolbarEnabled();
            UpdateToolbarMode();
        }

        // ---------- Drzewo serwerów ----------

        private void BuildServerTree()
        {
            foreach (var group in TestData.Groups())
            {
                ServerTree.Children.Add(BuildGroupHeader(group));
                foreach (var server in group.Servers)
                    ServerTree.Children.Add(BuildServerRow(server));
            }
        }

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

            row.Child = grid;

            row.MouseEnter += (s, e) => { if (_active?.Server != server) row.Background = (Brush)Resources["Elevated"]; };
            row.MouseLeave += (s, e) => { if (_active?.Server != server) row.Background = Brushes.Transparent; };
            row.MouseLeftButtonUp += (s, e) => OpenServer(server);

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

        private void OpenServer(ServerInfo server)
        {
            var existing = _sessions.Find(x => x.Server == server);
            if (existing != null) { Activate(existing); return; }

            var rdp = new AxMsRdpClient11NotSafeForScripting();
            var host = new WindowsFormsHost { Visibility = Visibility.Visible };

            ((ISupportInitialize)rdp).BeginInit();
            rdp.Dock = System.Windows.Forms.DockStyle.Fill;
            host.Child = rdp;
            ((ISupportInitialize)rdp).EndInit();

            var session = new Session(server, rdp, host);
            session.Resizer = new RdpDynamicResolution(session, host);
            WireEvents(session);

            SessionContainer.Children.Add(host);
            _sessions.Add(session);
            session.TabButton = BuildTab(session);
            TabStrip.Children.Add(session.TabButton);

            Activate(session);
        }

        private void Activate(Session session)
        {
            _active = session;
            EmptyHint.Visibility = Visibility.Collapsed;

            foreach (var s in _sessions)
                s.Host.Visibility = s == session ? Visibility.Visible : Visibility.Collapsed;

            RefreshTabStyles();
            UpdateActiveRows();
            LoadToolbar(session);
            UpdateToolbarEnabled();
            UpdateToolbarMode();
            SetStatus(session.Status);
            FsName.Text = session.Server.Name + " · " + session.Server.Host;
        }

        private void LoadToolbar(Session s)
        {
            HostBox.Text = s.Server.Host;
            PortBox.Text = s.Server.Port.ToString();
            UserBox.Text = s.Server.Username;
            DomainBox.Text = s.Server.Domain;
            PassBox.Password = s.Password;
            WinAuthCheck.IsChecked = s.Server.UseWindowsAccount;
            ApplyWinAuthState();
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
            content.Children.Add(new TextBlock
            {
                Text = session.Server.Name, Foreground = (Brush)Resources["TextPrim"], FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0)
            });
            content.Children.Add(new Ellipse
            {
                Width = 6, Height = 6, Fill = StatusBrush(session.Server.Status),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0)
            });
            var close = new TextBlock
            {
                Text = "✕", Foreground = (Brush)Resources["TextTer"], FontSize = 12,
                Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand
            };
            close.MouseLeftButtonUp += (s, e) => { e.Handled = true; CloseSession(session); };
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

        private void CloseSession(Session session)
        {
            try { session.Rdp.Disconnect(); } catch { /* nie połączona */ }
            session.Resizer?.Dispose();

            SessionContainer.Children.Remove(session.Host);
            TabStrip.Children.Remove(session.TabButton);
            _tabUnderline.Remove(session);
            _sessions.Remove(session);

            if (_active == session)
            {
                _active = null;
                if (_sessions.Count > 0) Activate(_sessions[_sessions.Count - 1]);
                else
                {
                    EmptyHint.Visibility = Visibility.Visible;
                    UpdateActiveRows();
                    UpdateToolbarEnabled();
                    UpdateToolbarMode();
                    SetStatus("—");
                }
            }
        }

        // ---------- Połączenie ----------

        private void Connect_Click(object sender, RoutedEventArgs e)
        {
            if (_active == null) return;
            var s = _active;

            try { s.Rdp.Disconnect(); } catch { /* nie połączona */ }

            try
            {
                s.Server.Host = HostBox.Text.Trim();
                s.Server.Port = int.TryParse(PortBox.Text.Trim(), out var p) ? p : 3389;
                s.Server.UseWindowsAccount = WinAuthCheck.IsChecked == true;
                s.Server.Username = UserBox.Text.Trim();
                s.Server.Domain = DomainBox.Text.Trim();
                s.Password = PassBox.Password;

                IMsRdpClientAdvancedSettings8 adv = s.Rdp.AdvancedSettings9;
                adv.RDPPort = s.Server.Port;
                adv.AuthenticationLevel = 0;
                adv.EnableCredSspSupport = true;
                adv.SmartSizing = false;   // dynamiczna rozdzielczość zajmie się dopasowaniem

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
                SetSessionStatus(s, "Łączenie z " + s.Server.Host + "…");
            }
            catch (Exception ex)
            {
                SetSessionStatus(s, "Wyjątek: " + ex.Message);
            }
        }

        private void Disconnect_Click(object sender, RoutedEventArgs e)
        {
            if (_active == null) return;
            try { _active.Rdp.Disconnect(); } catch (Exception ex) { SetSessionStatus(_active, "Rozłączanie: " + ex.Message); }
        }

        private void WireEvents(Session s)
        {
            s.Rdp.OnConnecting += (o, a) => SetSessionStatus(s, "Łączenie…");
            s.Rdp.OnConnected += (o, a) =>
            {
                s.Connected = true;
                if (s == _active) UpdateToolbarMode();
            };
            s.Rdp.OnLoginComplete += (o, a) =>
            {
                s.Resizer?.ApplyInitial();
                if (s == _active) UpdateToolbarMode();
            };
            s.Rdp.OnDisconnected += (o, a) =>
            {
                s.Connected = false;
                SetSessionStatus(s, "Rozłączono (reason " + a.discReason + ")");
                if (s == _active) UpdateToolbarMode();
            };
            s.Rdp.OnFatalError += (o, a) => SetSessionStatus(s, "Błąd krytyczny: errorCode " + a.errorCode);
        }

        // ---------- Konto Windows ----------

        private void WinAuth_Changed(object sender, RoutedEventArgs e) => ApplyWinAuthState();

        private void ApplyWinAuthState()
        {
            bool win = WinAuthCheck.IsChecked == true;
            bool has = _active != null;
            UserBox.IsEnabled = has && !win;
            DomainBox.IsEnabled = has && !win;
            PassBox.IsEnabled = has && !win;
        }

        // ---------- Pasek sesji: dwa stany ----------

        private void UpdateToolbarMode()
        {
            bool connected = _active != null && _active.Connected;
            ConnectForm.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
            StatusPanel.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
            if (connected)
                StatusHost.Text = _active.Server.Host + ":" + _active.Server.Port;
        }

        private void UpdateToolbarEnabled()
        {
            bool has = _active != null;
            HostBox.IsEnabled = has;
            PortBox.IsEnabled = has;
            WinAuthCheck.IsEnabled = has;
            ConnectBtn.IsEnabled = has;
            ApplyWinAuthState();
        }

        // ---------- Pełny ekran ----------

        private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

        private void ToggleFullscreen()
        {
            if (_active == null && !_isFullscreen) return;

            if (!_isFullscreen)
            {
                _prevStyle = WindowStyle;
                _prevState = WindowState;
                _prevResize = ResizeMode;

                AppTitleBar.Visibility = Visibility.Collapsed;
                Rail.Visibility = Visibility.Collapsed;
                Sidebar.Visibility = Visibility.Collapsed;
                TabStripHost.Visibility = Visibility.Collapsed;
                SessionToolbar.Visibility = Visibility.Collapsed;

                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                WindowState = WindowState.Maximized;
                _isFullscreen = true;
            }
            else
            {
                WindowStyle = _prevStyle;
                ResizeMode = _prevResize;
                WindowState = _prevState;

                AppTitleBar.Visibility = Visibility.Visible;
                Rail.Visibility = Visibility.Visible;
                Sidebar.Visibility = Visibility.Visible;
                TabStripHost.Visibility = Visibility.Visible;
                SessionToolbar.Visibility = Visibility.Visible;

                FsPopup.IsOpen = false;
                _isFullscreen = false;
            }
        }

        private void HotZone_MouseEnter(object sender, MouseEventArgs e)
        {
            if (_isFullscreen) FsPopup.IsOpen = true;
        }

        private void FsPopup_MouseLeave(object sender, MouseEventArgs e) => FsPopup.IsOpen = false;

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.F11) ToggleFullscreen();
            else if (e.Key == Key.Escape && _isFullscreen) ToggleFullscreen();
        }

        // ---------- Pomocnicze ----------

        private void AddServer_Click(object sender, RoutedEventArgs e)
        {
            SetStatus("Dodawanie serwerów — Faza 2 (SQLite/CRUD).");
        }

        private Brush AvatarBrush(string group)
        {
            if (group == "Produkcja") return (Brush)Resources["AvProd"];
            if (group == "Staging") return (Brush)Resources["AvStaging"];
            return (Brush)Resources["AvClient"];
        }

        private Brush GroupDotBrush(string group)
        {
            if (group == "Produkcja") return (Brush)Resources["GdProd"];
            if (group == "Staging") return (Brush)Resources["GdStaging"];
            return (Brush)Resources["GdClient"];
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

        private void SetSessionStatus(Session s, string text)
        {
            s.Status = text;
            if (s == _active) SetStatus(text);
        }

        private void SetStatus(string text) => StatusText.Text = text;
    }
}
