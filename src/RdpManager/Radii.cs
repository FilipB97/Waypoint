using System.Windows;

namespace RdpManager
{
    /// <summary>
    /// Promienie zaokrągleń z Themes/Metrics.xaml, dostępne dla interfejsu budowanego w kodzie
    /// (lista serwerów, pasek zakładek, kafelki pulpitu). Bez tego połowa aplikacji trzymała drabinę
    /// „na słowo honoru": w XAML-u szła przez StaticResource, a w C# przez wpisane wprost liczby —
    /// stąd wzięło się dziesięć różnych wartości przy czterech tokenach.
    ///
    /// Wartość jest czytana z zasobów RAZ i zapamiętywana, bo wiersze listy powstają setkami i
    /// TryFindResource przy każdym z nich to niepotrzebny spacer po drzewie słowników. Fallback ma
    /// znaczenie tylko w testach i projektancie, gdzie Application.Current bywa puste.
    /// </summary>
    internal static class Radii
    {
        private static CornerRadius? _xxs, _xs, _sm, _md, _lg, _pill;

        private static CornerRadius Get(ref CornerRadius? cache, string key, double fallback)
        {
            if (cache == null)
                cache = Application.Current?.TryFindResource(key) is CornerRadius r
                    ? r
                    : new CornerRadius(fallback);
            return cache.Value;
        }

        internal static CornerRadius Xxs => Get(ref _xxs, "RadiusXxs", 2);
        internal static CornerRadius Xs  => Get(ref _xs,  "RadiusXs",  4);
        internal static CornerRadius Sm  => Get(ref _sm,  "RadiusSm",  8);
        internal static CornerRadius Md  => Get(ref _md,  "RadiusMd", 12);
        internal static CornerRadius Lg  => Get(ref _lg,  "RadiusLg", 16);

        /// <summary>Pełne zaokrąglenie (chipy filtrów). Border i tak przycina promień do połowy
        /// krótszego boku, więc jedna wartość działa niezależnie od wysokości elementu.</summary>
        internal static CornerRadius Pill => Get(ref _pill, "RadiusPill", 999);
    }
}
