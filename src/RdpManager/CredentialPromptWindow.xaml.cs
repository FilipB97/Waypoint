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
                + "Podaj poświadczenia dla " + (server.Name ?? server.Host) + " (" + server.Host + ").";

            UserBox.Text = server.Username ?? "";
            DomainBox.Text = server.Domain ?? "";
            PassBox.Password = currentPassword ?? "";
            SavePassCheck.IsChecked = server.SavePassword;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(UserBox.Text))
            {
                MessageBox.Show("Podaj nazwę użytkownika.", "Poświadczenia", MessageBoxButton.OK, MessageBoxImage.Warning);
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
