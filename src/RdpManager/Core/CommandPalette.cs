using System;

namespace RdpManager.Core
{
    /// <summary>
    /// Ranking dopasowania dla palety poleceń (Ctrl+P). Czysty i testowalny — UI używa <see cref="Score"/>
    /// do SORTOWANIA trafień; o tym, co w ogóle pasuje (gating), decyduje RdpUtils.MatchesFilter.
    /// </summary>
    public static class CommandPalette
    {
        /// <summary>
        /// Wynik dopasowania query do haystack: im wyżej, tym lepiej; -1 = brak dopasowania.
        /// Kolejność jakości: dokładne &gt; prefiks &gt; granica słowa &gt; zwykły podciąg. W obrębie tej samej
        /// klasy remis rozstrzyga wcześniejsza pozycja trafienia i krótszy haystack. Bez rozróżniania wielkości liter.
        /// Puste query = 0 (neutralnie pasuje do wszystkiego — kolejność wejściowa zostaje zachowana).
        /// </summary>
        public static int Score(string haystack, string query)
        {
            haystack = haystack ?? "";
            query = (query ?? "").Trim();
            if (query.Length == 0) return 0;

            string h = haystack.ToLowerInvariant();
            string q = query.ToLowerInvariant();

            if (h == q) return 1000;                                    // dokładne
            int idx = h.IndexOf(q, StringComparison.Ordinal);
            if (idx < 0) return -1;                                     // brak podciągu = brak dopasowania

            int baseScore = idx == 0 ? 800                             // prefiks
                          : IsWordBoundary(h, idx) ? 600               // start po separatorze (spacja/-/_/.//:)
                          : 400;                                       // zwykły podciąg

            return baseScore - idx - Math.Min(h.Length, 200);          // wcześniej i krócej = lepiej (remisy)
        }

        private static bool IsWordBoundary(string h, int idx)
        {
            if (idx <= 0) return true;
            char c = h[idx - 1];
            return c == ' ' || c == '-' || c == '_' || c == '.' || c == ':' || c == '/' || c == '\\';
        }
    }
}
