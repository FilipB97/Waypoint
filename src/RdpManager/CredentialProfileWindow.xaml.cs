using System.Windows;
using RdpManager.Models;

namespace RdpManager
{
    /// <summary>Edytor współdzielonego profilu poświadczeń (nazwa/domena/login/hasło). Hasło zwracane przez
    /// EnteredPassword — wołający zapisuje je w Credential Manager pod profile.CredTarget (nie w JSON).</summary>
    public partial class CredentialProfileWindow
    {
        private readonly CredentialProfile _profile;

        /// <summary>Hasło wpisane w oknie (do zapisania w Credential Manager przez wołającego).</summary>
        public string EnteredPassword { get; private set; } = "";

        public CredentialProfileWindow(CredentialProfile profile, string currentPassword)
        {
            InitializeComponent();
            _profile = profile;
            NameBox.Text = profile.Name ?? "";
            DomainBox.Text = profile.Domain ?? "";
            UserBox.Text = profile.Username ?? "";
            PassBox.Password = currentPassword ?? "";
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(NameBox.Text) || string.IsNullOrWhiteSpace(UserBox.Text))
            {
                MessageBox.Show(LocalizationManager.S("S.prof.required"), LocalizationManager.S("S.prof.edit.title"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _profile.Name = NameBox.Text.Trim();
            _profile.Domain = DomainBox.Text.Trim();
            _profile.Username = UserBox.Text.Trim();
            EnteredPassword = PassBox.Password;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
