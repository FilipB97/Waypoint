using System;
using System.Windows;
using RdpManager.Core;
using RdpManager.Models;

namespace RdpManager
{
    public partial class ServerEditWindow
    {
        private readonly ServerInfo _server;
        private bool _initializing;

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

            _initializing = true;
            ProtocolCombo.SelectedIndex = server.Protocol == RemoteProtocol.Ssh ? 1 : 0;
            KeyPathBox.Text = server.PrivateKeyPath ?? "";
            ApplyWinAuthState();
            ApplyProtocolState();
            _initializing = false;

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

        private void Protocol_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_initializing) return;
            // Podmień domyślny port przy zmianie protokołu, o ile użytkownik nie ustawił własnego.
            string port = PortBox.Text.Trim();
            if (ProtocolCombo.SelectedIndex == 1 && (port == "3389" || port == "")) PortBox.Text = "22";
            else if (ProtocolCombo.SelectedIndex == 0 && port == "22") PortBox.Text = "3389";
            ApplyProtocolState();
        }

        // SSH: chowa pola RDP-only (konto Windows, domena, przekierowania, brama) i pokazuje pole klucza.
        private void ApplyProtocolState()
        {
            bool ssh = ProtocolCombo.SelectedIndex == 1;
            var rdpOnly = ssh ? Visibility.Collapsed : Visibility.Visible;
            WinAuthCheck.Visibility = rdpOnly;
            DomainLabel.Visibility = rdpOnly;
            DomainBox.Visibility = rdpOnly;
            RedirHeader.Visibility = rdpOnly;
            RdpOptionsPanel.Visibility = rdpOnly;
            KeyPathPanel.Visibility = ssh ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BrowseKey_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = LocalizationManager.S("S.se.keypath") };
            if (dlg.ShowDialog(this) == true) KeyPathBox.Text = dlg.FileName;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(NameBox.Text) || string.IsNullOrWhiteSpace(HostBox.Text))
            {
                MessageBox.Show(LocalizationManager.S("S.se.needname"), LocalizationManager.S("S.se.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool ssh = ProtocolCombo.SelectedIndex == 1;
            _server.Protocol = ssh ? RemoteProtocol.Ssh : RemoteProtocol.Rdp;
            _server.PrivateKeyPath = ssh ? KeyPathBox.Text.Trim() : "";

            _server.Name = NameBox.Text.Trim();
            _server.Host = HostBox.Text.Trim();
            _server.Port = int.TryParse(PortBox.Text.Trim(), out var p) ? p : (ssh ? 22 : 3389);
            _server.Group = string.IsNullOrWhiteSpace(GroupBox.Text) ? "Serwery" : GroupBox.Text.Trim();
            _server.UseWindowsAccount = !ssh && WinAuthCheck.IsChecked == true;
            _server.Username = _server.UseWindowsAccount ? "" : UserBox.Text.Trim();
            _server.Domain = (ssh || _server.UseWindowsAccount) ? "" : DomainBox.Text.Trim();
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
