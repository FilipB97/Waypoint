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
        // Marka = kobalt #2657D6. WPF-UI domyślnie bierze akcent SYSTEMOWY (stąd „szare" przyciski Primary,
        // ProgressRing, focus, przełączniki) — wymuszamy własny akcent PO zastosowaniu motywu, żeby akcentowe
        // kontrolki były kobaltowe i UI przestało wyglądać monochromatycznie.
        private static readonly System.Windows.Media.Color BrandAccent =
            System.Windows.Media.Color.FromRgb(0x26, 0x57, 0xD6);

        public static void Apply(string theme)
        {
            bool light = theme == "Light" || (theme == "System" && SystemIsLight());
            var appTheme = light ? ApplicationTheme.Light : ApplicationTheme.Dark;
            ApplicationThemeManager.Apply(appTheme);
            ApplicationAccentColorManager.Apply(BrandAccent, appTheme);   // nadpisz akcent systemowy marką (kobalt)
            ReneutralizeBorders();   // WPF-UI po zmianie motywu/akcentu przemalowuje krawędź okien na akcent — zdejmij ją ponownie
            SwapPalette(light);
        }

        // Po (ponownym) zastosowaniu motywu/akcentu WPF-UI potrafi przemalować krawędź otwartych okien na
        // akcent. Zdejmujemy ją ponownie (deferred — po zmianach WPF-UI). Przy starcie Apply leci PRZED
        // StartupUri, więc lista okien jest pusta (no-op); istotne przy zmianie motywu w locie.
        private static void ReneutralizeBorders()
        {
            if (Application.Current == null) return;
            foreach (Window w in Application.Current.Windows)
            {
                var win = w;
                win.Dispatcher.BeginInvoke(new Action(() => WindowBorder.Neutralize(win)),
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
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
