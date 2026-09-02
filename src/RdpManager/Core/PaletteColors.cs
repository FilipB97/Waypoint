using System.Windows;
using System.Windows.Media;

namespace RdpManager.Core
{
    /// <summary>
    /// Odczyt koloru z ŻYWEJ palety po kluczu. Jedno miejsce, bo powierzchnie poza drzewem zasobów WPF
    /// (terminal w WebView2, pulpit w WebView2) muszą same dosięgnąć palety — nie widzą DynamicResource.
    ///
    /// Obsługuje też pędzle gradientowe: „Canvas" jest w palecie bazowej gradientem, a pod presetem
    /// kolorem jednolitym. Odczyt zakładający SolidColorBrush zwracałby dla palety bazowej wartość
    /// awaryjną — czyli dokładnie tam, gdzie najczęściej się patrzy.
    /// </summary>
    public static class PaletteColors
    {
        public static Color? Of(string key)
        {
            var res = Application.Current?.TryFindResource(key);
            switch (res)
            {
                case SolidColorBrush s: return s.Color;
                // Gradient: pierwszy stop definiuje wrażenie barwy (tak samo liczy AvatarInk).
                case GradientBrush g when g.GradientStops.Count > 0: return g.GradientStops[0].Color;
                case Color c: return c;
                default: return null;
            }
        }

        /// <summary>Kolor jako „#RRGGBB"; <paramref name="fallback"/> gdy klucza nie ma.</summary>
        public static string Hex(string key, string fallback)
        {
            var c = Of(key);
            return c == null ? fallback : $"#{c.Value.R:X2}{c.Value.G:X2}{c.Value.B:X2}";
        }

        /// <summary>Kolor jako „rgba(...)" — zachowuje kanał alfa kluczy półprzezroczystych.</summary>
        public static string Rgba(string key, string fallback)
        {
            var c = Of(key);
            if (c == null) return fallback;
            string a = (c.Value.A / 255.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            return $"rgba({c.Value.R},{c.Value.G},{c.Value.B},{a})";
        }
    }
}
