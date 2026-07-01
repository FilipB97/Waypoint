using System;
using System.Linq;
using System.Windows;
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

            ApplyWinAuthState();
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
                MessageBox.Show("Podaj nazwę i host.", "Serwer", MessageBoxButton.OK, MessageBoxImage.Warning);
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

            if (string.IsNullOrWhiteSpace(_server.Initials))
                _server.Initials = MakeInitials(_server.Name);

            EnteredPassword = _server.UseWindowsAccount ? "" : PassBox.Password;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private static string MakeInitials(string name)
        {
            var parts = name.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
            string s = parts.Length >= 2
                ? "" + parts[0][0] + parts[1][0]
                : new string(name.Where(char.IsLetterOrDigit).Take(2).ToArray());
            return s.ToUpperInvariant();
        }
    }
}
