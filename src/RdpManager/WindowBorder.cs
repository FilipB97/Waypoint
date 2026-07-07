using System;
using System.Windows;
using System.Windows.Interop;

namespace RdpManager
{
    /// <summary>
    /// Steruje kolorem obwódki (krawędzi) okien FluentWindow na Windows 11 przez DWMWA_BORDER_COLOR.
    /// Domyślnie WPF-UI zostawia krawędź w kolorze akcentu (od #49 = kobalt) — użytkownik może to zmienić
    /// w Ustawieniach: „brak" (żadnej kolorowej krawędzi), „systemowa" (akcent Windows) albo wybrany kolor.
    /// WPF-UI oraz sam Windows przemalowują krawędź PO Loaded i przy każdej aktywacji, więc nakładamy ją
    /// wielokrotnie: na Loaded (Keep), przy aktywacji, po wyrenderowaniu i z hooka WM_NCACTIVATE (MainWindow).
    /// Na Windows 10 (brak DWMWA_BORDER_COLOR) wywołania są nieszkodliwym no-opem.
    /// </summary>
    internal static class WindowBorder
    {
        private const int DWMWA_BORDER_COLOR = 34;         // Windows 11 (build 22000+)
        private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;  // „nie rysuj kolorowej ramki"
        private const uint DWMWA_COLOR_DEFAULT = 0xFFFFFFFF; // domyślna (systemowy akcent)

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref uint value, int size);

        // Bieżąca specyfikacja z ustawień: "" = brak, "System" = akcent systemowy, "#RRGGBB" = kolor.
        private static string _spec = "";

        /// <summary>Ustawia specyfikację koloru i od razu nakłada ją na otwarte okna.</summary>
        public static void SetSpec(string spec)
        {
            _spec = spec ?? "";
            ReapplyAll();
        }

        /// <summary>Nakłada bieżącą specyfikację na wszystkie otwarte okna (teraz + deferred po WPF-UI).
        /// Wołane po zmianie ustawienia oraz po zmianie motywu/akcentu (WPF-UI wtedy przemalowuje krawędź).</summary>
        public static void ReapplyAll()
        {
            if (Application.Current == null) return;
            foreach (Window w in Application.Current.Windows)
            {
                var win = w;
                Apply(win);
                win.Dispatcher.BeginInvoke(new Action(() => Apply(win)),
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        }

        /// <summary>Nakłada bieżącą specyfikację koloru obwódki na okno. Bezpieczne na Win10 (no-op).</summary>
        public static void Apply(Window window)
        {
            if (window == null) return;
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            uint val = SpecToColorRef(_spec);
            try { DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref val, sizeof(uint)); }
            catch { /* starszy DWM bez tego atrybutu — bez znaczenia */ }
        }

        /// <summary>Nakłada TERAZ i utrzymuje: przy każdej aktywacji okna oraz raz po pełnym wyrenderowaniu
        /// (ApplicationIdle — już po tym, jak WPF-UI skończy malować krawędź).</summary>
        public static void Keep(Window window)
        {
            if (window == null) return;
            Apply(window);
            window.Activated += (_, __) => Apply(window);
            window.Dispatcher.BeginInvoke(new Action(() => Apply(window)),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        // "" → brak; "System"/"default" → systemowy akcent; "#RRGGBB" → COLORREF (0x00BBGGRR); błędny → brak.
        private static uint SpecToColorRef(string spec)
        {
            spec = (spec ?? "").Trim();
            if (spec.Length == 0) return DWMWA_COLOR_NONE;
            if (spec.Equals("System", StringComparison.OrdinalIgnoreCase)
                || spec.Equals("default", StringComparison.OrdinalIgnoreCase)) return DWMWA_COLOR_DEFAULT;
            try
            {
                var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(spec);
                return (uint)(c.R | (c.G << 8) | (c.B << 16));   // COLORREF = 0x00BBGGRR
            }
            catch { return DWMWA_COLOR_NONE; }
        }
    }
}
