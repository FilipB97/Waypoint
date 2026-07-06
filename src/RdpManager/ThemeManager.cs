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
