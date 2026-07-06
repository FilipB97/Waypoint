using System;
using System.Diagnostics;
using System.Windows;
using RdpManager.Core;

namespace RdpManager
{
    /// <summary>Notatki wydania (changelog). Tryb confirm = przed aktualizacją (Anuluj/Aktualizuj → DialogResult);
    /// tryb informacyjny = „co nowego" po aktualizacji (Zamknij).</summary>
    public partial class ReleaseNotesWindow
    {
        private readonly string _htmlUrl;

        public ReleaseNotesWindow(string header, Version version, string notes, string htmlUrl, bool confirm)
        {
            InitializeComponent();
            _htmlUrl = htmlUrl;
            Bar.Title = header;
            HeaderText.Text = header;
            NotesBox.Text = string.IsNullOrWhiteSpace(notes) ? LocalizationManager.S("S.update.nonotes") : notes.Trim();
            GithubLink.IsEnabled = !string.IsNullOrEmpty(htmlUrl);

            if (confirm)
            {
                WarnText.Visibility = Visibility.Visible;
                CancelBtn.Visibility = Visibility.Visible;
                OkBtn.Visibility = Visibility.Visible;
            }
            else
            {
                CloseBtn.Visibility = Visibility.Visible;
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void Github_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_htmlUrl)) return;
            try { Process.Start(new ProcessStartInfo(_htmlUrl) { UseShellExecute = true }); }
            catch { /* brak przeglądarki — ignoruj */ }
        }
    }
}
