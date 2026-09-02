using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using RdpManager.Core;
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
        // Ostatnia deska ratunku, gdyby w zasobach zabrakło klucza „Accent" (paleta i preset go dostarczają,
        // więc w praktyce nie odpala). Wartości muszą być te, co w Palette.* — stały tu kobalt sprzed
        // rebrandingu, czyli kolor, którego aplikacja już nigdzie nie używa.
        private static readonly Color AccentDark = Color.FromRgb(0x6C, 0x6D, 0xFF);
        private static readonly Color AccentLight = Color.FromRgb(0x5B, 0x4B, 0xD6);

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
            PinAccentFills(res, accent, light);                       // ...i sprowadzenie go do JEDNEGO odcienia
            if (custom != null)
            {
                res["Accent"] = new SolidColorBrush(accent);
                res["AccentSoft"] = new SolidColorBrush(Color.FromArgb(0x1F, accent.R, accent.G, accent.B));
                res["AccentStrong"] = new SolidColorBrush(Color.FromArgb(0x66, accent.R, accent.G, accent.B));
                res["AccentBright"] = new SolidColorBrush(Lighten(accent, 0.30));
            }

            // Gradient akcji głównej („Szybkie połączenie"). Stał tu na sztywno gradient ZE ZNAKU MARKI
            // (niebieski -> fioletowy), więc po wybraniu presetu z pomarańczowym akcentem przycisk zostawał
            // niebieski — jedyny element w oknie, który nie słuchał wyboru użytkownika. Teraz gradient
            // powstaje Z AKCENTU: ten sam kolor co reszta, tylko z lekkim rozjaśnieniem, żeby przycisk
            // zachował głębię. Logo zostaje przy swoich barwach — znak marki nie zmienia się z motywem.
            res["AccentGradient"] = new LinearGradientBrush(accent, Lighten(accent, 0.22), 45);
            WindowBorder.ReapplyAll();   // WPF-UI po zmianie motywu/akcentu przemalowuje krawędź — przywróć wybraną obwódkę
        }

        /// <summary>
        /// Sprowadza rodzinę akcentu WPF-UI do JEDNEGO odcienia — naszego.
        ///
        /// ApplicationAccentColorManager generuje własne warianty: w motywie ciemnym ROZJAŚNIA akcent
        /// (primary/secondary/tertiary z korektą jasności), w jasnym go przyciemnia. To konwencja Fluenta,
        /// ale u nas dawała dwie rodziny akcentu obok siebie: pigułka raila i obwódka fokusu w kolorze
        /// „Accent", a przyciski Primary, przełączniki i pola wyboru w wariancie jaśniejszym. Wygląda to
        /// po prostu jak dwa różne kolory w jednym oknie.
        ///
        /// Klucze piszemy WPROST do Application.Resources, nie przez paletę: ApplicationAccentColorManager
        /// robi to samo, a wpisy własne słownika mają pierwszeństwo przed jego MergedDictionaries — więc
        /// nadpisanie w Palette.* i tak by nie zadziałało.
        ///
        /// Odcienie na hover/wciśnięcie ZOSTAJĄ zróżnicowane (inaczej kontrolki straciłyby reakcję na
        /// dotknięcie), ale stan spoczynkowy jest dokładnie akcentem — a to on rzuca się w oczy.
        /// </summary>
        private static void PinAccentFills(ResourceDictionary res, Color accent, bool light)
        {
            // Hover/wciśnięcie: w ciemnym motywie w stronę bieli, w jasnym w stronę czerni — czyli
            // „dalej od tła", tak jak reakcja na dotknięcie w pozostałych kontrolkach aplikacji.
            Color step1 = light ? Darken(accent, 0.10) : Lighten(accent, 0.10);
            Color step2 = light ? Darken(accent, 0.20) : Lighten(accent, 0.20);

            res["AccentFillColorDefault"] = accent;
            res["AccentFillColorSecondary"] = step1;
            res["AccentFillColorTertiary"] = step2;
            res["AccentFillColorDefaultBrush"] = new SolidColorBrush(accent);
            res["AccentFillColorSecondaryBrush"] = new SolidColorBrush(step1);
            res["AccentFillColorTertiaryBrush"] = new SolidColorBrush(step2);
            res["SystemAccentBrush"] = new SolidColorBrush(accent);

            // SystemAccentColorPrimary to klucz, przez który idzie NAJWIĘCEJ powierzchni akcentowych
            // Fluenta — a pominąłem go za pierwszym razem, przez co przełączniki i pola wyboru dalej
            // świeciły wariantem rozjaśnionym. W motywie Dark.xaml WPF-UI wiszą na nim m.in.:
            // ToggleSwitchFillOn, CheckBoxCheckBackgroundFillChecked, AccentButtonBackground,
            // ProgressBarForeground, ProgressRingForegroundThemeBrush, SliderThumbBackground,
            // RadioButtonOuterEllipseCheckedStroke, TextControlFocusedBorderBrush oraz wskaźniki
            // zaznaczenia list i drzew. Secondary/Tertiary trzymają hover i wciśnięcie tych samych
            // kontrolek, więc zostają zróżnicowane — inaczej zniknęłaby reakcja na dotknięcie.
            res["SystemAccentColor"] = accent;
            res["SystemAccentColorPrimary"] = accent;
            res["SystemAccentColorSecondary"] = step1;
            res["SystemAccentColorTertiary"] = step2;

            // Tekst NA akcencie nie może być na sztywno biały: przy jasnym akcencie (pomarańcz presetu
            // „Claude", bursztyn z próbnika, błękit Norda) biel ma na nim kontrast 2.0-3.1. Reguła jest
            // wspólna z inicjałami na awatarach — patrz ColorMath.PrefersDarkInk.
            Color ink = ColorMath.PrefersDarkInk(accent) ? ColorMath.InkDark : Colors.White;
            res["TextOnAccentFillColorPrimary"] = ink;
            res["TextOnAccentFillColorSecondary"] = Color.FromArgb(0xC8, ink.R, ink.G, ink.B);
            res["TextOnAccentFillColorSelectedText"] = ink;
        }

        private static Color Darken(Color c, double f) => Color.FromRgb(
            (byte)(c.R * (1 - f)), (byte)(c.G * (1 - f)), (byte)(c.B * (1 - f)));

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

            // Próg czytelności NA PANELU, bo tam leży większość tekstu (karty, wiersze ustawień, lista),
            // a panel jest bliżej koloru tekstu niż kanwa — czyli to gorszy przypadek. Presety niosą
            // własne kolory z kanonicznych palet edytorów, gdzie trzeci stopień to kolor KOMENTARZA:
            // celowo przygaszony. U nas ten sam klucz trzyma etykiety pól i komunikaty pustych stanów.
            // We wszystkich dwunastu presetach TextTer wypadał między 2.07 a 3.77 przy progu 4.5.
            // Barwa presetu zostaje, dociągana jest tylko jasność — i tylko o tyle, o ile trzeba.
            Color prim = ColorMath.EnsureContrast(p.TextPrim, p.Panel, 4.5);
            Color sec = ColorMath.EnsureContrast(p.TextSec, p.Panel, 4.5);
            Color ter = ColorMath.EnsureContrast(p.TextTer, p.Panel, 4.5);
            B("TextPrim", prim); B("TextSec", sec); B("TextTer", ter);
            B("Accent", p.Accent);
            d["AccentSoft"] = new SolidColorBrush(Color.FromArgb(0x1F, p.Accent.R, p.Accent.G, p.Accent.B));
            d["AccentStrong"] = new SolidColorBrush(Color.FromArgb(0x66, p.Accent.R, p.Accent.G, p.Accent.B));
            d["AccentBright"] = new SolidColorBrush(Lighten(p.Accent, 0.25));

            // Te same kolory pod kontrolki WPF-UI (patrz blok „Kontrolki WPF-UI" w Palette.*). Bez tego
            // preset przestawiał tło, panele i tekst CAŁEJ aplikacji poza formularzami — pola i przełączniki
            // zostawały na kolorach palety bazowej, czyli w innym tonie niż wszystko dokoła.
            B("TextFillColorPrimaryBrush", prim);
            B("TextFillColorSecondaryBrush", sec);
            B("TextFillColorTertiaryBrush", ter);
            B("TextControlForeground", prim);
            B("TextControlPlaceholderForeground", ter);
            B("ControlFillColorDefaultBrush", p.Panel);
            B("TextControlBackground", p.Panel);
            B("TextControlBackgroundFocused", p.Panel);
            B("ControlStrokeColorDefaultBrush", p.Border);
            B("CardBackgroundFillColorDefaultBrush", p.Panel);
            B("SolidBackgroundFillColorBaseBrush", p.Canvas);
            B("SolidBackgroundFillColorSecondaryBrush", p.Panel);

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
