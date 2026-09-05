using System;
using System.Windows;

namespace RdpManager.Core
{
    /// <summary>Gdzie aktywna karta nosi akcent. Kształt paska kart sprowadza się w praktyce do tego wyboru.</summary>
    public enum TabMark
    {
        /// <summary>Kreska pod treścią — styl domyślny.</summary>
        Bottom,
        /// <summary>Kreska nad kartą — styl blokowy (karty stykają się bokami, jak w edytorze).</summary>
        Top,
        /// <summary>Pionowy pasek przy lewej krawędzi — ten sam język, którego używa nawigacja Ustawień.</summary>
        Left
    }

    /// <summary>Styl paska kart z ustawień. Wartości zapisywane w settings.json jako tekst.</summary>
    public enum TabStyle
    {
        /// <summary>Zaokrąglona karta z wypełnieniem i podkreśleniem.</summary>
        Default,
        /// <summary>Bez zaokrągleń, karty stykają się bokami, akcent u góry.</summary>
        Block,
        /// <summary>Jak domyślny, ale akcent to pionowy pasek przy lewej krawędzi.</summary>
        Marker
    }

    /// <summary>
    /// Wymiary karty i paska kart dla wybranego stylu. Wyliczone TUTAJ, a nie rozsypane po
    /// TabStripController, bo to jedyne miejsce, w którym da się je sprawdzić testem — kontroler
    /// buduje kontrolki WPF i nie uruchomi się poza aplikacją.
    ///
    /// Styl blokowy jest jedynym, który ma WYMUSZONĄ wysokość karty: karty stykają się bokami i
    /// wypełniają pasek na całą wysokość, więc to wysokość paska decyduje o wysokości karty, a nie
    /// odwrotnie. Pozostałe style liczą wysokość z treści, dokładnie jak dotąd.
    /// </summary>
    public sealed class TabMetrics
    {
        /// <summary>Promień zaokrąglenia karty (0 = kanciasta).</summary>
        public double Radius { get; private set; }
        /// <summary>Grubość obramowania karty. W stylu blokowym tylko prawa krawędź — to separator.</summary>
        public Thickness Border { get; private set; }
        /// <summary>Wewnętrzny margines treści karty.</summary>
        public Thickness Padding { get; private set; }
        /// <summary>Odstęp do sąsiedniej karty.</summary>
        public Thickness Margin { get; private set; }
        /// <summary>Gdzie karta nosi akcent.</summary>
        public TabMark Mark { get; private set; }
        /// <summary>Grubość paska akcentu (2 px poziomo, 3 px pionowo — jak w nawigacji Ustawień).</summary>
        public double MarkSize { get; private set; }
        /// <summary>
        /// Czy karta rezerwuje miejsce na dolną kreskę, nawet gdy akcent jest gdzie indziej.
        /// Dzięki temu przełączenie stylu „domyślny ⇄ lewy znacznik" nie zmienia wysokości paska
        /// i obszar sesji pod nim nie podskakuje.
        /// </summary>
        public bool ReserveBottom { get; private set; }
        /// <summary>Pionowy margines paska kart (odstęp karty od krawędzi paska).</summary>
        public double StripPadV { get; private set; }
        /// <summary>Poziomy margines paska. Zero = karty dobijają do krawędzi (styl blokowy).</summary>
        public double StripPadH { get; private set; }
        /// <summary>Klucz palety na tło AKTYWNEJ karty.</summary>
        public string ActiveFill { get; private set; }
        /// <summary>
        /// Karta aktywna zrasta się z obszarem POD paskiem: bierze jego kolor i traci dolną krawędź,
        /// więc dolna kreska paska jest pod nią PRZERWANA. To jest cała istota karty przeglądarkowej
        /// i edytorowej — i jedyna rzecz, która odróżnia taki pasek od rzędu wypełnionych prostokątów.
        /// Bez tego blok i znacznik czytają się jako ten sam obrazek, co zostało zgłoszone z użycia.
        /// </summary>
        public bool FuseWithContent { get; private set; }
        /// <summary>
        /// Karta nie pokazuje awatara (sam znacznik stanu i nazwa). Poza gęstością minimalną włącza to
        /// tryb skupienia w stylu blokowym: pasek kart zastępuje tam pasek tytułu, więc ma być niski.
        /// </summary>
        public bool HideAvatar { get; private set; }
        /// <summary>Wymuszona wysokość karty; 0 = z treści.</summary>
        public double TabHeight { get; private set; }

        /// <summary>Rozmiar przycisków okna na pasku kart w trybie skupienia — pasek musi je pomieścić.</summary>
        public const double FocusButton = 28;
        /// <summary>Ten sam przycisk w widoku minimalnym.</summary>
        public const double FocusButtonMinimal = 24;

        public static TabStyle Parse(string value)
        {
            if (string.Equals(value, "Block", StringComparison.OrdinalIgnoreCase)) return TabStyle.Block;
            if (string.Equals(value, "Marker", StringComparison.OrdinalIgnoreCase)) return TabStyle.Marker;
            return TabStyle.Default;
        }

        /// <param name="minimal">Widok minimalny (ListStyle = „Minimal") — bez awatara, ciaśniej.</param>
        /// <param name="focus">Tryb skupienia — pasek kart pełni rolę paska tytułu, więc liczy się każdy piksel.</param>
        public static TabMetrics For(TabStyle style, bool minimal, bool focus)
        {
            TabMetrics m;
            switch (style)
            {
                case TabStyle.Block: m = Block(minimal, focus); break;
                case TabStyle.Marker: m = Marker(minimal); break;
                default: m = Default(minimal); break;
            }
            m.HideAvatar = minimal || (style == TabStyle.Block && focus);
            return m;
        }

        // Stan obecny — wartości przeniesione 1:1 z BuildTabDefault / BuildTabMinimal.
        private static TabMetrics Default(bool minimal) => new TabMetrics
        {
            Radius = 8,
            Border = new Thickness(1),
            Padding = minimal ? new Thickness(11, 2, 6, 2) : new Thickness(10, 6, 7, 5),
            Margin = new Thickness(0, 0, 4, 0),
            Mark = TabMark.Bottom,
            MarkSize = 2,
            ReserveBottom = true,
            StripPadV = minimal ? 2 : 6,
            StripPadH = 8,
            ActiveFill = "Panel",
            TabHeight = 0
        };

        // Lewy znacznik: geometria dokładnie jak w stylu domyślnym — zmienia się WYŁĄCZNIE miejsce
        // akcentu. Dolna kreska zostaje jako rozpórka (niewidoczna), więc pasek ma tę samą wysokość
        // i przełączanie stylu nie przesuwa obszaru sesji.
        //
        // Lewe wypełnienie (10 px domyślnie, 11 px minimalnie) mieści pasek 3 px odsunięty o 4 px od
        // krawędzi, więc treść karty nie wymaga przesunięcia.
        private static TabMetrics Marker(bool minimal)
        {
            var m = Default(minimal);
            m.Mark = TabMark.Left;
            m.MarkSize = 3;
            return m;
        }

        // Styl blokowy. Karty bez zaokrągleń i bez odstępu, rozdzielone kreską 1 px, akcent nad kartą.
        //
        // Wysokość jest wymuszona i MALEJE w widoku minimalnym oraz w trybie skupienia. Powód jest
        // inny w każdym z tych przypadków: minimalny nie ma awatara, więc karta pełnej wysokości
        // byłaby w większości pusta; w skupieniu pasek kart zastępuje pasek tytułu i zjada wysokość
        // samej sesji. Dolna granica to przycisk okna, który w skupieniu stoi na tym samym pasku.
        private static TabMetrics Block(bool minimal, bool focus)
        {
            //   zwykle          : 40 px przy awatarze, 28 px bez niego,
            //   tryb skupienia  : 26 px ZAWSZE — karta traci tam awatar (HideAvatar), więc granicą
            //                     jest wiersz tekstu (~16 px) i przycisk okna w wersji zwartej (24 px).
            // Dla porównania: pasek w stylu domyślnym ma 48 px (widok domyślny) i ~30 px (minimalny).
            double h = focus ? 26 : (minimal ? 28 : 40);
            return new TabMetrics
            {
                Radius = 0,
                Border = new Thickness(0, 0, 1, 0),   // separator zamiast obrysu
                Padding = minimal ? new Thickness(11, 0, 8, 0) : new Thickness(10, 0, 8, 0),
                Margin = new Thickness(0),
                Mark = TabMark.Top,
                MarkSize = 3,                         // 2 px na samej krawędzi paska było za cienkie
                ReserveBottom = false,                // wysokość daje TabHeight, nie rozpórka
                StripPadV = 0,                        // karta dotyka krawędzi paska — stąd „blok"
                StripPadH = 0,                        // ...i dobija do jego boków, jak segment kontrolki
                // „Panel" — DOKŁADNIE ten sam klucz, którym pomalowany jest SessionToolbar leżący
                // bezpośrednio pod paskiem kart. To nie jest powtórzenie stylu znacznika: tam Panel jest
                // wypełnieniem pływającej karty, tu jest kolorem obszaru, w który karta się WTAPIA.
                // Rozróżnia je FuseWithContent — przerwana dolna krawędź paska pod aktywną kartą.
                ActiveFill = "Panel",
                FuseWithContent = true,
                TabHeight = h
            };
        }
    }
}
