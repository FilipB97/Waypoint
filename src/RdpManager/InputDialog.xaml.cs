using System.Windows;

namespace RdpManager
{
    /// <summary>Proste modalne okienko na jedną linię tekstu (np. zmiana nazwy grupy) lub hasło.</summary>
    public partial class InputDialog
    {
        private readonly bool _masked;

        /// <summary>Wpisana wartość. Tryb tekstowy: przycięta z białych znaków; maskowany: bez zmian.</summary>
        public string Value => _masked ? MaskedBox.Password : (InputBox.Text ?? "").Trim();

        public InputDialog(string title, string label, string initial, bool masked = false)
        {
            InitializeComponent();
            _masked = masked;
            Title = title;
            WinTitleBar.Title = title;
            PromptLabel.Text = label;
            if (masked)
            {
                InputBox.Visibility = Visibility.Collapsed;
                MaskedBox.Visibility = Visibility.Visible;
                Loaded += (s, e) => MaskedBox.Focus();
            }
            else
            {
                InputBox.Text = initial ?? "";
                Loaded += (s, e) => { InputBox.Focus(); InputBox.SelectAll(); };
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
