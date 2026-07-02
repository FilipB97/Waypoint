using System;
using System.Windows;

namespace RdpManager
{
    /// <summary>
    /// Przełącza język interfejsu („pl" / „en"), podmieniając słownik tekstów
    /// (Themes/Strings.*.xaml) w zasobach aplikacji. XAML używa DynamicResource,
    /// więc etykiety odświeżają się w locie (bez restartu). Analogicznie do ThemeManager.
    /// </summary>
    public static class LocalizationManager
    {
        public static void Apply(string lang)
        {
            string l = (lang == "en") ? "en" : "pl";
            var dicts = Application.Current.Resources.MergedDictionaries;
            for (int i = dicts.Count - 1; i >= 0; i--)
            {
                var src = dicts[i].Source?.OriginalString ?? "";
                if (src.IndexOf("Strings.", StringComparison.OrdinalIgnoreCase) >= 0)
                    dicts.RemoveAt(i);
            }
            dicts.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Themes/Strings." + l + ".xaml")
            });
        }
    }
}
