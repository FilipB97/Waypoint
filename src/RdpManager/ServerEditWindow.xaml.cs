using System;
using System.Windows;
using RdpManager.Core;
using RdpManager.Models;

namespace RdpManager
{
    public partial class ServerEditWindow
    {
        private readonly ServerInfo _server;

        /// <summary>Hasło wpisane w oknie (do zapisania w Credential Manager przez wołającego).</summary>
        public string EnteredPassword { get; private set; } = "";

        public ServerEditWindow(ServerInfo server, string currentPassword)
        {
            InitializeComponent();
            _server = server;

            NameBox.Text = server.Name ?? "";
            HostBox.Text = server.Host ?? "";
            PortBox.Text = server.Port.ToString();
            GroupBox.Text = server.Group ?? "";
            UserBox.Text = server.Username ?? "";
            DomainBox.Text = server.Domain ?? "";
            WinAuthCheck.IsChecked = server.UseWindowsAccount;
            PassBox.Password = currentPassword ?? "";
            SavePassCheck.IsChecked = server.SavePassword;

            EdClipboard.IsChecked = server.RedirectClipboard;
            EdDrives.IsChecked = server.RedirectDrives;
            EdPrinters.IsChecked = server.RedirectPrinters;
            EdAudio.SelectedIndex = Math.Clamp(server.AudioMode, 0, 2);
            EdAuthLevel.SelectedIndex = Math.Clamp(server.AuthenticationLevel, 0, 2);
            GatewayHostBox.Text = server.GatewayHostname ?? "";
            EdGatewayUsage.SelectedIndex = Math.Clamp(server.GatewayUsageMethod, 0, 2);

            ApplyWinAuthState();
            Loaded += (s, e) => ClampToScreen();
        }

        /// <summary>
        /// Ogranicza wysokość okna do obszaru roboczego monitora, na którym stoi (DPI-poprawnie),
        /// żeby stopka z „Zapisz" nigdy nie wypadła poza ekran — treść wtedy się przewija.
        /// </summary>
        private void ClampToScreen()
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                var wa = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
                double waTop = wa.Top / dpi.DpiScaleY;
                MaxHeight = wa.Height / dpi.DpiScaleY - 16;
                if (Top < waTop + 8) Top = waTop + 8;
            }
            catch
            {
                MaxHeight = SystemParameters.WorkArea.Height - 16;
            }
        }

        private void WinAuth_Changed(object sender, RoutedEventArgs e) => ApplyWinAuthState();

        private void ApplyWinAuthState()
        {
            bool win = WinAuthCheck.IsChecked == true;
            UserBox.IsEnabled = !win;
            DomainBox.IsEnabled = !win;
            PassBox.IsEnabled = !win;
            SavePassCheck.IsEnabled = !win;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(NameBox.Text) || string.IsNullOrWhiteSpace(HostBox.Text))
            {
                MessageBox.Show(LocalizationManager.S("S.se.needname"), LocalizationManager.S("S.se.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _server.Name = NameBox.Text.Trim();
            _server.Host = HostBox.Text.Trim();
            _server.Port = int.TryParse(PortBox.Text.Trim(), out var p) ? p : 3389;
            _server.Group = string.IsNullOrWhiteSpace(GroupBox.Text) ? "Serwery" : GroupBox.Text.Trim();
            _server.UseWindowsAccount = WinAuthCheck.IsChecked == true;
            _server.Username = _server.UseWindowsAccount ? "" : UserBox.Text.Trim();
            _server.Domain = _server.UseWindowsAccount ? "" : DomainBox.Text.Trim();
            _server.SavePassword = !_server.UseWindowsAccount && SavePassCheck.IsChecked == true;

            _server.RedirectClipboard = EdClipboard.IsChecked == true;
            _server.RedirectDrives = EdDrives.IsChecked == true;
            _server.RedirectPrinters = EdPrinters.IsChecked == true;
            _server.AudioMode = EdAudio.SelectedIndex < 0 ? 0 : EdAudio.SelectedIndex;
            _server.AuthenticationLevel = EdAuthLevel.SelectedIndex < 0 ? 2 : EdAuthLevel.SelectedIndex;
            _server.GatewayHostname = GatewayHostBox.Text.Trim();
            _server.GatewayUsageMethod = EdGatewayUsage.SelectedIndex < 0 ? 0 : EdGatewayUsage.SelectedIndex;

            if (string.IsNullOrWhiteSpace(_server.Initials))
                _server.Initials = RdpUtils.MakeInitials(_server.Name);

            EnteredPassword = _server.UseWindowsAccount ? "" : PassBox.Password;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
