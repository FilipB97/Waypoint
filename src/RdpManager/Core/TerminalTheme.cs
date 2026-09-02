using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Media;

namespace RdpManager.Core
{
    /// <summary>
    /// Kolory terminala (xterm.js w WebView2) WYPROWADZONE Z PALETY, a nie wpisane na sztywno.
    ///
    /// Terminal żyje poza drzewem zasobów WPF, więc nie widzi DynamicResource — i przez to miał
    /// wszystkie barwy zaszyte jako literały sterowane samym „jasny/ciemny". Dwa skutki: presety
    /// palety i własny kolor akcentu nigdy do niego nie docierały (wybór motywu Tokyo Night
    /// przemalowywał całe okno POZA terminalem), a jeden z tych literałów — #2C2E37 — był kolorem
    /// panelu sprzed zmiany palety, więc pasek szukania odstawał od każdego innego panelu w oknie.
    ///
    /// Klucze dobrane tak, żeby PRESETY DZIAŁAŁY: preset nadpisuje Canvas, Panel, Border, stopnie
    /// tekstu i Accent, więc tylko z tych kluczy da się zbudować motyw, który za nim podąża.
    /// </summary>
    public sealed class TerminalTheme
    {
        // ---- xterm ----
        /// <summary>Tło terminala. Canvas, bo terminal to powierzchnia „najgłębsza", pełnoekranowa.</summary>
        public string Background { get; private set; }
        public string Foreground { get; private set; }
        public string Cursor { get; private set; }
        /// <summary>Zaznaczenie — akcent z kryciem, więc tekst pod spodem zostaje czytelny.</summary>
        public string Selection { get; private set; }

        // ---- pasek szukania (nakładka HTML nad terminalem) ----
        public string Panel { get; private set; }
        public string Border { get; private set; }
        public string TextPrim { get; private set; }
        public string TextTer { get; private set; }
        public string Accent { get; private set; }
        public string InputBg { get; private set; }
        public string HoverBg { get; private set; }

        /// <summary>
        /// Buduje motyw z podanego czytnika palety. Czytnik jest parametrem (a nie odczytem wprost),
        /// żeby dało się to sprawdzić testem bez uruchamiania aplikacji WPF.
        /// Wartości awaryjne odpowiadają palecie bazowej — brak klucza nie może dać czarnego ekranu.
        /// </summary>
        public static TerminalTheme From(Func<string, Color?> resolve, bool light)
        {
            string Hex(string key, string fallback)
            {
                var c = resolve?.Invoke(key);
                return c == null ? fallback : $"#{c.Value.R:X2}{c.Value.G:X2}{c.Value.B:X2}";
            }

            string Rgba(string key, double alpha, string fallback)
            {
                var c = resolve?.Invoke(key);
                if (c == null) return fallback;
                return $"rgba({c.Value.R},{c.Value.G},{c.Value.B},{alpha.ToString("0.##", CultureInfo.InvariantCulture)})";
            }

            return new TerminalTheme
            {
                Background = Hex("Canvas", light ? "#EEF0F3" : "#0F1014"),
                Foreground = Hex("TextPrim", light ? "#1B1D22" : "#E7E8EE"),
                Cursor = Hex("Accent", light ? "#5B4BD6" : "#6C6DFF"),
                Selection = Rgba("Accent", light ? 0.22 : 0.34, light ? "rgba(91,75,214,.22)" : "rgba(108,109,255,.34)"),

                Panel = Hex("Panel", light ? "#FAFBFC" : "#282A36"),
                Border = BorderOf(resolve, light),
                TextPrim = Hex("TextPrim", light ? "#1B1D22" : "#E7E8EE"),
                TextTer = Hex("TextTer", light ? "#6B6F78" : "#9396A6"),
                Accent = Hex("Accent", light ? "#5B4BD6" : "#6C6DFF"),
                InputBg = light ? "rgba(0,0,0,.04)" : "rgba(255,255,255,.08)",
                HoverBg = light ? "rgba(0,0,0,.06)" : "rgba(255,255,255,.10)"
            };
        }

        private static string BorderOf(Func<string, Color?> resolve, bool light)
        {
            var c = resolve?.Invoke("Border");
            if (c == null) return light ? "rgba(0,0,0,.13)" : "rgba(255,255,255,.13)";
            string a = (c.Value.A / 255.0).ToString("0.###", CultureInfo.InvariantCulture);
            return $"rgba({c.Value.R},{c.Value.G},{c.Value.B},{a})";
        }

        /// <summary>Motyw xterm.js jako literał obiektu JS (wstrzykiwany do strony i wysyłany na żywo).</summary>
        public string ToXtermObject()
            => "{ background:'" + Background + "', foreground:'" + Foreground +
               "', cursor:'" + Cursor + "', selectionBackground:'" + Selection + "' }";

        /// <summary>Zmienne CSS paska szukania — jedna definicja dla budowy strony i dla przemalowania.</summary>
        public Dictionary<string, string> CssVars() => new Dictionary<string, string>
        {
            ["--wp-bg"] = Background,
            ["--wp-panel"] = Panel,
            ["--wp-border"] = Border,
            ["--wp-tx"] = TextPrim,
            ["--wp-tx3"] = TextTer,
            ["--wp-accent"] = Accent,
            ["--wp-input"] = InputBg,
            ["--wp-hover"] = HoverBg
        };
    }
}
