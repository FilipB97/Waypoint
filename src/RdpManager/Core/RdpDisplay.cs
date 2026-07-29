using System;
using System.Globalization;

namespace RdpManager.Core
{
    /// <summary>
    /// Czyste przeliczenia rozdzielczości i skalowania (DPI) sesji RDP — bez WPF/ActiveX, więc testowalne.
    /// Na kontrolce stosuje je <c>RdpDynamicResolution</c>.
    ///
    /// Dwa niezależne pokrętła (Ustawienia → Połączenie → Domyślne RDP):
    ///  - ROZDZIELCZOŚĆ: 0×0 = „dopasuj do okna" (domyślne — sesja trzyma natywne piksele panelu, obraz 1:1),
    ///    wartość stała (np. 1600×900) = sesja negocjuje ten rozmiar, a SmartSizing rozciąga go do okna.
    ///  - SKALOWANIE (DPI): 0 = „jak ekran lokalny" (mnożnik DPI okna — tak robi mstsc), inaczej stałe procenty.
    ///    Trafia do ulDesktopScaleFactor (UpdateSessionDisplaySettings) i „DesktopScaleFactor" przed Connect,
    ///    więc zdalny pulpit RYSUJE interfejs większy — bez utraty ostrości (odwrotnie niż SmartSizing).
    ///    Na ekranie 4K/HiDPI sesja 1:1 przy 100% daje mikroskopijny tekst; to jest na to lekarstwo.
    /// </summary>
    public static class RdpDisplay
    {
        // Zakresy z dokumentacji IMsRdpClient9::UpdateSessionDisplaySettings: ulDesktopScaleFactor 100..500,
        // ulDeviceScaleFactor TYLKO 100/140/180 (inna wartość => E_INVALIDARG, cała zmiana odrzucona).
        // Device trzymamy na 100 („bez dodatkowego skalowania urządzenia") — DPI sesji ustala desktopScaleFactor.
        public const int MinScale = 100;
        public const int MaxScale = 500;
        public const uint DeviceScaleFactor = 100u;

        /// <summary>Czy ustawienie opisuje STAŁĄ rozdzielczość (a nie „dopasuj do okna").</summary>
        public static bool IsFixed(int width, int height)
            => width >= RdpUtils.MinDim && height >= RdpUtils.MinDim;

        /// <summary>
        /// Sprowadza zapisaną parę do postaci używalnej przez kontrolkę: (0, 0) = „dopasuj do okna",
        /// inaczej wymiary znormalizowane jak przy Display-Update (parzyste, [200..8192]).
        /// Wartości poniżej minimum traktujemy jak brak wyboru — plik settings.json bywa edytowany ręcznie.
        /// </summary>
        public static (int Width, int Height) Normalize(int width, int height)
            => IsFixed(width, height)
                ? (RdpUtils.NormalizeDim(width), RdpUtils.NormalizeDim(height))
                : (0, 0);

        /// <summary>„1920x1080" (albo „1920×1080") → (1920, 1080); „", „Auto", śmieci → (0, 0) = dopasuj do okna.</summary>
        public static (int Width, int Height) ParseResolution(string tag)
        {
            string s = (tag ?? "").Trim();
            if (s.Length == 0) return (0, 0);

            int sep = s.IndexOfAny(new[] { 'x', 'X', '×' });
            if (sep <= 0 || sep == s.Length - 1) return (0, 0);

            if (!int.TryParse(s.Substring(0, sep).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)) return (0, 0);
            if (!int.TryParse(s.Substring(sep + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)) return (0, 0);
            return Normalize(w, h);
        }

        /// <summary>Odwrotność <see cref="ParseResolution"/> — Tag pozycji listy dla zapisanej wartości („" = auto).</summary>
        public static string FormatResolution(int width, int height)
        {
            var (w, h) = Normalize(width, height);
            return w == 0 ? "" : w.ToString(CultureInfo.InvariantCulture) + "x" + h.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Tag pozycji listy skalowania → wartość do zapisu: „" / „Auto" / śmieci = 0 („jak ekran lokalny").</summary>
        public static int ParseScale(string tag)
        {
            string s = (tag ?? "").Trim().TrimEnd('%').Trim();
            if (s.Length == 0) return 0;
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pct) || pct <= 0) return 0;
            return Math.Clamp(pct, MinScale, MaxScale);
        }

        /// <summary>
        /// Skala DPI wysyłana serwerowi. <paramref name="configured"/> &gt; 0 = wybór użytkownika (przycięty do
        /// [100..500]); 0 = „jak ekran lokalny", czyli mnożnik DPI okna (1.5 → 150%). Zaokrąglamy do 5%, bo
        /// mnożnik z <c>VisualTreeHelper.GetDpi</c> bywa niecałkowity (np. 1.4999), a serwer i tak operuje
        /// na okrągłych krokach DPI.
        /// </summary>
        public static int EffectiveScale(int configured, double localDpiScale)
        {
            if (configured > 0) return Math.Clamp(configured, MinScale, MaxScale);
            if (double.IsNaN(localDpiScale) || double.IsInfinity(localDpiScale) || localDpiScale <= 0) return MinScale;

            int pct = (int)Math.Round(localDpiScale * 100.0 / 5.0, MidpointRounding.AwayFromZero) * 5;
            return Math.Clamp(pct, MinScale, MaxScale);
        }
    }
}
