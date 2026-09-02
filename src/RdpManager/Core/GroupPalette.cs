namespace RdpManager.Core
{
    /// <summary>
    /// Klucze palety, z których biorą się kolory GRUP — kart (TabStripController) i, docelowo,
    /// serwerów. Trzymane osobno, a nie w kontrolerze, z dwóch powodów: kontroler jest wewnętrzny
    /// (test nie może go dosięgnąć), a lista jest kontraktem z paletą, nie szczegółem paska kart.
    ///
    /// Kolejność to kolejność przydzielania kolorów nowym grupom. Każdy z tych kolorów musi mieć
    /// wobec akcentu odległość barwną ΔE &gt; 15 w OBU motywach — inaczej znacznik grupy zlewa się
    /// z akcentem, co dokładnie zdarzyło się poprzedniej palecie (#7C6CFB, ΔE 4,1). Pilnuje tego
    /// GroupPaletteTests, czytając wartości wprost z plików palety.
    /// </summary>
    public static class GroupPalette
    {
        public static readonly string[] Keys = { "GdProd", "GdClient", "GdStaging", "GdBlue", "GdRose", "GdGreen" };

        /// <summary>Poniżej tej odległości barwnej dwa kolory czytają się jako odcienie tego samego.</summary>
        public const double MinDeltaE = 15.0;
    }
}
