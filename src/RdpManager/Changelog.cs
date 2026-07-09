using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace RdpManager
{
    /// <summary>
    /// Rodzaj wpisu w historii zmian — steruje kolorem etykiety (Compass §4.11).
    /// </summary>
    public enum ChangeKind { New, Change, Fix }

    public sealed class ChangeItem
    {
        public ChangeKind Kind;
        public string Text;
        public ChangeItem(ChangeKind kind, string text) { Kind = kind; Text = text; }
    }

    public sealed class ChangelogEntry
    {
        public string Version;
        public string Date;      // ISO „yyyy-MM-dd" — formatowana przy renderze
        public bool Latest;      // najnowsza → badge „NOWA"
        public List<ChangeItem> Items;
    }

    /// <summary>
    /// Historia zmian pokazywana w Ustawienia → O aplikacji (Compass §4.11). Docelowo pobierana z wydań
    /// GitHub (notatki wydania → <see cref="ParseNotes"/>); poniższa lista to FALLBACK offline / gdy API
    /// nie odpowie. Aktualizuj ją przy większych wydaniach (opcjonalnie — realne dane biorą się z GitHuba).
    /// </summary>
    public static class Changelog
    {
        public static readonly IReadOnlyList<ChangelogEntry> Entries = new List<ChangelogEntry>
        {
            new ChangelogEntry
            {
                Version = "1.8.0", Date = "2026-07-09", Latest = true,
                Items = new List<ChangeItem>
                {
                    new ChangeItem(ChangeKind.New, "Redesign „Compass”: nowa paleta, presety motywu i konfigurowalny akcent"),
                    new ChangeItem(ChangeKind.Change, "Ekran Ustawień wg mockupu (segmenty, przełączniki, sekcje)"),
                    new ChangeItem(ChangeKind.New, "„O aplikacji” w Ustawieniach z historią zmian"),
                    new ChangeItem(ChangeKind.New, "Lista serwerów świadoma protokołów + opóźnienia (ms)"),
                    new ChangeItem(ChangeKind.New, "Menedżer plików: wielozaznaczenie i sortowanie kolumn"),
                },
            },
            new ChangelogEntry
            {
                Version = "1.7.0", Date = "2026-07-08",
                Items = new List<ChangeItem>
                {
                    new ChangeItem(ChangeKind.New, "Klient REST: kolekcje, globalne środowiska, skrypty"),
                    new ChangeItem(ChangeKind.New, "SFTP/FTP: dwupanelowy menedżer plików z transferem i postępem"),
                },
            },
            new ChangelogEntry
            {
                Version = "1.6.0", Date = "2026-07-07",
                Items = new List<ChangeItem>
                {
                    new ChangeItem(ChangeKind.New, "Import z mRemoteNG, RDCMan i FileZilla"),
                    new ChangeItem(ChangeKind.Fix, "Bezpieczeństwo: TOFU dla FTPS, weryfikacja podpisu aktualizacji"),
                },
            },
        };

        // Rozpoznawane nagłówki sekcji (Keep a Changelog / pl) → rodzaj wpisu.
        private static readonly (string kw, ChangeKind kind)[] SectionKw =
        {
            ("popraw", ChangeKind.Fix), ("fix", ChangeKind.Fix), ("bug", ChangeKind.Fix), ("napraw", ChangeKind.Fix),
            ("zmian", ChangeKind.Change), ("chang", ChangeKind.Change), ("aktualiz", ChangeKind.Change),
            ("now", ChangeKind.New), ("dodan", ChangeKind.New), ("add", ChangeKind.New), ("new", ChangeKind.New), ("feat", ChangeKind.New),
        };

        /// <summary>
        /// Zamienia notatki wydania (markdown „body" z GitHuba) na wpisy z etykietą rodzaju. Bierze punkty
        /// list („- ", „* "), a rodzaj ustala z nagłówka sekcji (### Nowe / Poprawki …) albo ze słów kluczowych
        /// w treści; domyślnie „NOWE". Czyści prefiksy (feat:/fix:/emoji) i proste znaczniki markdown.
        /// Publiczne dla testów.
        /// </summary>
        public static List<ChangeItem> ParseNotes(string body, int max = 8)
        {
            var items = new List<ChangeItem>();
            if (string.IsNullOrWhiteSpace(body)) return items;
            ChangeKind? section = null;
            foreach (var raw in body.Replace("\r", "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;

                bool header = line.StartsWith("#") || (line.StartsWith("**") && line.EndsWith("**") && !line.Contains("- "));
                if (header) { section = DetectKind(line, null); continue; }

                string text = line;
                bool bullet = false;
                foreach (var bp in new[] { "- ", "* ", "• ", "– ", "+ " })
                    if (text.StartsWith(bp)) { text = text.Substring(bp.Length).Trim(); bullet = true; break; }
                if (!bullet) continue;   // pomijamy zwykłe akapity — bierzemy tylko punkty

                var kind = DetectKind(text, section) ?? ChangeKind.New;
                text = Clean(text);
                if (text.Length > 0) items.Add(new ChangeItem(kind, text));
                if (items.Count >= max) break;
            }
            return items;
        }

        private static ChangeKind? DetectKind(string text, ChangeKind? fallback)
        {
            var t = text.ToLowerInvariant();
            foreach (var (kw, kind) in SectionKw) if (t.Contains(kw)) return kind;
            return fallback;
        }

        // Prefiks konwencji commitów: „typ", opcjonalny „(zakres)", opcjonalny „!", dwukropek. Tylko małe
        // litery, żeby nie uciąć zwykłej prozy („Uwaga:", „TODO:"). Łapie feat:, fix(rest):, chore(ci)!: itd.
        private static readonly Regex CommitPrefix = new Regex(@"^[a-z][a-z0-9]*(\([^)]*\))?!?:\s+", RegexOptions.Compiled);

        // Usuwa prefiksy konwencji commitów / emoji oraz proste znaczniki markdown; pierwszą literę na wielką
        // (tematy commitów są zwykle małą literą → czyta się jak wpis changeloga, nie jak commit).
        private static string Clean(string s)
        {
            s = s.Trim();
            // wiodące emoji / symbole
            while (s.Length > 0 && !char.IsLetterOrDigit(s[0]) && s[0] != '„' && s[0] != '"') s = s.Substring(1).TrimStart();
            s = CommitPrefix.Replace(s, "");
            s = s.Replace("**", "").Replace("`", "").Trim();
            if (s.Length > 0 && char.IsLower(s[0])) s = char.ToUpper(s[0]) + s.Substring(1);
            return s;
        }
    }
}
