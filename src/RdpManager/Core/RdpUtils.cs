using System;
using System.Linq;
using RdpManager.Models;

namespace RdpManager.Core
{
    /// <summary>
    /// Czyste funkcje pomocnicze — bez zależności od WPF/ActiveX, dzięki czemu są
    /// jednostkowo testowalne. Logika przeniesiona 1:1 z code-behind (patrz komentarze).
    /// </summary>
    public static class RdpUtils
    {
        /// <summary>Dozwolony zakres wymiaru sesji RDP (piksele).</summary>
        public const int MinDim = 200;
        public const int MaxDim = 8192;

        /// <summary>Inicjały do awatara serwera. Źródło: ServerEditWindow.MakeInitials.</summary>
        public static string MakeInitials(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var parts = name.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
            string s = parts.Length >= 2
                ? "" + parts[0][0] + parts[1][0]
                : new string(name.Where(char.IsLetterOrDigit).Take(2).ToArray());
            return s.ToUpperInvariant();
        }

        /// <summary>
        /// Normalizuje wymiar sesji: zakres [200, 8192] i parzystość (nieparzyste =&gt; E_INVALIDARG
        /// w UpdateSessionDisplaySettings). Źródło: RdpDynamicResolution.NormalizeDim.
        /// </summary>
        public static int NormalizeDim(int v)
        {
            if (v < MinDim) v = MinDim;
            if (v > MaxDim) v = MaxDim;
            v &= ~1;
            return v;
        }

        /// <summary>
        /// Czy serwer pasuje do filtra (po nazwie lub hoście, bez uwzględniania wielkości liter).
        /// Źródło: MainWindow.MatchesFilter.
        /// </summary>
        public static bool MatchesFilter(ServerInfo s, string filter)
        {
            if (s == null) return false;
            filter = (filter ?? "").Trim().ToLowerInvariant();
            if (filter.Length == 0) return true;
            return (s.Name ?? "").ToLowerInvariant().Contains(filter)
                || (s.Host ?? "").ToLowerInvariant().Contains(filter);
        }

        /// <summary>
        /// Parsuje głębię kolorów z tekstu ComboBoxa do jednej z dozwolonych wartości (16/24/32);
        /// przy nieznanej wartości zwraca fallback. Źródło: MainWindow.ParseColorDepth.
        /// </summary>
        public static int ParseColorDepth(string text, int fallback = 32)
        {
            if (int.TryParse((text ?? "").Trim(), out var v) && (v == 16 || v == 24 || v == 32))
                return v;
            return fallback;
        }

        /// <summary>
        /// Formatuje opis rozłączenia z kodami. Źródło: MainWindow.DescribeDisconnect
        /// (część czysto tekstowa; pobranie opisu z kontrolki RDP zostaje w UI).
        /// </summary>
        public static string FormatDisconnectReason(string description, int reason, long ext)
        {
            string d = string.IsNullOrWhiteSpace(description) ? "rozłączono" : description.Trim().TrimEnd('.');
            return d + " (kod " + reason + "/" + ext + ")";
        }
    }
}
