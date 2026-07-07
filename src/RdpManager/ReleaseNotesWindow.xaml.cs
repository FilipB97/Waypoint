using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
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

            string notesText = string.IsNullOrWhiteSpace(notes) ? LocalizationManager.S("S.update.nonotes") : notes.Trim();
            var doc = MarkdownLite.Build(notesText, OpenLink);
            doc.FontFamily = FontFamily;                 // czcionka okna (Segoe UI Variable), nie domyślna FlowDocument
            doc.FontSize = 13;
            doc.PagePadding = new Thickness(14, 12, 14, 12);
            doc.TextAlignment = TextAlignment.Left;      // bez „justowania" (poprzednia wersja rozciągała wiersze)
            doc.Foreground = TryFindResource("TextSec") as Brush ?? Brushes.Gray;
            NotesView.Document = doc;

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

        private void Github_Click(object sender, RoutedEventArgs e) => OpenLink(_htmlUrl);

        // Otwiera adres w domyślnej przeglądarce (przycisk „Zobacz na GitHub" oraz linki [tekst](url) z notatek).
        private void OpenLink(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* brak przeglądarki — ignoruj */ }
        }
    }
}
