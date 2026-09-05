using System.Windows;

namespace RdpManager.Core
{
    /// <summary>Gęstość wiersza listy serwerów — ile treści niesie wiersz i ile go jest.</summary>
    public enum ListDensity
    {
        /// <summary>Awatar z inicjałami + nazwa + etykieta protokołu.</summary>
        Default,
        /// <summary>Kafelek z ikoną protokołu (w kolorze serwera) + nazwa, bez etykiety.</summary>
        Minimal,
        /// <summary>Sam pasek koloru protokołu + nazwa. Najwięcej pozycji na ekranie.</summary>
        Dense
    }

    /// <summary>Jak listę dzielą grupy.</summary>
    public enum GroupLayout
    {
        /// <summary>Wiersze grupy wcięte względem nagłówka (stan dotychczasowy).</summary>
        Indent,
        /// <summary>Pionowy pas w kolorze grupy ciągnący się przez wszystkie jej wiersze.</summary>
        Rail,
        /// <summary>Bez wcięcia, wiersze na pełną szerokość; nagłówek przykleja się do góry przy przewijaniu.</summary>
        Flat
    }

    /// <summary>
    /// Wymiary wiersza i sekcji listy serwerów. Wyliczone TUTAJ, bo ServerTreeController buduje
    /// kontrolki WPF i nie uruchomi się poza aplikacją — a to są właśnie te liczby, które po cichu
    /// się rozjeżdżają (np. suma wcięcia grupy rozbita między margines wiersza a padding nagłówka).
    ///
    /// Gęstość i układ grup są NIEZALEŻNE: pierwsze mówi, co jest w wierszu, drugie — jak wiersze
    /// dzielą się na sekcje. Wszystkie dziewięć kombinacji jest poprawnych.
    /// </summary>
    public sealed class ListMetrics
    {
        // ---- wiersz ----
        /// <summary>Awatar z inicjałami (gęstość domyślna).</summary>
        public bool Avatar { get; private set; }
        /// <summary>Kafelek 22 px z ikoną protokołu (gęstość minimalna).</summary>
        public bool ProtocolTile { get; private set; }
        /// <summary>Pasek 3 px w kolorze protokołu przy lewej krawędzi (gęstość gęsta).</summary>
        public bool ProtocolBar { get; private set; }
        /// <summary>Tekstowa etykieta protokołu po prawej (tylko gęstość domyślna — inaczej niesie go ikona/pasek).</summary>
        public bool ProtocolTag { get; private set; }
        public Thickness RowPadding { get; private set; }
        public double RowMinHeight { get; private set; }
        /// <summary>Odstęp pionowy między wierszami (połowa z góry, połowa z dołu).</summary>
        public double RowGap { get; private set; }

        // ---- sekcje ----
        /// <summary>Wcięcie wiersza względem lewej krawędzi listy.</summary>
        public double RowIndent { get; private set; }
        public Thickness HeaderPadding { get; private set; }
        /// <summary>Pionowy pas w kolorze grupy przy lewej krawędzi sekcji.</summary>
        public bool Rail { get; private set; }
        /// <summary>Szerokość tego pasa.</summary>
        public double RailWidth { get; private set; }
        /// <summary>Nagłówek przykleja się do góry obszaru przewijania.</summary>
        public bool StickyHeader { get; private set; }
        /// <summary>Włosowa kreska nad nagłówkiem grupy. W układzie płaskim zastępuje ją tło nagłówka.</summary>
        public bool HeaderRule { get; private set; }
        /// <summary>Wiersz na pełną szerokość listy (bez zaokrągleń po bokach).</summary>
        public bool FullBleedRow { get; private set; }

        public static ListDensity ParseDensity(string value)
        {
            if (string.Equals(value, "Minimal", System.StringComparison.OrdinalIgnoreCase)) return ListDensity.Minimal;
            if (string.Equals(value, "Dense", System.StringComparison.OrdinalIgnoreCase)) return ListDensity.Dense;
            return ListDensity.Default;
        }

        public static GroupLayout ParseLayout(string value)
        {
            if (string.Equals(value, "Rail", System.StringComparison.OrdinalIgnoreCase)) return GroupLayout.Rail;
            if (string.Equals(value, "Flat", System.StringComparison.OrdinalIgnoreCase)) return GroupLayout.Flat;
            return GroupLayout.Indent;
        }

        public static ListMetrics For(ListDensity density, GroupLayout layout)
        {
            var m = Density(density);
            Layout(m, layout, density);
            return m;
        }

        // Wartości gęstości DOMYŚLNEJ i MINIMALNEJ przeniesione 1:1 z BuildServerRowDefault
        // / BuildServerRowMinimal — zmiana ustawienia nie może przestawić dotychczasowego wyglądu.
        private static ListMetrics Density(ListDensity d)
        {
            switch (d)
            {
                case ListDensity.Minimal:
                    return new ListMetrics
                    {
                        ProtocolTile = true, ProtocolTag = false,
                        RowPadding = new Thickness(4, 2, 6, 2), RowMinHeight = 27, RowGap = 1
                    };
                case ListDensity.Dense:
                    // Bez kafelka: zostaje pasek 3 px. Wiersz schodzi z 27 px na 22 px, czyli o piątą
                    // część — przy trzydziestu serwerach to cztery pozycje więcej bez przewijania.
                    return new ListMetrics
                    {
                        ProtocolBar = true, ProtocolTag = false,
                        RowPadding = new Thickness(4, 1, 6, 1), RowMinHeight = 22, RowGap = 0
                    };
                default:
                    return new ListMetrics
                    {
                        Avatar = true, ProtocolTag = true,
                        RowPadding = new Thickness(4, 5, 6, 5), RowMinHeight = 0, RowGap = 1
                    };
            }
        }

        private static void Layout(ListMetrics m, GroupLayout layout, ListDensity d)
        {
            // Wcięcie wiersza w układzie z wcięciem — wartości przeniesione 1:1 z dotychczasowych
            // marginesów (18 px przy awatarze, 12 px przy kafelku i pasku).
            double baseIndent = d == ListDensity.Default ? 18 : 12;

            switch (layout)
            {
                case GroupLayout.Rail:
                    // Pas zabiera 3 px + 9 px oddechu; sekcja odsunięta od krawędzi o 8 px.
                    m.Rail = true; m.RailWidth = 3;
                    m.RowIndent = 0;                 // wcięcie daje kontener sekcji, nie wiersz
                    m.HeaderPadding = new Thickness(0, d == ListDensity.Default ? 7 : 5, 8, 3);
                    m.HeaderRule = false;            // sekcje rozdziela pas koloru, nie kreska
                    break;

                case GroupLayout.Flat:
                    m.RowIndent = 0;
                    m.HeaderPadding = new Thickness(10, 5, 10, 5);
                    m.StickyHeader = true;
                    m.HeaderRule = false;            // nagłówek ma własne tło, kreska byłaby zdublowaniem
                    m.FullBleedRow = true;
                    break;

                default:
                    // DOKŁADNIE dotychczasowy margines wiersza. Odejmowanie od niego lewego paddingu
                    // przesunęłoby treść o 4 px — a włączenie nowej opcji nie może ruszyć układu,
                    // którego użytkownik nie zmienił.
                    m.RowIndent = baseIndent;
                    m.HeaderPadding = new Thickness(12, d == ListDensity.Default ? 7 : 5, 8, 3);
                    m.HeaderRule = true;
                    break;
            }
        }
    }
}
