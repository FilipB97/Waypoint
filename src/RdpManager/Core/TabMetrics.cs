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
        /// <summary>
        /// Klucz palety na tło AKTYWNEJ karty. To jest sygnał DOMINUJĄCY — ten, który widać z odległości
        /// rzutu oka, zanim ktokolwiek zauważy promień czy położenie paska akcentu. Style muszą się
        /// różnić właśnie tutaj, inaczej różnią się tylko w szczegółach i czytają jako to samo.
        /// </summary>
        public string ActiveFill { get; private set; }
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
            switch (style)
            {
                case TabStyle.Block: return Block(minimal, focus);
                case TabStyle.Marker: return Marker(minimal);
                default: return Default(minimal);
            }
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
            //   widok domyślny : 40 px zwykle, 36 px w skupieniu — mieści awatar 17 px i przycisk okna 28 px,
            //   widok minimalny : 28 px zwykle, 26 px w skupieniu — nie ma awatara, więc granicą jest
            //                     wiersz tekstu (~16 px) i przycisk okna w wersji minimalnej (24 px).
            // Dla porównania: pasek w stylu domyślnym ma dziś 48 px (widok domyślny) i ~30 px (minimalny).
            double h = minimal ? (focus ? 26 : 28) : (focus ? 36 : 40);
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
                // Wypełnienie akcentem, a NIE „Panel" jak w pozostałych stylach. Powód jest zmierzony:
                // dopóki blok i znacznik malowały aktywną kartę tym samym „Panel", różniły się wyłącznie
                // promieniem, odstępem i położeniem paska 2 px — czyli szczegółami, które przy rzucie oka
                // znikają, i oba style czytały się jako ten sam. AccentSoft odsuwa je od siebie o ΔE 11,7
                // (motyw ciemny) i 14,3 (jasny). W motywie jasnym to zarazem naprawa czytelności samego
                // stanu aktywnego: „Panel" dzieli od paska ΔE 2,9, AccentSoft — 12,0.
                ActiveFill = "AccentSoft",
                TabHeight = h
            };
        }
    }
}
