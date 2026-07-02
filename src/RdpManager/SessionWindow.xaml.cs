using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Forms.Integration;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AxMSTSCLib;
using MSTSCLib;
using RdpManager.Core;
using RdpManager.Models;

namespace RdpManager
{
    /// <summary>
    /// Samodzielne okno pojedynczej sesji RDP. Kontrolka ActiveX jest tworzona TU i żyje tu przez
    /// całe życie okna (bez reparentingu — dlatego renderuje poprawnie, także po przeniesieniu okna
    /// na inny monitor i wejściu w pełny ekran). Model jak w mstsc: kilka okien = kilka sesji.
    /// </summary>
    public partial class SessionWindow
    {
        private readonly ServerInfo _server;
        private readonly AppSettings _settings;
        private readonly Action _persist;
        private readonly Action<ServerInfo, string> _dockBack;   // „zadokuj" z powrotem jako kartę w managerze
        private readonly AxMsRdpClient11NotSafeForScripting _rdp;
        private readonly WindowsFormsHost _host;
        private readonly Session _session;
        private readonly RdpDynamicResolution _resizer;
        private string _password;
        private bool _loggedIn;

        /// <summary>Czy sesja jest aktualnie połączona (do potwierdzenia zamknięcia aplikacji).</summary>
        public bool IsConnected => _session != null && _session.Connected;

        // Pełny ekran (bezramkowy na bieżącym monitorze) + auto-chowany pasek.
        private bool _fs;
        private bool _fsPinned;   // pasek „przypięty" — nie chowa się automatycznie
        private WindowStyle _pStyle; private ResizeMode _pResize; private bool _pTopmost;
        private double _pL, _pT, _pW, _pH, _fsBarOffset;
        private RECT _fsMon;
        private readonly DispatcherTimer _fsPoll, _fsDelay;

        public SessionWindow(ServerInfo server, AppSettings settings, string password, Action persist,
                             Action<ServerInfo, string> dockBack = null)
        {
            InitializeComponent();
            _server = server;
            _settings = settings;
            _password = password ?? "";
            _persist = persist;
            _dockBack = dockBack;
            Title = server.Name + " — " + server.Host;
            WinTitleBar.Title = server.Name + " · " + server.Host;
            FsName.Text = server.Name + " · " + server.Host;

            _rdp = new AxMsRdpClient11NotSafeForScripting();
            _host = new WindowsFormsHost();
            ((ISupportInitialize)_rdp).BeginInit();
            _rdp.Dock = System.Windows.Forms.DockStyle.Fill;
            _host.Child = _rdp;
            ((ISupportInitialize)_rdp).EndInit();
            HostContainer.Children.Insert(0, _host);       // pod nakładką
            _host.UpdateLayout();
            try { ((System.Windows.Forms.Control)_rdp).CreateControl(); } catch { }

            _session = new Session(server, _rdp, _host) { Password = _password };
            _resizer = new RdpDynamicResolution(_session, _host);
            WireEvents();

            _fsDelay = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(Math.Clamp(settings.FullscreenBarDelayMs, 0, 3000)) };
            _fsDelay.Tick += (s, e) => { _fsDelay.Stop(); if (_fs) FsPopup.IsOpen = true; };
            _fsPoll = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromMilliseconds(90) };
            _fsPoll.Tick += FsPollTick;
            FsPopup.CustomPopupPlacementCallback = PlaceFsBar;

            SetStatus(L("S.st.ready"), StatusKind.Info, false);
            ShowOverlay();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e) => BeginConnect();

        // ---------- Łączenie ----------

        private void BeginConnect()
        {
            bool canAuto = _server.UseWindowsAccount || !string.IsNullOrEmpty(_password);
            if (canAuto) Connect();
            else PromptAndConnect(null);
        }

        private void Connect_Click(object sender, RoutedEventArgs e) => BeginConnect();

        private void PromptAndConnect(string reason)
        {
            var dlg = new CredentialPromptWindow(_server, _password, reason) { Owner = this };
            if (dlg.ShowDialog() != true) return;

            _server.UseWindowsAccount = false;
            _server.Username = dlg.EnteredUser;
            _server.Domain = dlg.EnteredDomain;
            _password = dlg.EnteredPassword;
            _session.Password = _password;
            if (dlg.SavePassword)
            {
                _server.SavePassword = true;
                CredentialStore.Save(_server.CredTarget, _server.Username, _password);
            }
            else
            {
                _server.SavePassword = false;
                CredentialStore.Delete(_server.CredTarget);
            }
            _persist?.Invoke();
            Connect();
        }

        private void Connect()
        {
            try { _rdp.Disconnect(); } catch { }
            _loggedIn = false;
            try
            {
                IMsRdpClientAdvancedSettings8 adv = _rdp.AdvancedSettings9;
                adv.RDPPort = _server.Port;
                adv.AuthenticationLevel = (uint)Math.Clamp(_server.AuthenticationLevel, 0, 2);
                adv.EnableCredSspSupport = true;
                adv.ConnectToAdministerServer = _server.AdminSession;   // sesja konsolowa (mstsc /admin)
                adv.SmartSizing = false;
                adv.EnableAutoReconnect = _settings.AutoReconnect;
                _rdp.ColorDepth = _settings.ColorDepth;
                adv.RedirectClipboard = _server.RedirectClipboard;
                adv.RedirectDrives = _server.RedirectDrives;
                adv.RedirectPrinters = _server.RedirectPrinters;
                adv.AudioRedirectionMode = (uint)Math.Clamp(_server.AudioMode, 0, 2);
                try { _rdp.SecuredSettings2.KeyboardHookMode = 2; } catch { }
                try { ((IMsRdpClientNonScriptable5)_rdp.GetOcx()).UseMultimon = false; } catch { }
                ApplyGateway();

                _rdp.Server = _server.Host;
                if (_server.UseWindowsAccount)
                {
                    _rdp.UserName = ""; _rdp.Domain = ""; adv.ClearTextPassword = "";
                }
                else
                {
                    _rdp.UserName = _server.Username; _rdp.Domain = _server.Domain; adv.ClearTextPassword = _password;
                }
                _rdp.Connect();
                SetStatus(string.Format(L("S.st.connecting"), _server.Host), StatusKind.Connecting, false);
            }
            catch (Exception ex)
            {
                SetStatus(string.Format(L("S.st.exception"), ex.Message), StatusKind.Error, true);
            }
        }

        private void ApplyGateway()
        {
            try
            {
                var ts = _rdp.TransportSettings;
                if (string.IsNullOrWhiteSpace(_server.GatewayHostname)) { ts.GatewayUsageMethod = 0; return; }
                ts.GatewayHostname = _server.GatewayHostname;
                ts.GatewayUsageMethod = (uint)(_server.GatewayUsageMethod == 0 ? 1 : _server.GatewayUsageMethod);
                ts.GatewayProfileUsageMethod = 1;
                ts.GatewayCredsSource = 0;
            }
            catch { }
        }

        private void Disconnect_Click(object sender, RoutedEventArgs e)
        {
            try { _rdp.Disconnect(); } catch { }
        }

        // „Zadokuj" — wstaw sesję z powrotem jako kartę w managerze (reconnect wznawia sesję serwera).
        private void Dock_Click(object sender, RoutedEventArgs e)
        {
            if (_dockBack == null) return;
            if (_fs) ExitFs();
            _dockBack(_server, _password);
            Close();   // Window_Closing rozłączy tę kontrolkę
        }

        private void WireEvents()
        {
            _rdp.OnConnecting += (o, a) => SetStatus(L("S.st.connectingShort"), StatusKind.Connecting, false);
            _rdp.OnConnected += (o, a) =>
            {
                _session.Connected = true;
                SetStatus(L("S.st.connected"), StatusKind.Ok, false);
                HideOverlay();
                ConnectionLog.Append("CONNECTED", _server);
            };
            _rdp.OnLoginComplete += (o, a) => { _loggedIn = true; _resizer?.ApplyInitial(); try { _rdp.Focus(); } catch { } };
            _rdp.OnDisconnected += (o, a) =>
            {
                bool was = _loggedIn;
                _session.Connected = false; _loggedIn = false;
                string ext = ""; try { ext = ((uint)_rdp.ExtendedDisconnectReason).ToString(); } catch { }
                string desc = ""; try { desc = _rdp.GetErrorDescription((uint)a.discReason, string.IsNullOrEmpty(ext) ? 0u : (uint)_rdp.ExtendedDisconnectReason); } catch { }
                string msg = string.Format(L("S.st.disconnected"), RdpUtils.FormatDisconnectReason(desc, a.discReason, string.IsNullOrEmpty(ext) ? 0 : long.Parse(ext)));
                if (!was)
                    msg += "  " + (_server.UseWindowsAccount
                        ? L("S.st.hint.winauth")
                        : L("S.st.hint.creds"));
                SetStatus(msg, StatusKind.Error, true);
            };
            _rdp.OnFatalError += (o, a) =>
            {
                _session.Connected = false;
                SetStatus(string.Format(L("S.st.fatal"), a.errorCode), StatusKind.Error, true);
            };
        }

        // ---------- Nakładka / status ----------

        private static string L(string key) => LocalizationManager.S(key);

        private void SetStatus(string text, StatusKind kind, bool reconnect)
        {
            StatusText.Text = text;
            StatusText.ToolTip = text;
            var b = KindBrush(kind);
            StatusText.Foreground = b;
            StatusDot.Fill = b;

            // Przyciski paska zależne od stanu: łączenie → [Rozłącz]; połączono → [Pełny ekran][Rozłącz]; rozłączono → [Połącz].
            bool connected = _session.Connected;
            bool connecting = kind == StatusKind.Connecting;
            TbConnect.Visibility = (!connected && !connecting) ? Visibility.Visible : Visibility.Collapsed;
            TbFullscreen.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
            TbDisconnect.Visibility = (connected || connecting) ? Visibility.Visible : Visibility.Collapsed;

            if (_session.Connected) HideOverlay();
            else UpdateOverlay(text, kind, reconnect);
        }

        private void ShowOverlay() => UpdateOverlay(OverlayTitle.Text, StatusKind.Info, true);

        private void UpdateOverlay(string msg, StatusKind kind, bool reconnect)
        {
            if (_session.Connected) { Overlay.Visibility = Visibility.Collapsed; return; }
            Overlay.Visibility = Visibility.Visible;
            bool connecting = kind == StatusKind.Connecting;
            Spinner.Visibility = connecting ? Visibility.Visible : Visibility.Collapsed;
            OverlayReconnect.Visibility = (connecting || !reconnect) ? Visibility.Collapsed : Visibility.Visible;
            OverlayTitle.Text = connecting ? string.Format(L("S.st.connecting"), _server.Host)
                : (kind == StatusKind.Error ? L("S.st.disconnectedShort") : L("S.st.ready"));
            OverlayMsg.Text = connecting ? "" : msg;
        }

        private void HideOverlay() => Overlay.Visibility = Visibility.Collapsed;

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

        // ---------- Pełny ekran (bezramkowy na bieżącym monitorze) ----------

        private void Fullscreen_Click(object sender, RoutedEventArgs e)
        {
            if (_fs) ExitFs(); else EnterFs();
        }

        // mstsc-owo: maksymalizacja okna = pełny ekran. Wyjście z pełnego ekranu wraca do okna (Normal).
        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Maximized && !_fs) EnterFs();
        }

        private void EnterFs()
        {
            _pStyle = WindowStyle; _pResize = ResizeMode; _pTopmost = Topmost;
            // Przy wejściu z maksymalizacji Left/Width dają śmieci — bierzemy granice okna z RestoreBounds.
            if (WindowState == WindowState.Maximized)
            { var rb = RestoreBounds; _pL = rb.Left; _pT = rb.Top; _pW = rb.Width; _pH = rb.Height; }
            else { _pL = Left; _pT = Top; _pW = Width; _pH = Height; }
            _fs = true;   // wcześnie: StateChanged w trakcie przełączania (WindowState=Normal) nie odpali EnterFs ponownie

            WinTitleBar.Visibility = Visibility.Collapsed;
            Toolbar.Visibility = Visibility.Collapsed;
            HotZoneRow.Height = new GridLength(0);   // host wypełnia CAŁY monitor → rozdzielczość = piksele monitora 1:1
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;

            // Pełny ekran na monitorze, na którym aktualnie stoi okno (na inny ekran przenosisz przeciągając okno).
            var hwnd = new WindowInteropHelper(this).Handle;
            IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var monInfo = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (GetMonitorInfo(mon, ref monInfo))
            {
                _fsMon = monInfo.rcMonitor;
                int pw = _fsMon.right - _fsMon.left, ph = _fsMon.bottom - _fsMon.top;
                SetWindowPos(hwnd, IntPtr.Zero, _fsMon.left, _fsMon.top, pw, ph, SWP_SHOWWINDOW);
                Topmost = true;
                _fs = true;
                _fsPoll.Start();
                // Rozdzielczość dokładnie = natywne piksele monitora (deterministycznie, bez wyścigu DPI).
                Dispatcher.BeginInvoke(new Action(() => { if (_fs) _resizer?.ApplyExact(pw, ph); }), DispatcherPriority.Background);
            }
            else
            {
                WindowState = WindowState.Maximized;
                _fs = true;
                _fsPoll.Start();
            }
        }

        private void ExitFs()
        {
            _fsPoll.Stop(); _fsDelay.Stop();
            Topmost = _pTopmost; WindowStyle = _pStyle; ResizeMode = _pResize;
            // Zawsze wracamy do OKNA (Normal), nie do Maximized — inaczej StateChanged znów odpaliłby fullscreen.
            Left = _pL; Top = _pT; Width = _pW; Height = _pH; WindowState = WindowState.Normal;
            WinTitleBar.Visibility = Visibility.Visible;
            Toolbar.Visibility = Visibility.Visible;
            HotZoneRow.Height = new GridLength(6);
            FsPopup.IsOpen = false;
            _fs = false;
            _fsPinned = false;
            FsPinBtn.Content = L("S.fs.pin");
        }

        private void FsPollTick(object sender, EventArgs e)
        {
            if (!_fs) { _fsPoll.Stop(); return; }
            if (WindowState == WindowState.Minimized) return;
            if (_fsPinned) { if (!FsPopup.IsOpen) FsPopup.IsOpen = true; return; }   // przypięty: zawsze widoczny
            if (!GetCursorPos(out POINT p)) return;
            bool atTop = p.X >= _fsMon.left && p.X < _fsMon.right && p.Y <= _fsMon.top + 2;
            if (atTop) { if (!FsPopup.IsOpen && !_fsDelay.IsEnabled) _fsDelay.Start(); }
            else { if (_fsDelay.IsEnabled) _fsDelay.Stop(); if (FsPopup.IsOpen && p.Y > _fsMon.top + 140) FsPopup.IsOpen = false; }
        }

        private void HotZone_MouseEnter(object sender, MouseEventArgs e) { if (_fs && !FsPopup.IsOpen) { _fsDelay.Stop(); _fsDelay.Start(); } }
        private void FsPopup_MouseLeave(object sender, MouseEventArgs e) { if (!_fsPinned) FsPopup.IsOpen = false; }

        private void FsPin_Click(object sender, RoutedEventArgs e)
        {
            _fsPinned = !_fsPinned;
            FsPinBtn.Content = _fsPinned ? L("S.fs.pinned") : L("S.fs.pin");
            if (_fsPinned) FsPopup.IsOpen = true;
        }

        private void FsMinimize_Click(object sender, RoutedEventArgs e)
        {
            FsPopup.IsOpen = false;
            WindowState = WindowState.Minimized;   // po przywróceniu wraca do pełnego ekranu (granice zachowane)
        }

        private CustomPopupPlacement[] PlaceFsBar(Size popupSize, Size targetSize, Point offset)
        {
            double free = Math.Max(0, targetSize.Width - popupSize.Width);
            double x = free / 2.0 + _fsBarOffset;
            if (x < 0) x = 0; if (x > free) x = free;
            return new[] { new CustomPopupPlacement(new Point(x, 0), PopupPrimaryAxis.None) };
        }

        private void FsBarThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            _fsBarOffset += e.HorizontalChange;
            if (FsPopup.IsOpen) { FsPopup.HorizontalOffset += 0.01; FsPopup.HorizontalOffset -= 0.01; }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F11) { if (_fs) ExitFs(); else EnterFs(); }
            else if (e.Key == Key.Escape && _fs) ExitFs();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            try { _resizer?.Dispose(); } catch { }
            try { _rdp.Disconnect(); } catch { }
            try { _host.Dispose(); } catch { }
        }

        // ---------- P/Invoke ----------
        private const int MONITOR_DEFAULTTONEAREST = 0x2;
        private const uint SWP_SHOWWINDOW = 0x0040;

        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }
    }
}
