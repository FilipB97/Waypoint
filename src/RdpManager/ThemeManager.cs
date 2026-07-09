using System;
using System.Windows;
using Microsoft.Win32;
using Wpf.Ui.Appearance;

namespace RdpManager
{
    /// <summary>
    /// Przełącza motyw aplikacji: „Dark" / „Light" / „System". Ustawia motyw kontrolek WPF-UI
    /// (ApplicationThemeManager) oraz podmienia własną paletę Waypoint (Themes/Palette.*.xaml)
    /// w zasobach aplikacji. Elementy w XAML używają DynamicResource, więc odświeżają się w locie.
    /// </summary>
    public static class ThemeManager
    {
        // WPF-UI domyślnie bierze akcent SYSTEMOWY (stąd „szare" przyciski Primary, ProgressRing, focus,
        // przełączniki) — wymuszamy własny akcent PO zastosowaniu motywu, żeby akcentowe kontrolki WPF-UI
        // zgadzały się z paletą Waypoint (klucz „Accent") i UI nie było monochromatyczne.
        // Compass §2 rozróżnia akcent interakcji per motyw: ciemny #4C86FF (jaśniejszy, na chłodnej czerni),
        // jasny #2657D6 (kobalt — lepszy kontrast białego tekstu na akcencie). Faza 4 (§4.7) uczyni to
        // konfigurowalnym; tu zostaje domyślna para Compass.
        private static readonly System.Windows.Media.Color AccentDark =
            System.Windows.Media.Color.FromRgb(0x4C, 0x86, 0xFF);
        private static readonly System.Windows.Media.Color AccentLight =
            System.Windows.Media.Color.FromRgb(0x26, 0x57, 0xD6);

        /// <summary>Czy aktualnie obowiązuje jasny motyw — ostatni wynik <see cref="Apply"/>. Czytane m.in.
        /// przez XtermControl, który (WebView2/xterm.js) nie żyje w drzewie zasobów WPF i nie może
        /// same reagować na DynamicResource (D5 z przeglądu).</summary>
        public static bool IsLight { get; private set; }

        public static void Apply(string theme)
        {
            bool light = theme == "Light" || (theme == "System" && SystemIsLight());
            IsLight = light;
            var appTheme = light ? ApplicationTheme.Light : ApplicationTheme.Dark;
            ApplicationThemeManager.Apply(appTheme);
            ApplicationAccentColorManager.Apply(light ? AccentLight : AccentDark, appTheme);   // akcent Compass per motyw
            WindowBorder.ReapplyAll();   // WPF-UI po zmianie motywu/akcentu przemalowuje krawędź — przywróć wybraną obwódkę
            SwapPalette(light);
        }

        /// <summary>Czy Windows jest ustawiony na jasny motyw aplikacji (klucz Personalize).</summary>
        private static bool SystemIsLight()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k?.GetValue("AppsUseLightTheme") is int v && v != 0;
            }
            catch { return false; }
        }

        private static void SwapPalette(bool light)
        {
            var dicts = Application.Current.Resources.MergedDictionaries;
            for (int i = dicts.Count - 1; i >= 0; i--)
            {
                var src = dicts[i].Source?.OriginalString ?? "";
                if (src.IndexOf("Palette.", StringComparison.OrdinalIgnoreCase) >= 0)
                    dicts.RemoveAt(i);
            }
            dicts.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Themes/Palette." + (light ? "Light" : "Dark") + ".xaml")
            });
        }
    }
}
