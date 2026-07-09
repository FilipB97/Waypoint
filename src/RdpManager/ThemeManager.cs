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

        // Nakładka wybranego presetu motywu (§4.9) w MergedDictionaries — trzymana po referencji, bo generowana
        // w kodzie (brak Source, więc SwapPalette jej nie usuwa po URI).
        private static ResourceDictionary _presetOverlay;

        /// <param name="accentHex">Własny akcent użytkownika (np. „#7C6CFB"); pusty/niepoprawny = domyślny presetu/palety.</param>
        /// <param name="variantDark">Preset ciemny (Id z <see cref="ThemePresets"/>); „Waypoint"/pusty = baza.</param>
        /// <param name="variantLight">Preset jasny; „Waypoint"/pusty = baza.</param>
        public static void Apply(string theme, string accentHex = null, string variantDark = null, string variantLight = null)
        {
            bool light = theme == "Light" || (theme == "System" && SystemIsLight());
            IsLight = light;
            var appTheme = light ? ApplicationTheme.Light : ApplicationTheme.Dark;
            ApplicationThemeManager.Apply(appTheme);

            SwapPalette(light);                                       // baza palety Waypoint
            ApplyPreset(light ? variantLight : variantDark, light);  // nakładka presetu (ton), jeśli wybrany

            // Akcent: własny (§4.7) > akcent presetu/palety > domyślny Compass.
            var res = Application.Current.Resources;
            foreach (var k in AccentKeys) res.Remove(k);   // zdejmij stare nadpisania, by odczyt = paleta/preset
            Color? custom = ParseAccent(accentHex);
            Color accent = custom
                ?? (res["Accent"] as SolidColorBrush)?.Color
                ?? (light ? AccentLight : AccentDark);
            ApplicationAccentColorManager.Apply(accent, appTheme);   // akcent kontrolek WPF-UI
            if (custom != null)
            {
                res["Accent"] = new SolidColorBrush(accent);
                res["AccentSoft"] = new SolidColorBrush(Color.FromArgb(0x1F, accent.R, accent.G, accent.B));
                res["AccentStrong"] = new SolidColorBrush(Color.FromArgb(0x66, accent.R, accent.G, accent.B));
                res["AccentBright"] = new SolidColorBrush(Lighten(accent, 0.30));
            }
            WindowBorder.ReapplyAll();   // WPF-UI po zmianie motywu/akcentu przemalowuje krawędź — przywróć wybraną obwódkę
        }

        private static Color? ParseAccent(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return null;
            try { return (Color)ColorConverter.ConvertFromString(hex.Trim()); }
            catch { return null; }
        }

        // Nakłada „tonowe" klucze presetu na bazę (statusy/grupy/protokoły/gradienty zostają z bazy). Domyślny
        // „Waypoint" (Find == null) = brak nakładki. Akcent presetu wraz z pochodnymi (Soft/Strong/Bright).
        private static void ApplyPreset(string id, bool light)
        {
            var dicts = Application.Current.Resources.MergedDictionaries;
            if (_presetOverlay != null) { dicts.Remove(_presetOverlay); _presetOverlay = null; }

            var p = ThemePresets.Find(id, light);
            if (p == null) return;

            var d = new ResourceDictionary();
            void B(string k, Color c) => d[k] = new SolidColorBrush(c);
            B("CanvasBrush", p.Canvas); B("Canvas", p.Canvas);
            B("Panel", p.Panel); B("Border", p.Border); B("RailBg", p.RailBg);
            B("TextPrim", p.TextPrim); B("TextSec", p.TextSec); B("TextTer", p.TextTer);
            B("Accent", p.Accent);
            d["AccentSoft"] = new SolidColorBrush(Color.FromArgb(0x1F, p.Accent.R, p.Accent.G, p.Accent.B));
            d["AccentStrong"] = new SolidColorBrush(Color.FromArgb(0x66, p.Accent.R, p.Accent.G, p.Accent.B));
            d["AccentBright"] = new SolidColorBrush(Lighten(p.Accent, 0.25));
            dicts.Add(d);
            _presetOverlay = d;
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
