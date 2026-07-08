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
        private readonly List<CredentialProfile> _profiles;
        private bool _initializing;

        /// <summary>Hasło wpisane w oknie (do zapisania w Credential Manager przez wołającego).</summary>
        public string EnteredPassword { get; private set; } = "";

        public ServerEditWindow(ServerInfo server, string currentPassword, List<CredentialProfile> profiles)
        {
            InitializeComponent();
            _server = server;
            _profiles = profiles ?? new List<CredentialProfile>();

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
            RemoteAppBox.Text = server.RemoteAppProgram ?? "";
            RemoteAppArgsBox.Text = server.RemoteAppArgs ?? "";
            MacBox.Text = server.MacAddress ?? "";
            TagsBox.Text = server.Tags == null ? "" : string.Join(", ", server.Tags);
            NotesBox.Text = server.Notes ?? "";
            EdAudio.SelectedIndex = Math.Clamp(server.AudioMode, 0, 2);
            EdAuthLevel.SelectedIndex = Math.Clamp(server.AuthenticationLevel, 0, 2);
            GatewayHostBox.Text = server.GatewayHostname ?? "";
            EdGatewayUsage.SelectedIndex = Math.Clamp(server.GatewayUsageMethod, 0, 2);

            _initializing = true;
            ProfileCombo.Items.Add(new ComboBoxItem { Content = LocalizationManager.S("S.prof.none"), Tag = "" });
            foreach (var pr in _profiles)
                ProfileCombo.Items.Add(new ComboBoxItem { Content = pr.Name, Tag = pr.Id });
            SelectProfile(server.CredentialProfileId);
            ProtocolCombo.SelectedIndex =
                server.Protocol == RemoteProtocol.Ssh ? 1 :
                server.Protocol == RemoteProtocol.Telnet ? 2 :
                server.Protocol == RemoteProtocol.Serial ? 3 :
                server.Protocol == RemoteProtocol.Http ? 4 :
                server.Protocol == RemoteProtocol.Vnc ? 5 :
                server.Protocol == RemoteProtocol.Sftp ? 6 :
                server.Protocol == RemoteProtocol.Ftp ? 7 :
                server.Protocol == RemoteProtocol.Rest ? 8 : 0;
            KeyPathBox.Text = server.PrivateKeyPath ?? "";
            TunnelsBox.Text = server.Tunnels == null ? "" : string.Join(Environment.NewLine, server.Tunnels);
            FtpTlsCombo.SelectedIndex = Math.Clamp(server.FtpEncryption, 0, 3);
            FtpAnonCheck.IsChecked = server.FtpAnonymous;
            BuildColorSwatches();
            ApplyWinAuthState();
            ApplyProtocolState();
            ApplyProfileState();
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
        private static readonly string[] DefaultPorts = { "", "3389", "22", "23", "115200", "443", "5900", "21" };

        private void Protocol_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_initializing) return;
            string port = PortBox.Text.Trim();
            if (Array.IndexOf(DefaultPorts, port) >= 0)
                PortBox.Text = DefaultPortFor(ProtocolCombo.SelectedIndex).ToString();
            ApplyProtocolState();
        }

        private void FtpAnon_Click(object sender, RoutedEventArgs e) { if (!_initializing) ApplyProtocolState(); }

        private static int DefaultPortFor(int protocolIndex)
            => protocolIndex == 1 ? 22 : protocolIndex == 2 ? 23 : protocolIndex == 3 ? 115200
             : protocolIndex == 4 ? 443 : protocolIndex == 5 ? 5900 : protocolIndex == 6 ? 22 : protocolIndex == 7 ? 21 : 3389;

        // Widoczność pól zależnie od protokołu: RDP = wszystko; SSH = poświadczenia + klucz + tunele;
        // Telnet/Serial = bez poświadczeń (logowanie w terminalu); WWW = tylko URL (bez portu).
        // Serial: Host=COM, Port=baud. Http: Host=URL.
        private void ApplyProtocolState()
        {
            int idx = ProtocolCombo.SelectedIndex;
            bool rdp = idx == 0, ssh = idx == 1, serial = idx == 3, http = idx == 4, vnc = idx == 5, sftp = idx == 6, ftp = idx == 7, rest = idx == 8;
            bool urlMode = http || rest;   // Host = URL, bez portu i bez poświadczeń w edytorze (REST auth jest w konsoli)
            bool ftpAnon = ftp && FtpAnonCheck.IsChecked == true;   // anonimowy FTP: bez loginu/hasła
            bool user = (rdp || ssh || sftp || ftp) && !ftpAnon;  // login (VNC hasłem; FTP anonimowy = brak)
            bool pass = (rdp || ssh || vnc || sftp || ftp) && !ftpAnon;   // pole hasła

            var rdpVis = rdp ? Visibility.Visible : Visibility.Collapsed;
            TabRdp.Visibility = rdpVis;                 // zakładka „Opcje RDP" tylko dla RDP
            WinAuthCheck.Visibility = rdpVis;
            DomainLabel.Visibility = rdpVis;
            DomainBox.Visibility = rdpVis;

            KeyPathPanel.Visibility = (ssh || sftp) ? Visibility.Visible : Visibility.Collapsed;   // klucz: SSH i SFTP
            TunnelsPanel.Visibility = ssh ? Visibility.Visible : Visibility.Collapsed;             // tunele ssh -L: tylko powłoka SSH

            TabAuth.Visibility = (pass || ftp) ? Visibility.Visible : Visibility.Collapsed;   // Telnet/Serial/WWW inaczej; FTP: zakładka też dla anonimowego (opcje FTP)
            // Aktywna zakładka mogła właśnie zniknąć (byłeś na „Opcje RDP"/„Uwierzytelnianie" i zmieniłeś protokół) → wróć na pierwszą.
            if (EditorTabs.SelectedItem is System.Windows.Controls.TabItem sel && sel.Visibility != Visibility.Visible)
                EditorTabs.SelectedIndex = 0;
            FtpOptionsPanel.Visibility = ftp ? Visibility.Visible : Visibility.Collapsed;
            ProfileRow.Visibility = user ? Visibility.Visible : Visibility.Collapsed; // profil (login/domena) tylko dla RDP/SSH
            var userVis = user ? Visibility.Visible : Visibility.Collapsed;
            UserLabel.Visibility = userVis;
            UserBox.Visibility = userVis;
            var passVis = pass ? Visibility.Visible : Visibility.Collapsed;
            PassLabel.Visibility = passVis;
            PassPanel.Visibility = passVis;

            var portVis = urlMode ? Visibility.Collapsed : Visibility.Visible;   // URL niesie port w sobie
            PortLabel.Visibility = portVis;
            PortBox.Visibility = portVis;

            HostLabel.Text = serial ? LocalizationManager.S("S.se.comport")
                           : urlMode ? LocalizationManager.S("S.se.url") : "Host";
            PortLabel.Text = serial ? LocalizationManager.S("S.se.baud") : "Port";
            HostBox.PlaceholderText = serial ? "COM3" : urlMode ? "https://…" : "";
        }

        // ---------- Profil poświadczeń ----------
        private string SelectedProfileId()
            => (ProfileCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

        private void SelectProfile(string id)
        {
            id = id ?? "";
            foreach (var obj in ProfileCombo.Items)
                if (obj is ComboBoxItem it && (it.Tag as string ?? "") == id) { ProfileCombo.SelectedItem = it; return; }
            if (ProfileCombo.Items.Count > 0) ProfileCombo.SelectedIndex = 0;   // profil zniknął → „(brak)"
        }

        private void Profile_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing) return;
            ApplyProfileState();
        }

        // Gdy wybrany profil dostarcza poświadczeń, własne pola login/domena/hasło są nieaktywne (bierze je profil).
        private void ApplyProfileState()
        {
            var pid = SelectedProfileId();
            if (pid.Length > 0)
            {
                // Podgląd: pokaż login/domenę z profilu w (wyszarzonych) polach, żeby było widać, czym się połączy.
                CredentialProfile prof = null;
                foreach (var x in _profiles) if (x.Id == pid) { prof = x; break; }
                if (prof != null) { UserBox.Text = prof.Username ?? ""; DomainBox.Text = prof.Domain ?? ""; }
                WinAuthCheck.IsEnabled = false;
                UserBox.IsEnabled = false;
                DomainBox.IsEnabled = false;
                PassBox.IsEnabled = false;
                PassPlain.IsEnabled = false;
                RevealBtn.IsEnabled = false;
                SavePassCheck.IsEnabled = false;
            }
            else
            {
                // Wróć do własnych danych serwera (mogły zostać nadpisane podglądem profilu).
                UserBox.Text = _server.Username ?? "";
                DomainBox.Text = _server.Domain ?? "";
                WinAuthCheck.IsEnabled = true;
                ApplyWinAuthState();   // przywróć stan pól wg „Konto Windows"
            }
        }

        private void BrowseKey_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = LocalizationManager.S("S.se.keypath") };
            if (dlg.ShowDialog(this) == true) KeyPathBox.Text = dlg.FileName;
        }

        // Tagi: rozdzielone przecinkami, przycięte (opcjonalny wiodący #), bez pustych i duplikatów (ignorując wielkość liter).
        private static List<string> ParseTags(string text)
        {
            var list = new List<string>();
            foreach (var raw in (text ?? "").Split(','))
            {
                var t = raw.Trim().TrimStart('#').Trim();
                if (t.Length > 0 && !list.Exists(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase)))
                    list.Add(t);
            }
            return list;
        }

        // Pola oznaczone czerwoną obwódką po nieudanej walidacji — czyszczone na początku każdej kolejnej próby.
        private readonly List<Control> _invalidFields = new List<Control>();

        private void MarkInvalid(Control field)
        {
            field.BorderBrush = (Brush)FindResource("Danger");
            field.BorderThickness = new Thickness(1.5);
            _invalidFields.Add(field);
        }

        private void ClearInvalidMarks()
        {
            foreach (var f in _invalidFields) { f.ClearValue(Control.BorderBrushProperty); f.ClearValue(Control.BorderThicknessProperty); }
            _invalidFields.Clear();
        }

        // Zaznacza pole (czerwona obwódka) i przenosi tam fokus — najpierw przełączając zakładkę, jeśli
        // pole leży poza aktualnie widoczną (bez tego Focus() nic by nie dał na niewidocznej karcie).
        private void FocusInvalid(Control field, TabItem tab)
        {
            EditorTabs.SelectedItem = tab;
            MarkInvalid(field);
            field.Focus();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            ClearInvalidMarks();

            bool restProto = ProtocolCombo.SelectedIndex == 8;   // REST: kolekcja — URL bazowy opcjonalny
            bool needName = string.IsNullOrWhiteSpace(NameBox.Text);
            bool needHost = !restProto && string.IsNullOrWhiteSpace(HostBox.Text);
            if (needName || needHost)
            {
                if (needName && needHost) MarkInvalid(HostBox);   // drugie puste pole — dodatkowo, bez przenoszenia tam fokusu
                FocusInvalid(needName ? NameBox : HostBox, TabConn);
                MessageBox.Show(LocalizationManager.S("S.se.needname"), LocalizationManager.S("S.se.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int idx = ProtocolCombo.SelectedIndex;
            var protocol = idx == 1 ? RemoteProtocol.Ssh
                         : idx == 2 ? RemoteProtocol.Telnet
                         : idx == 3 ? RemoteProtocol.Serial
                         : idx == 4 ? RemoteProtocol.Http
                         : idx == 5 ? RemoteProtocol.Vnc
                         : idx == 6 ? RemoteProtocol.Sftp
                         : idx == 7 ? RemoteProtocol.Ftp
                         : idx == 8 ? RemoteProtocol.Rest
                         : RemoteProtocol.Rdp;
            bool ssh = protocol == RemoteProtocol.Ssh;
            bool rdp = protocol == RemoteProtocol.Rdp;
            bool vnc = protocol == RemoteProtocol.Vnc;
            bool sftp = protocol == RemoteProtocol.Sftp;
            bool ftp = protocol == RemoteProtocol.Ftp;
            bool anon = ftp && FtpAnonCheck.IsChecked == true;
            bool creds = rdp || ssh || sftp || ftp; // login — Telnet/Serial/WWW/VNC nie mają loginu w modelu
            bool passProto = creds || vnc;   // hasło (VNC uwierzytelnia samym hasłem)

            // Tunele i MAC: waliduj PRZED zapisem czegokolwiek (błąd = nic się nie zmienia).
            var tunnels = TunnelSpec.ParseAll(TunnelsBox.Text, out string badTunnel);
            if (ssh && badTunnel != null)
            {
                FocusInvalid(TunnelsBox, TabAuth);
                MessageBox.Show(string.Format(LocalizationManager.S("S.se.tunnels.bad"), badTunnel),
                    LocalizationManager.S("S.se.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            string macText = MacBox.Text.Trim();
            if (macText.Length > 0 && !WakeOnLan.TryParseMac(macText, out _))
            {
                FocusInvalid(MacBox, TabConn);
                MessageBox.Show(string.Format(LocalizationManager.S("S.se.mac.bad"), macText),
                    LocalizationManager.S("S.se.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // Port sieciowy 1–65535 (RDP/SSH/Telnet/VNC). Serial = baud (dowolna liczba), WWW = brak portu — pomijamy.
            bool netPort = rdp || ssh || vnc || sftp || ftp || protocol == RemoteProtocol.Telnet;
            if (netPort && (!int.TryParse(PortBox.Text.Trim(), out var portVal) || portVal < 1 || portVal > 65535))
            {
                FocusInvalid(PortBox, TabConn);
                MessageBox.Show(LocalizationManager.S("S.se.port.bad"),
                    LocalizationManager.S("S.se.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // Klucz SSH: informacja (nie blokada) gdy podany plik nie istnieje — ścieżka bywa środowiskowa/sieciowa.
            if ((ssh || sftp) && KeyPathBox.Text.Trim().Length > 0 && !System.IO.File.Exists(KeyPathBox.Text.Trim()))
                MessageBox.Show(string.Format(LocalizationManager.S("S.se.keypath.missing"), KeyPathBox.Text.Trim()),
                    LocalizationManager.S("S.se.title"), MessageBoxButton.OK, MessageBoxImage.Information);

            _server.Protocol = protocol;
            _server.PrivateKeyPath = (ssh || sftp) ? KeyPathBox.Text.Trim() : "";
            _server.Tunnels = ssh ? tunnels : new List<string>();
            _server.FtpEncryption = ftp ? FtpTlsCombo.SelectedIndex : 0;
            _server.FtpAnonymous = anon;

            _server.Name = NameBox.Text.Trim();
            _server.Host = HostBox.Text.Trim();
            _server.Port = int.TryParse(PortBox.Text.Trim(), out var p) ? p : DefaultPortFor(idx);
            // Puste pole grupy = domyślny „kosz" lokalizowany przy wyświetlaniu (nie zapisujemy nazwy PL).
            // REST bez grupy → domyślnie „HTTP", żeby kolekcje ładnie zbierały się w drzewie serwerów.
            _server.Group = string.IsNullOrWhiteSpace(GroupBox.Text)
                ? (protocol == RemoteProtocol.Rest ? "HTTP" : "")
                : GroupBox.Text.Trim();
            // Profil poświadczeń (tylko RDP/SSH): gdy wybrany, login/domena/hasło biorą się z profilu → własne czyścimy.
            string profileId = SelectedProfileId();
            _server.CredentialProfileId = (creds && profileId.Length > 0) ? profileId : "";
            bool useProfile = _server.CredentialProfileId.Length > 0;

            _server.UseWindowsAccount = !useProfile && rdp && WinAuthCheck.IsChecked == true;
            _server.Username = (useProfile || !creds || _server.UseWindowsAccount) ? "" : UserBox.Text.Trim();
            _server.Domain = (!useProfile && rdp && !_server.UseWindowsAccount) ? DomainBox.Text.Trim() : "";
            _server.SavePassword = !useProfile && passProto && !_server.UseWindowsAccount && !anon && SavePassCheck.IsChecked == true;

            _server.RedirectClipboard = EdClipboard.IsChecked == true;
            _server.RedirectDrives = EdDrives.IsChecked == true;
            _server.RedirectPrinters = EdPrinters.IsChecked == true;
            _server.AdminSession = rdp && EdAdmin.IsChecked == true;
            _server.RemoteAppProgram = rdp ? RemoteAppBox.Text.Trim() : "";
            _server.RemoteAppArgs = rdp ? RemoteAppArgsBox.Text.Trim() : "";
            _server.MacAddress = macText;
            _server.Tags = ParseTags(TagsBox.Text);
            _server.Notes = NotesBox.Text.Trim();
            _server.AudioMode = EdAudio.SelectedIndex < 0 ? 0 : EdAudio.SelectedIndex;
            _server.AuthenticationLevel = EdAuthLevel.SelectedIndex < 0 ? 2 : EdAuthLevel.SelectedIndex;
            _server.GatewayHostname = GatewayHostBox.Text.Trim();
            _server.GatewayUsageMethod = EdGatewayUsage.SelectedIndex < 0 ? 0 : EdGatewayUsage.SelectedIndex;

            _server.AvatarColor = _avatarColor ?? "";
            _server.Initials = RdpUtils.MakeInitials(_server.Name);   // zawsze z nazwy (leczy stare inicjały z IP)

            EnteredPassword = (!useProfile && passProto && !_server.UseWindowsAccount && !anon) ? CurrentPassword() : "";
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
