using System.Windows;
using RdpManager.Models;

namespace RdpManager
{
    /// <summary>
    /// Prosty prompt poświadczeń przy połączeniu (jak w mstsc): login, domena, hasło,
    /// opcjonalny zapis. Nie zapisuje niczego sam — wołający decyduje.
    /// </summary>
    public partial class CredentialPromptWindow
    {
        public string EnteredUser { get; private set; } = "";
        public string EnteredDomain { get; private set; } = "";
        public string EnteredPassword { get; private set; } = "";
        public bool SavePassword { get; private set; }

        public CredentialPromptWindow(ServerInfo server, string currentPassword, string reason = null)
        {
            InitializeComponent();

            PromptHeader.Text = (string.IsNullOrEmpty(reason) ? "" : reason + "\n")
                + string.Format(LocalizationManager.S("S.cp.header"), server.Name ?? server.Host, server.Host);

            UserBox.Text = server.Username ?? "";
            DomainBox.Text = server.Domain ?? "";
            PassBox.Password = currentPassword ?? "";
            SavePassCheck.IsChecked = server.SavePassword;

            Loaded += (s, e) => ClampToScreen();
        }

        /// <summary>Jak w ServerEditWindow: stopka zawsze na ekranie, treść się przewija.</summary>
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

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(UserBox.Text))
            {
                MessageBox.Show(LocalizationManager.S("S.cp.needuser"), LocalizationManager.S("S.cp.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            EnteredUser = UserBox.Text.Trim();
            EnteredDomain = DomainBox.Text.Trim();
            EnteredPassword = PassBox.Password;
            SavePassword = SavePassCheck.IsChecked == true;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
