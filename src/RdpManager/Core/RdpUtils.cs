using System;
using System.Globalization;
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
        /// Rozdziela adres "host" / "host:port" na host i port. Bierze port tylko gdy jest dokładnie
        /// jeden ':' i po nim poprawny numer [1..65535] (pomija adresy IPv6 z wieloma ':'). W innym
        /// przypadku zwraca cały adres jako host i <paramref name="defaultPort"/>. Używane przy imporcie z mstsc.
        /// </summary>
        public static (string Host, int Port) SplitHostPort(string address, int defaultPort)
        {
            string host = (address ?? "").Trim();
            // IPv6 w nawiasach: "[::1]" albo "[::1]:3389" — nawiasy usuwamy, port bierzemy tylko po "]".
            if (host.StartsWith("[", StringComparison.Ordinal))
            {
                int close = host.IndexOf(']');
                if (close > 1)
                {
                    string inner = host.Substring(1, close - 1);
                    string rest = host.Substring(close + 1);
                    if (rest.StartsWith(":", StringComparison.Ordinal)
                        && int.TryParse(rest.Substring(1), out var pp) && pp >= 1 && pp <= 65535)
                        return (inner, pp);
                    return (inner, defaultPort);
                }
            }
            int i = host.LastIndexOf(':');
            if (i > 0 && host.IndexOf(':') == i
                && int.TryParse(host.Substring(i + 1), out var p) && p >= 1 && p <= 65535)
                return (host.Substring(0, i), p);
            return (host, defaultPort);
        }

        /// <summary>
        /// Parsuje adres „szybkiego połączenia" na host/port/login/domenę. Obsługuje formy:
        /// host, host:port, user@host, user@host:port, DOMENA\user@host[:port].
        /// (Część host:port deleguje do <see cref="SplitHostPort"/>.)
        /// </summary>
        public static (string Host, int Port, string Username, string Domain) ParseQuickConnect(string input, int defaultPort)
        {
            string s = (input ?? "").Trim();
            string userPart = "";
            int at = s.LastIndexOf('@');
            if (at >= 0) { userPart = s.Substring(0, at); s = s.Substring(at + 1); }

            var (host, port) = SplitHostPort(s, defaultPort);

            string domain = "", user = userPart.Trim();
            int bs = user.IndexOf('\\');
            if (bs > 0) { domain = user.Substring(0, bs); user = user.Substring(bs + 1); }
            return (host, port, user, domain);
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
        /// Czy serwer pasuje do filtra (po nazwie, hoście lub tagu; bez uwzględniania wielkości liter).
        /// Składnia „#tag" — wiodący # jest ignorowany przy porównaniu. Źródło: MainWindow.MatchesFilter.
        /// </summary>
        public static bool MatchesFilter(ServerInfo s, string filter)
        {
            if (s == null) return false;
            filter = (filter ?? "").Trim().ToLowerInvariant();
            if (filter.Length == 0) return true;
            if ((s.Name ?? "").ToLowerInvariant().Contains(filter)) return true;
            if ((s.Host ?? "").ToLowerInvariant().Contains(filter)) return true;
            string needle = filter.StartsWith("#") ? filter.Substring(1) : filter;
            if (needle.Length > 0 && s.Tags != null)
                foreach (var t in s.Tags)
                    if ((t ?? "").ToLowerInvariant().Contains(needle)) return true;
            return false;
        }

        /// <summary>Czy serwer pasuje do filtra protokołu z paska chipów. <paramref name="proto"/> == null
        /// oznacza „Wszystkie" (bez filtrowania). Komponuje się z <see cref="MatchesFilter"/> (AND).</summary>
        public static bool MatchesProtocol(ServerInfo s, RemoteProtocol? proto)
            => s != null && (proto == null || s.Protocol == proto.Value);

        /// <summary>Formatuje zmierzone opóźnienie sondy TCP do etykiety wiersza. Ujemne = brak pomiaru
        /// (pusty tekst); 0 ms pokazujemy jako „&lt;1 ms". Nie dla protokołów bez sondy (Serial/WWW/REST).</summary>
        public static string FormatLatency(int ms)
            => ms < 0 ? "" : ms == 0 ? "<1 ms" : ms + " ms";

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

        /// <summary>
        /// Jedna linia dziennika połączeń (audyt). Znacznik czasu podaje wołający (testowalne).
        /// Pola pochodzące od użytkownika są oczyszczane ze znaków sterujących — nazwa serwera
        /// z „\n" nie może sfałszować kolejnych linii logu.
        /// </summary>
        public static string FormatConnectionLog(DateTime ts, string ev, ServerInfo s)
        {
            string user = s == null ? "-"
                : s.UseWindowsAccount ? "(konto Windows)"
                : string.IsNullOrEmpty(s.Username) ? "-"
                : string.IsNullOrEmpty(s.Domain) ? s.Username : s.Domain + "\\" + s.Username;
            string name = SanitizeLogField(s?.Name);
            string host = SanitizeLogField(s?.Host);
            user = SanitizeLogField(user);
            int port = s?.Port ?? 0;
            return string.Format(CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd HH:mm:ss}  {1,-12} {2} ({3}:{4}) user={5}", ts, ev, name, host, port, user);
        }

        /// <summary>Zastępuje znaki sterujące (w tym CR/LF) spacją; puste pole -&gt; "-".</summary>
        internal static string SanitizeLogField(string value)
        {
            if (string.IsNullOrEmpty(value)) return "-";
            var chars = value.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (char.IsControl(chars[i])) chars[i] = ' ';
            return new string(chars);
        }

        /// <summary>
        /// Czytelny wynik diagnostyki osiągalności portu RDP. Szablony (zlokalizowane) podaje wołający:
        /// <paramref name="openFormat"/> dostaje {0}=host, {1}=port, {2}=ms; <paramref name="closedFormat"/> {0}=host, {1}=port.
        /// </summary>
        public static string FormatDiagnostics(string host, int port, bool reachable, long elapsedMs,
                                               string openFormat, string closedFormat)
        {
            return reachable
                ? string.Format(CultureInfo.InvariantCulture, openFormat, host, port, elapsedMs)
                : string.Format(CultureInfo.InvariantCulture, closedFormat, host, port);
        }
    }
}
