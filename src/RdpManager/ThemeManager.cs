using System;
using System.Windows;
using System.Windows.Media;
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
        // jasny #2657D6 (kobalt — lepszy kontrast białego tekstu na akcencie). To domyślna para Compass;
        // użytkownik może ją nadpisać własnym akcentem (§4.7).
        private static readonly Color AccentDark = Color.FromRgb(0x4C, 0x86, 0xFF);
        private static readonly Color AccentLight = Color.FromRgb(0x26, 0x57, 0xD6);

        // Rodzina kluczy akcentu nadpisywana bezpośrednio w zasobach App, gdy wybrano własny kolor (§4.7).
        private static readonly string[] AccentKeys = { "Accent", "AccentSoft", "AccentStrong", "AccentBright" };

        /// <summary>Czy aktualnie obowiązuje jasny motyw — ostatni wynik <see cref="Apply"/>. Czytane m.in.
        /// przez XtermControl, który (WebView2/xterm.js) nie żyje w drzewie zasobów WPF i nie może
        /// same reagować na DynamicResource (D5 z przeglądu).</summary>
        public static bool IsLight { get; private set; }

        /// <param name="accentHex">Własny akcent użytkownika (np. „#7C6CFB"); pusty/niepoprawny = domyślny Compass.</param>
        public static void Apply(string theme, string accentHex = null)
        {
            bool light = theme == "Light" || (theme == "System" && SystemIsLight());
            IsLight = light;
            var appTheme = light ? ApplicationTheme.Light : ApplicationTheme.Dark;
            ApplicationThemeManager.Apply(appTheme);

            Color accent = ParseAccent(accentHex) ?? (light ? AccentLight : AccentDark);
            ApplicationAccentColorManager.Apply(accent, appTheme);   // akcent kontrolek WPF-UI
            WindowBorder.ReapplyAll();   // WPF-UI po zmianie motywu/akcentu przemalowuje krawędź — przywróć wybraną obwódkę
            SwapPalette(light);
            ApplyAccentOverride(accentHex, accent);   // po SwapPalette — nadpisz klucze palety, jeśli akcent własny
        }

        private static Color? ParseAccent(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return null;
            try { return (Color)ColorConverter.ConvertFromString(hex.Trim()); }
            catch { return null; }
        }

        // Własny akcent: nadpisz rodzinę kluczy Accent* bezpośrednio w zasobach App (biją paletę z MergedDictionaries).
        // Domyślny: usuń nadpisania — wracają wartości z palety Compass. Soft/Strong = ten sam kolor z alfą, Bright = rozjaśniony.
        private static void ApplyAccentOverride(string hex, Color accent)
        {
            var res = Application.Current.Resources;
            foreach (var k in AccentKeys) res.Remove(k);
            if (string.IsNullOrWhiteSpace(hex)) return;
            res["Accent"] = new SolidColorBrush(accent);
            res["AccentSoft"] = new SolidColorBrush(Color.FromArgb(0x1F, accent.R, accent.G, accent.B));
            res["AccentStrong"] = new SolidColorBrush(Color.FromArgb(0x66, accent.R, accent.G, accent.B));
            res["AccentBright"] = new SolidColorBrush(Lighten(accent, 0.30));
        }

        private static Color Lighten(Color c, double f) => Color.FromRgb(
            (byte)(c.R + (255 - c.R) * f), (byte)(c.G + (255 - c.G) * f), (byte)(c.B + (255 - c.B) * f));

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
