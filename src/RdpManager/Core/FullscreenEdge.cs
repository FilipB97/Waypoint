namespace RdpManager.Core
{
    /// <summary>
    /// Wykrycie górnej krawędzi monitora pełnego ekranu (wysunięcie paska narzędzi). Piksele fizyczne;
    /// współrzędne mogą być ujemne (monitor na lewo od głównego albo nad nim).
    /// </summary>
    public static class FullscreenEdge
    {
        /// <summary>Strefa przy samej krawędzi (px).</summary>
        public const int TopZone = 2;

        /// <summary>Kursor w obrębie monitora (poziomo i pionowo) i przy jego górnej krawędzi. Pionowe
        /// ograniczenie od góry wyklucza monitor powyżej — jego dolny brzeg ma y &lt; top.</summary>
        public static bool IsAtTop(int x, int y, int left, int top, int right, int bottom)
            => x >= left && x < right && y >= top && y < bottom && y <= top + TopZone;
    }
}
