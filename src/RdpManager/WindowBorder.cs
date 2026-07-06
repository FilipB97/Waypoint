using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RdpManager
{
    /// <summary>
    /// Zdejmuje kolorową (akcentową) obwódkę z krawędzi okien na Windows 11. Ramkę okna FluentWindow
    /// rysuje DWM, a domyślnie przejmuje ona kolor akcentu — stąd kobaltowa kreska wokół okna. Wymuszamy
    /// DWMWA_BORDER_COLOR = „brak", więc krawędź przestaje być kolorowa. Akcent na KONTROLKACH (przyciski
    /// Primary, przełączniki, focus, ProgressRing) zostaje bez zmian — to osobny mechanizm WPF-UI.
    /// Na Windows 10 (brak DWMWA_BORDER_COLOR) wywołanie jest nieszkodliwym no-opem — tam akcentowej
    /// obwódki i tak nie ma.
    /// </summary>
    internal static class WindowBorder
    {
        private const int DWMWA_BORDER_COLOR = 34;       // Windows 11 (build 22000+)
        private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE; // „nie rysuj kolorowej ramki"

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref uint value, int size);

        /// <summary>Wołane z Loaded — uchwyt okna już istnieje. Idempotentne.</summary>
        public static void Neutralize(Window window)
        {
            if (window == null) return;
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            uint none = DWMWA_COLOR_NONE;
            try { DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref none, sizeof(uint)); }
            catch { /* starszy DWM bez tego atrybutu — bez znaczenia */ }
        }
    }
}
