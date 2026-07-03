using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
            EdAdmin.IsChecked = server.AdminSession;
            MacBox.Text = server.MacAddress ?? "";
            EdAudio.SelectedIndex = Math.Clamp(server.AudioMode, 0, 2);
            EdAuthLevel.SelectedIndex = Math.Clamp(server.AuthenticationLevel, 0, 2);
            GatewayHostBox.Text = server.GatewayHostname ?? "";
            EdGatewayUsage.SelectedIndex = Math.Clamp(server.GatewayUsageMethod, 0, 2);

            _initializing = true;
            ProtocolCombo.SelectedIndex =
                server.Protocol == RemoteProtocol.Ssh ? 1 :
                server.Protocol == RemoteProtocol.Telnet ? 2 :
                server.Protocol == RemoteProtocol.Serial ? 3 :
                server.Protocol == RemoteProtocol.Http ? 4 : 0;
            KeyPathBox.Text = server.PrivateKeyPath ?? "";
            TunnelsBox.Text = server.Tunnels == null ? "" : string.Join(Environment.NewLine, server.Tunnels);
            BuildColorSwatches();
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
            PassPlain.IsEnabled = !win;
            RevealBtn.IsEnabled = !win;
            SavePassCheck.IsEnabled = !win;
        }

        // Podgląd hasła: przełącza PasswordBox <-> zwykły TextBox (WPF PasswordBox nie ma trybu „pokaż").
        private void RevealPass_Click(object sender, RoutedEventArgs e)
        {
            if (PassPlain.Visibility != Visibility.Visible)
            {
                PassPlain.Text = PassBox.Password;
                PassBox.Visibility = Visibility.Collapsed;
                PassPlain.Visibility = Visibility.Visible;
                RevealIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.EyeOff24;
                PassPlain.Focus();
            }
            else
            {
                PassBox.Password = PassPlain.Text;
                PassPlain.Visibility = Visibility.Collapsed;
                PassBox.Visibility = Visibility.Visible;
                RevealIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Eye24;
                PassBox.Focus();
            }
        }

        // Hasło z aktywnego pola (gdy odsłonięte, PasswordBox nie jest zsynchronizowany).
        private string CurrentPassword()
            => PassPlain.Visibility == Visibility.Visible ? PassPlain.Text : PassBox.Password;

        // Wybór koloru awatara: presety + „Auto" (pusty = kolor wg grupy). Zaznaczenie = obrys akcentem.
        private static readonly string[] SwatchColors =
            { "", "#3B82F6", "#22C1C3", "#10B981", "#F59E0B", "#EF4444", "#8B5CF6", "#EC4899", "#64748B" };
        private string _avatarColor = "";
        private readonly Dictionary<string, Border> _swatchByColor = new Dictionary<string, Border>();

        private void BuildColorSwatches()
        {
            _avatarColor = _server.AvatarColor ?? "";
            foreach (var hex in SwatchColors)
            {
                var sw = new Border
                {
                    Width = 26, Height = 26, CornerRadius = new CornerRadius(6),
                    Margin = new Thickness(0, 0, 8, 6), Cursor = Cursors.Hand, BorderThickness = new Thickness(2),
                    ToolTip = hex.Length == 0 ? LocalizationManager.S("S.se.iconcolor.auto") : hex
                };
                if (hex.Length == 0)
                {
                    sw.Background = (Brush)TryFindResource("Elevated") ?? Brushes.Gray;
                    sw.Child = new TextBlock
                    {
                        Text = "A", Foreground = (Brush)TryFindResource("TextSec") ?? Brushes.White,
                        FontWeight = FontWeights.Bold, FontSize = 12,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                    };
                }
                else
                {
                    try { sw.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
                    catch { continue; }
                }
                string captured = hex;
                sw.MouseLeftButtonUp += (s, e) => { _avatarColor = captured; HighlightSwatches(); };
                _swatchByColor[hex] = sw;
                ColorSwatches.Children.Add(sw);
            }
            HighlightSwatches();
        }

        private void HighlightSwatches()
        {
            var accent = (Brush)TryFindResource("Accent") ?? Brushes.DodgerBlue;
            foreach (var kv in _swatchByColor)
                kv.Value.BorderBrush = kv.Key == _avatarColor ? accent : Brushes.Transparent;
        }

        // Porty domyślne wszystkich protokołów — port podmieniamy tylko, gdy bieżąca wartość
        // jest jednym z nich (czyli użytkownik nie wpisał własnego). Musi zawierać KAŻDY default,
        // inaczej jedno przejście przez dany protokół zrywa automat na zawsze.
        private static readonly string[] DefaultPorts = { "", "3389", "22", "23", "115200", "443" };

        private void Protocol_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_initializing) return;
            string port = PortBox.Text.Trim();
            if (Array.IndexOf(DefaultPorts, port) >= 0)
                PortBox.Text = DefaultPortFor(ProtocolCombo.SelectedIndex).ToString();
            ApplyProtocolState();
        }

        private static int DefaultPortFor(int protocolIndex)
            => protocolIndex == 1 ? 22 : protocolIndex == 2 ? 23 : protocolIndex == 3 ? 115200
             : protocolIndex == 4 ? 443 : 3389;

        // Widoczność pól zależnie od protokołu: RDP = wszystko; SSH = poświadczenia + klucz + tunele;
        // Telnet/Serial = bez poświadczeń (logowanie w terminalu); WWW = tylko URL (bez portu).
        // Serial: Host=COM, Port=baud. Http: Host=URL.
        private void ApplyProtocolState()
        {
            int idx = ProtocolCombo.SelectedIndex;
            bool rdp = idx == 0, ssh = idx == 1, serial = idx == 3, http = idx == 4;
            bool creds = rdp || ssh;

            var rdpVis = rdp ? Visibility.Visible : Visibility.Collapsed;
            RdpCard.Visibility = rdpVis;               // cała karta „Opcje RDP" (bez pustego nagłówka dla nie-RDP)
            WinAuthCheck.Visibility = rdpVis;
            DomainLabel.Visibility = rdpVis;
            DomainBox.Visibility = rdpVis;
            RedirHeader.Visibility = rdpVis;
            RdpOptionsPanel.Visibility = rdpVis;

            KeyPathPanel.Visibility = ssh ? Visibility.Visible : Visibility.Collapsed;

            var credsVis = creds ? Visibility.Visible : Visibility.Collapsed;
            AuthCard.Visibility = credsVis;            // cała karta „Uwierzytelnianie" (Telnet/Serial/WWW logują się inaczej)
            UserLabel.Visibility = credsVis;
            UserBox.Visibility = credsVis;
            PassLabel.Visibility = credsVis;
            PassPanel.Visibility = credsVis;

            var portVis = http ? Visibility.Collapsed : Visibility.Visible;   // URL niesie port w sobie
            PortLabel.Visibility = portVis;
            PortBox.Visibility = portVis;

            HostLabel.Text = serial ? LocalizationManager.S("S.se.comport")
                           : http ? LocalizationManager.S("S.se.url") : "Host";
            PortLabel.Text = serial ? LocalizationManager.S("S.se.baud") : "Port";
            HostBox.PlaceholderText = serial ? "COM3" : http ? "https://…" : "";
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

            int idx = ProtocolCombo.SelectedIndex;
            var protocol = idx == 1 ? RemoteProtocol.Ssh
                         : idx == 2 ? RemoteProtocol.Telnet
                         : idx == 3 ? RemoteProtocol.Serial
                         : idx == 4 ? RemoteProtocol.Http
                         : RemoteProtocol.Rdp;
            bool ssh = protocol == RemoteProtocol.Ssh;
            bool rdp = protocol == RemoteProtocol.Rdp;
            bool creds = rdp || ssh;   // Telnet/Serial logują się w terminalu — bez poświadczeń w modelu

            // Tunele i MAC: waliduj PRZED zapisem czegokolwiek (błąd = nic się nie zmienia).
            var tunnels = TunnelSpec.ParseAll(TunnelsBox.Text, out string badTunnel);
            if (ssh && badTunnel != null)
            {
                MessageBox.Show(string.Format(LocalizationManager.S("S.se.tunnels.bad"), badTunnel),
                    LocalizationManager.S("S.se.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            string macText = MacBox.Text.Trim();
            if (macText.Length > 0 && !WakeOnLan.TryParseMac(macText, out _))
            {
                MessageBox.Show(string.Format(LocalizationManager.S("S.se.mac.bad"), macText),
                    LocalizationManager.S("S.se.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _server.Protocol = protocol;
            _server.PrivateKeyPath = ssh ? KeyPathBox.Text.Trim() : "";
            _server.Tunnels = ssh ? tunnels : new List<string>();

            _server.Name = NameBox.Text.Trim();
            _server.Host = HostBox.Text.Trim();
            _server.Port = int.TryParse(PortBox.Text.Trim(), out var p) ? p : DefaultPortFor(idx);
            _server.Group = string.IsNullOrWhiteSpace(GroupBox.Text) ? "Serwery" : GroupBox.Text.Trim();
            _server.UseWindowsAccount = rdp && WinAuthCheck.IsChecked == true;
            _server.Username = (!creds || _server.UseWindowsAccount) ? "" : UserBox.Text.Trim();
            _server.Domain = (rdp && !_server.UseWindowsAccount) ? DomainBox.Text.Trim() : "";
            _server.SavePassword = creds && !_server.UseWindowsAccount && SavePassCheck.IsChecked == true;

            _server.RedirectClipboard = EdClipboard.IsChecked == true;
            _server.RedirectDrives = EdDrives.IsChecked == true;
            _server.RedirectPrinters = EdPrinters.IsChecked == true;
            _server.AdminSession = rdp && EdAdmin.IsChecked == true;
            _server.MacAddress = macText;
            _server.AudioMode = EdAudio.SelectedIndex < 0 ? 0 : EdAudio.SelectedIndex;
            _server.AuthenticationLevel = EdAuthLevel.SelectedIndex < 0 ? 2 : EdAuthLevel.SelectedIndex;
            _server.GatewayHostname = GatewayHostBox.Text.Trim();
            _server.GatewayUsageMethod = EdGatewayUsage.SelectedIndex < 0 ? 0 : EdGatewayUsage.SelectedIndex;

            _server.AvatarColor = _avatarColor ?? "";
            _server.Initials = RdpUtils.MakeInitials(_server.Name);   // zawsze z nazwy (leczy stare inicjały z IP)

            EnteredPassword = (creds && !_server.UseWindowsAccount) ? CurrentPassword() : "";
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
