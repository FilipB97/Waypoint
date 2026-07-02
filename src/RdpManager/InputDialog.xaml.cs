using System.Windows;

namespace RdpManager
{
    /// <summary>Proste modalne okienko na jedną linię tekstu (np. zmiana nazwy grupy).</summary>
    public partial class InputDialog
    {
        /// <summary>Wpisana wartość (przycięta z białych znaków).</summary>
        public string Value => (InputBox.Text ?? "").Trim();

        public InputDialog(string title, string label, string initial)
        {
            InitializeComponent();
            Title = title;
            WinTitleBar.Title = title;
            PromptLabel.Text = label;
            InputBox.Text = initial ?? "";
            Loaded += (s, e) => { InputBox.Focus(); InputBox.SelectAll(); };
        }

        private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
