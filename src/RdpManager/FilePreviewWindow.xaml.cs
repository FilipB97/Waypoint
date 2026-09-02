using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RdpManager.Core;

namespace RdpManager
{
    /// <summary>
    /// Podgląd pliku z panelu plików — bez zapisywania czegokolwiek na dysku. Treść przychodzi już jako
    /// bajty (panel pobrał je do pamięci i sam pilnował limitu rozmiaru — patrz <see cref="FilePreview"/>),
    /// więc okno tylko rozpoznaje rodzaj i rysuje.
    ///
    /// Cztery ścieżki: tekst/kod, JSON (te same tokeny i kolory co konsola REST), Markdown (ten sam
    /// renderer co notatki wydania) oraz obraz. Piąta, awaryjna, to zrzut szesnastkowy dla danych
    /// binarnych — lepszy niż odmowa, bo najczęściej wystarczy zerknąć na nagłówek pliku.
    ///
    /// Numeracja linii jest częścią akapitu (a nie osobną kolumną), więc wyrównanie do tekstu wynika
    /// z konstrukcji i nie może się rozjechać. Kosztem jest to, że zaznaczenie myszą obejmuje numery —
    /// dlatego przycisk „Kopiuj" kopiuje SUROWĄ treść, bez numerów.
    /// </summary>
    public partial class FilePreviewWindow
    {
        private readonly byte[] _data;
        private readonly string _fileName;
        private string _rawText;   // treść bez numerów linii — do schowka (null dla obrazów)

        private static string L(string key) => LocalizationManager.S(key);

        public FilePreviewWindow(string fileName, byte[] data)
        {
            InitializeComponent();
            _fileName = fileName ?? "";
            _data = data ?? Array.Empty<byte>();

            Title = _fileName;
            Bar.Title = _fileName;
            NameText.Text = _fileName;

            Render();
        }

        /// <summary>Otwiera podgląd jako okno modalne właściciela (nazwa inna niż Show — ta jest już w Window).</summary>
        public static void Open(Window owner, string fileName, byte[] data)
            => new FilePreviewWindow(fileName, data) { Owner = owner }.ShowDialog();

        private void Render()
        {
            var kind = FilePreview.KindFor(_fileName, _data);
            var notes = new List<string> { string.Format(L("S.preview.meta.size"), FormatSize(_data.LongLength)) };

            switch (kind)
            {
                case PreviewKind.Image:
                    if (RenderImage(notes)) break;
                    kind = PreviewKind.Binary;      // nieczytelny obraz → pokaż bajty zamiast pustego okna
                    goto case PreviewKind.Binary;

                case PreviewKind.Binary:
                    _rawText = FilePreview.HexDump(_data);
                    RenderPlain(_rawText);
                    notes.Add(L("S.preview.meta.binary"));
                    break;

                case PreviewKind.Markdown:
                    RenderMarkdown(notes);
                    break;

                case PreviewKind.Json:
                    RenderCodeOrJson(notes, json: true);
                    break;

                default:
                    RenderCodeOrJson(notes, json: false);
                    break;
            }

            MetaText.Text = string.Join("  ·  ", notes);
            CopyBtn.IsEnabled = _rawText != null;
        }

        // ---------- Ścieżki renderowania ----------

        private bool RenderImage(List<string> notes)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;   // wczytaj od razu — strumień zamykamy poniżej
                bmp.StreamSource = new System.IO.MemoryStream(_data, writable: false);
                bmp.EndInit();
                bmp.Freeze();

                ImageView.Source = bmp;
                ImageHost.Visibility = Visibility.Visible;
                notes.Add(string.Format(L("S.preview.meta.image"), bmp.PixelWidth, bmp.PixelHeight));
                CopyBtn.IsEnabled = false;
                return true;
            }
            catch { return false; }
        }

        private void RenderMarkdown(List<string> notes)
        {
            _rawText = FilePreview.DecodeText(_data, out string enc);
            notes.Add(enc);
            DocHost.Document = MarkdownLite.Build(_rawText, OpenLink);
            DocHost.Visibility = Visibility.Visible;
        }

        // Wspólna ścieżka dla zwykłego tekstu i JSON-a: różni je wyłącznie źródło kolorów tokenów.
        private void RenderCodeOrJson(List<string> notes, bool json)
        {
            string text = FilePreview.DecodeText(_data, out string enc);
            notes.Add(enc);
            _rawText = text;

            string shown = FilePreview.LimitLines(text, out bool truncated);
            if (truncated) NoteText.Text = string.Format(L("S.preview.truncated"), FilePreview.MaxTextLines);

            var segments = json
                ? RestJsonColorizer.Tokenize(shown).Select(t => (t.Text, JsonBrush(t.Kind)))
                : new[] { (shown, Res("TextPrim")) }.AsEnumerable();

            FillCode(segments, CountLines(shown));
        }

        private void RenderPlain(string text)
        {
            NoteText.Text = L("S.preview.hex");
            FillCode(new[] { (text, Res("TextPrim")) }, CountLines(text));
        }

        // ---------- Budowa dokumentu z numeracją linii ----------

        // Jeden akapit, w nim naprzemiennie: numer linii (wyszarzony) i pokolorowane fragmenty treści.
        // Tokeny JSON-a bywają wielolinijkowe, więc każdy jest dzielony na '\n' — dzięki temu numer
        // trafia dokładnie na początek każdej linii, niezależnie od tego, gdzie kończą się tokeny.
        private void FillCode(IEnumerable<(string Text, Brush Brush)> segments, int totalLines)
        {
            var doc = new FlowDocument
            {
                FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
                FontSize = (double)TryFindResource("FontBody"),
                PageWidth = 2400   // szeroka strona = brak zawijania + poziomy pasek (jak w konsoli REST)
            };
            var p = new Paragraph { Margin = new Thickness(0), LineHeight = 18 };
            int width = Math.Max(2, totalLines.ToString().Length);
            var gutter = Res("TextTer");

            int line = 1;
            p.Inlines.Add(Gutter(line, width, gutter));
            foreach (var (text, brush) in segments)
            {
                var parts = (text ?? "").Split('\n');
                for (int i = 0; i < parts.Length; i++)
                {
                    if (i > 0)
                    {
                        p.Inlines.Add(new Run("\n"));
                        p.Inlines.Add(Gutter(++line, width, gutter));
                    }
                    string seg = parts[i].TrimEnd('\r');
                    if (seg.Length > 0) p.Inlines.Add(new Run(seg) { Foreground = brush });
                }
            }

            doc.Blocks.Add(p);
            CodeHost.Document = doc;
            CodeHost.Visibility = Visibility.Visible;
        }

        private static Run Gutter(int line, int width, Brush brush)
            => new Run(line.ToString().PadLeft(width) + "  ") { Foreground = brush };

        private static int CountLines(string s)
        {
            if (string.IsNullOrEmpty(s)) return 1;
            int n = 1;
            foreach (char c in s) if (c == '\n') n++;
            return n;
        }

        // ---------- Akcje ----------

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            if (_rawText == null) return;
            try { Clipboard.SetText(_rawText); NoteText.Text = L("S.preview.copied"); }
            catch (Exception ex) { NoteText.Text = ex.Message; }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = _fileName };   // pyta o nadpisanie natywnie
            if (dlg.ShowDialog() != true) return;
            try { System.IO.File.WriteAllBytes(dlg.FileName, _data); NoteText.Text = L("S.sftp.done"); }
            catch (Exception ex) { NoteText.Text = ex.Message; }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        // Odnośniki z Markdownu otwieramy tą samą, sprawdzającą schemat ścieżką co terminal (tylko http/https).
        private static void OpenLink(string url)
        {
            if (!UrlValidation.TryNormalizeWebUrl(url, out Uri uri)) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch { }
        }

        // ---------- Pomocnicze ----------

        private Brush JsonBrush(RestJsonTok k)
        {
            switch (k)
            {
                case RestJsonTok.Key: return PaletteBrush("MethodPost", 0x7B, 0xA6, 0xFF);
                case RestJsonTok.Str: return PaletteBrush("MethodGet", 0x4B, 0xD6, 0xA0);
                case RestJsonTok.Num:
                case RestJsonTok.Keyword: return PaletteBrush("MethodPut", 0xF0, 0xB4, 0x5F);
                case RestJsonTok.Punct: return Res("TextTer");
                default: return Res("TextPrim");
            }
        }

        private Brush PaletteBrush(string key, byte r, byte g, byte b)
            => (TryFindResource(key) as Brush) ?? new SolidColorBrush(Color.FromRgb(r, g, b));

        private Brush Res(string key) => (TryFindResource(key) as Brush) ?? Brushes.Gray;

        private static string FormatSize(long bytes)
        {
            string[] u = { "B", "KB", "MB", "GB" };
            double v = bytes;
            int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return (i == 0 ? v.ToString("0") : v.ToString("0.#")) + " " + u[i];
        }
    }
}
