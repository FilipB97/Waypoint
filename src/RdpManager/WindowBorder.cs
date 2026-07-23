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

        /// <summary>Nakłada TERAZ i utrzymuje: synchronicznie z hooka WM_NCACTIVATE (ogranicza błysk),
        /// z handlera Activated + odroczonych dobić (ostatnie słowo po repaintach WPF-UI) oraz raz po
        /// pełnym wyrenderowaniu.</summary>
        public static void Keep(Window window)
        {
            if (window == null) return;
            Apply(window);
            // WM_NCACTIVATE: Windows/WPF-UI przemalowują krawędź (non-client) przy (de)aktywacji — m.in. gdy
            // zamknie się okno potomne (np. „O aplikacji") i główne wraca na wierzch. Zapis synchroniczny w tym
            // hooku ogranicza błysk, a odroczone dobicie w BorderHook domyka cykl. Hook jest statyczny (bez
            // domknięcia per-okno) i zwalnia się z HwndSource okna — brak wycieku.
            HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.AddHook(BorderHook);
            // Handler Activated — sprawdzona ścieżka sprzed #183: WPF-UI maluje akcent PO Activated, więc
            // dobicie odroczone stąd ląduje jako ostatnie. #183 usunął to w całości (zostawiając tylko zapis
            // synchroniczny z hooka, który leci PRZED repaintem WPF-UI) — obwódka zostawała kobaltowa NA STAŁE.
            window.Activated += (_, __) =>
            {
                Apply(window);
                window.Dispatcher.BeginInvoke(new Action(() => Apply(window)),
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            };
            // Dobij raz po pełnym wyrenderowaniu (WPF-UI kończy malować krawędź po Loaded) — asekuracja pierwszej klatki.
            window.Dispatcher.BeginInvoke(new Action(() => Apply(window)),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        private const int WM_NCACTIVATE = 0x0086;

        // Przy każdym WM_NCACTIVATE ponownie nakłada bieżącą specyfikację obwódki: raz SYNCHRONICZNIE
        // (ogranicza błysk — zanim klatka trafi na ekran) i raz ODROCZONO (ApplicationIdle).
        // Odroczone dobicie jest KONIECZNE: WPF-UI przemalowuje krawędź na akcent PÓŹNIEJ w tym samym
        // cyklu aktywacji (po zdarzeniu Activated), więc sam synchroniczny zapis przegrywa „ostatnie
        // słowo" i obwódka ZOSTAWAŁA kobaltowa na stałe (regresja z #183, która to dobicie usunęła).
        private static IntPtr BorderHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_NCACTIVATE && hwnd != IntPtr.Zero)
            {
                WriteBorder(hwnd);
                var h = hwnd;   // kopia do domknięcia (hwnd to parametr ref-świata WndProc)
                Application.Current?.Dispatcher.BeginInvoke(new Action(() => WriteBorder(h)),
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
            return IntPtr.Zero;
        }

        // Zapis bieżącej specyfikacji wprost na hwnd (okno mogło już nie mieć obiektu Window — np. w trakcie
        // zamykania; nieaktualny uchwyt jest nieszkodliwy, DWM zwróci błąd, który ignorujemy).
        private static void WriteBorder(IntPtr hwnd)
        {
            uint val = SpecToColorRef(_spec);
            try { DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref val, sizeof(uint)); }
            catch { /* starszy DWM bez atrybutu — bez znaczenia */ }
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
