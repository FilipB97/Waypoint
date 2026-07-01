using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using AxMSTSCLib;
using MSTSCLib;

namespace RdpSpike
{
    public partial class MainWindow : Window
    {
        // v12 jest zadeklarowana w typelib, ale mstscax.dll na tym systemie jej nie serwuje
        // (CLASS_E_CLASSNOTAVAILABLE) — v11 to najwyższa realnie działająca wersja.
        private AxMsRdpClient11NotSafeForScripting _rdp;

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
            _rdp = new AxMsRdpClient11NotSafeForScripting();
            ((ISupportInitialize)_rdp).BeginInit();
            _rdp.Dock = DockStyle.Fill;
            RdpHost.Child = _rdp;
            ((ISupportInitialize)_rdp).EndInit();

            _rdp.OnConnecting += (s, a) => SetStatus("Łączenie…");
            _rdp.OnConnected += (s, a) => SetStatus("● Połączono z " + _rdp.Server);
            _rdp.OnLoginComplete += (s, a) => SetStatus("● Zalogowano");
            _rdp.OnDisconnected += (s, a) => SetStatus("Rozłączono (reason " + a.discReason + ")");
            _rdp.OnFatalError += (s, a) => SetStatus("Błąd krytyczny: errorCode " + a.errorCode);
        }

        private void Connect_Click(object sender, RoutedEventArgs e)
        {
            try { _rdp.Disconnect(); } catch { /* not connected — ignore */ }

            try
            {
                _rdp.Server = HostBox.Text.Trim();
                _rdp.UserName = UserBox.Text.Trim();

                IMsRdpClientAdvancedSettings8 adv = _rdp.AdvancedSettings9;
                adv.RDPPort = 3389;
                adv.ClearTextPassword = PassBox.Password;
                adv.AuthenticationLevel = 0;      // 0 = nie przerywaj przy braku weryfikacji certyfikatu serwera
                adv.EnableCredSspSupport = true;  // NLA
                adv.SmartSizing = true;           // skaluj pulpit do rozmiaru kontrolki

                FsName.Text = _rdp.Server;
                _rdp.Connect();
                SetStatus("Łączenie z " + _rdp.Server + "…");
            }
            catch (Exception ex)
            {
                SetStatus("Wyjątek przy łączeniu: " + ex.Message);
            }
        }

        private void Disconnect_Click(object sender, RoutedEventArgs e)
        {
            try { _rdp.Disconnect(); } catch (Exception ex) { SetStatus("Rozłączanie: " + ex.Message); }
        }

        private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

        private void ToggleFullscreen()
        {
            if (!_isFullscreen)
            {
                _prevStyle = WindowStyle;
                _prevState = WindowState;
                _prevResize = ResizeMode;

                Toolbar.Visibility = Visibility.Collapsed;
                StatusBar.Visibility = Visibility.Collapsed;
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
                Toolbar.Visibility = Visibility.Visible;
                StatusBar.Visibility = Visibility.Visible;
                FsPopup.IsOpen = false;
                _isFullscreen = false;
            }
        }

        private void HotZone_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_isFullscreen) FsPopup.IsOpen = true;
        }

        private void FsPopup_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            FsPopup.IsOpen = false;
        }

        private void ShowBar_Click(object sender, RoutedEventArgs e)
        {
            FsPopup.IsOpen = !FsPopup.IsOpen;
        }

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // Uwaga: gdy fokus ma kontrolka RDP, przechwytuje ona klawiaturę i te skróty mogą nie dotrzeć do WPF.
            if (e.Key == Key.F11) ToggleFullscreen();
            else if (e.Key == Key.Escape && _isFullscreen) ToggleFullscreen();
        }

        private void SetStatus(string text)
        {
            StatusText.Text = text;
        }
    }
}
