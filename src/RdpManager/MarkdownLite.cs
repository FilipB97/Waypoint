using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace RdpManager
{
    /// <summary>
    /// Minimalny renderer Markdown (podzbiór z notatek wydania GitHuba) → FlowDocument:
    /// nagłówki #/##/###, listy „- ", **pogrubienie**, _kursywa_/*kursywa*, `kod`, [link](url), --- linia.
    /// Świadomie prosty — nie pełny Markdown; ma tylko ładnie sformatować changelog.
    /// </summary>
    public static class MarkdownLite
    {
        // Kolejność ma znaczenie: link, potem **bold** przed *italic*, kod przed _italic_.
        private static readonly Regex Inline = new Regex(
            @"\[(?<lt>[^\]]+)\]\((?<lu>[^)]+)\)|\*\*(?<b>[^*]+)\*\*|`(?<c>[^`]+)`|_(?<i>[^_]+)_|\*(?<i2>[^*]+)\*",
            RegexOptions.Compiled);

        /// <summary>Buduje dokument z Markdowna. <paramref name="onLink"/> otwiera adres z [tekst](url).</summary>
        public static FlowDocument Build(string md, Action<string> onLink)
        {
            var doc = new FlowDocument();
            if (string.IsNullOrWhiteSpace(md)) return doc;

            foreach (var raw in md.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
            {
                string t = raw.Trim();
                if (t.Length == 0) continue;   // odstępy dają marginesy akapitów

                if (t == "---" || t == "***" || t == "___")
                {
                    doc.Blocks.Add(new Paragraph
                    {
                        Margin = new Thickness(0, 8, 0, 8),
                        BorderBrush = Res("Border"),
                        BorderThickness = new Thickness(0, 0, 0, 1)
                    });
                    continue;
                }

                if (t.StartsWith("#"))
                {
                    int level = 0;
                    while (level < t.Length && t[level] == '#') level++;
                    var p = new Paragraph { Margin = new Thickness(0, level <= 1 ? 2 : 10, 0, 4) };
                    AddInlines(p, t.Substring(level).Trim(), onLink);
                    foreach (var inl in p.Inlines) if (inl is Run r) { r.FontWeight = FontWeights.SemiBold; r.FontSize = level <= 1 ? 16 : 14; }
                    doc.Blocks.Add(p);
                    continue;
                }

                if (t.StartsWith("- ") || t.StartsWith("* "))
                {
                    var p = new Paragraph { Margin = new Thickness(0, 1, 0, 1), TextIndent = -12, Padding = new Thickness(14, 0, 0, 0) };
                    p.Inlines.Add(new Run("•  "));
                    AddInlines(p, t.Substring(2), onLink);
                    doc.Blocks.Add(p);
                    continue;
                }

                var para = new Paragraph { Margin = new Thickness(0, 2, 0, 2) };
                AddInlines(para, t, onLink);
                doc.Blocks.Add(para);
            }
            return doc;
        }

        private static void AddInlines(Paragraph p, string text, Action<string> onLink)
        {
            int pos = 0;
            foreach (Match m in Inline.Matches(text))
            {
                if (m.Index > pos) p.Inlines.Add(new Run(text.Substring(pos, m.Index - pos)));
                if (m.Groups["lt"].Success)
                {
                    var link = new Hyperlink(new Run(m.Groups["lt"].Value)) { Foreground = Res("Accent") };
                    string url = m.Groups["lu"].Value;
                    if (onLink != null) link.Click += (s, e) => onLink(url);
                    p.Inlines.Add(link);
                }
                else if (m.Groups["b"].Success) p.Inlines.Add(new Run(m.Groups["b"].Value) { FontWeight = FontWeights.SemiBold });
                else if (m.Groups["c"].Success) p.Inlines.Add(new Run(m.Groups["c"].Value) { FontFamily = Mono(), Foreground = Res("TextPrim") });
                else if (m.Groups["i"].Success) p.Inlines.Add(new Run(m.Groups["i"].Value) { FontStyle = FontStyles.Italic });
                else if (m.Groups["i2"].Success) p.Inlines.Add(new Run(m.Groups["i2"].Value) { FontStyle = FontStyles.Italic });
                pos = m.Index + m.Length;
            }
            if (pos < text.Length) p.Inlines.Add(new Run(text.Substring(pos)));
        }

        private static Brush Res(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
        private static FontFamily Mono() => Application.Current?.TryFindResource("Mono") as FontFamily ?? new FontFamily("Consolas");
    }
}
