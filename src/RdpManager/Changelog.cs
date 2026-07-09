using System.Collections.Generic;

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
    /// Kurowana ręcznie historia zmian pokazywana w Ustawienia → O aplikacji (Compass §4.11).
    /// AKTUALIZUJ przy każdym wydaniu (dopisz nowy wpis na górze, zdejmij Latest z poprzedniego).
    /// Wersje/daty odpowiadają wydaniom na GitHubie; opisy są skrótowe.
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
    }
}
