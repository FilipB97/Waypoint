using System;
using System.Windows.Media;

namespace RdpManager.Core
{
    /// <summary>
    /// Rachunek kontrastu wg WCAG 2.1 i wymuszanie progu czytelności. Wydzielone z widoków, bo to czysta
    /// matematyka bez stanu — da się to przetestować, w odróżnieniu od reszty warstwy wizualnej, którą
    /// można tylko obejrzeć na Windowsie.
    ///
    /// Po co wymuszanie progu: presety motywu (Themes/ThemePresets) niosą WŁASNE kolory tekstu, wzięte
    /// z kanonicznych palet (Nord, Tokyo Night, GitHub, Solarized…). Tam trzeci stopień tekstu to kolor
    /// KOMENTARZA w edytorze kodu — celowo przygaszony. Waypoint używa go do etykiet pól i komunikatów
    /// pustych stanów, czyli do treści, którą trzeba przeczytać. Efekt: we wszystkich dwunastu presetach
    /// TextTer wypadał między 2.07 a 3.77 wobec własnego panelu, przy progu 4.5.
    ///
    /// Dlatego nie podmieniamy dwunastu tabel kolorów (to zabrałoby presetom tożsamość i trzeba by
    /// pamiętać o każdym nowym), tylko trzymamy REGUŁĘ: barwa zostaje z presetu, jasność jest dociągana
    /// dokładnie tyle, ile trzeba, żeby przejść próg.
    /// </summary>
    public static class ColorMath
    {
        /// <summary>Luminancja względna wg WCAG 2.1 (definicja z sekcji „relative luminance").</summary>
        public static double RelativeLuminance(Color c)
        {
            double F(byte v)
            {
                double s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * F(c.R) + 0.7152 * F(c.G) + 0.0722 * F(c.B);
        }

        /// <summary>Iloraz kontrastu wg WCAG 2.1 (1.4.3): od 1.0 (te same kolory) do 21.0 (biel na czerni).</summary>
        public static double Contrast(Color a, Color b)
        {
            double la = RelativeLuminance(a), lb = RelativeLuminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        /// <summary>Liniowa interpolacja między dwoma kolorami (t = 0 daje <paramref name="from"/>).</summary>
        public static Color Mix(Color from, Color to, double t)
        {
            t = Math.Max(0.0, Math.Min(1.0, t));
            return Color.FromRgb(
                (byte)Math.Round(from.R + (to.R - from.R) * t),
                (byte)Math.Round(from.G + (to.G - from.G) * t),
                (byte)Math.Round(from.B + (to.B - from.B) * t));
        }

        /// <summary>Domyślny ciemny inkaust aplikacji — ten sam odcień, co kanwa motywu ciemnego.</summary>
        public static readonly Color InkDark = Color.FromRgb(0x14, 0x16, 0x20);

        /// <summary>
        /// Czy na danym tle czytelniej wypadnie CIEMNY inkaust niż biel. Reguła nie brzmi „lepszy kontrast
        /// wygrywa", tylko „biel, chyba że ciemny jest WYRAŹNIE lepszy" (domyślnie 1.3x) — i to jest
        /// świadome. Bez progu ciemny wygrywałby minimalnie na kolorach takich jak akcent Waypointa
        /// (4.52 vs 3.99), przez co napis na przycisku akcji zmieniłby się z białego na czarny przy
        /// różnicy nie do zauważenia, a obok stałyby elementy z bielą na tym samym tle. Próg przełącza
        /// inkaust dopiero tam, gdzie biel naprawdę ginie: na jasnych akcentach i kolorach grup
        /// (bursztyn, zieleń, turkus, pomarańcz presetu „Claude") kontrast bieli spada do 2.0-2.8.
        ///
        /// Używane w dwóch miejscach o tym samym problemie: inicjały na kolorowym awatarze i tekst na
        /// przycisku w kolorze akcentu. Jedna reguła, bo to jedno pytanie.
        /// </summary>
        public static bool PrefersDarkInk(Color background, double threshold = 1.3)
            => Contrast(background, InkDark) >= Contrast(background, Colors.White) * threshold;

        /// <summary>
        /// Dociąga <paramref name="foreground"/> do progu kontrastu wobec <paramref name="background"/>,
        /// mieszając go z bielą (na ciemnym tle) albo z czernią (na jasnym). Kolor, który już przechodzi,
        /// wraca bez zmian — preset zachowuje swoje kolory wszędzie tam, gdzie są czytelne.
        /// Kroki po 1%: bierzemy PIERWSZY, który przechodzi, więc zmiana jest najmniejsza z możliwych.
        /// </summary>
        public static Color EnsureContrast(Color foreground, Color background, double target)
        {
            if (Contrast(foreground, background) >= target) return foreground;

            // Kierunek dociągania wyznacza tło, nie tekst: na ciemnym panelu rozjaśniamy, na jasnym
            // przyciemniamy. Inaczej na jasnym tle „poprawialibyśmy" szary tekst w stronę bieli.
            Color toward = RelativeLuminance(background) < 0.5 ? Colors.White : Colors.Black;
            for (int i = 1; i <= 100; i++)
            {
                Color c = Mix(foreground, toward, i / 100.0);
                if (Contrast(c, background) >= target) return c;
            }
            return toward;   // nieosiągalne dla progów <= 21, ale niech funkcja zawsze coś zwraca
        }
    }
}
