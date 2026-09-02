using System;
using System.Collections.Generic;
using System.Text;
using RdpManager.Models;

namespace RdpManager.Core
{
    /// <summary>
    /// Podstawianie zmiennych serwera w treści snippetu: „ssh {user}@{host}" na karcie serwera app-01
    /// wychodzi jako „ssh root@10.0.0.5".
    ///
    /// Podstawienie jest CELOWO zachowawcze, bo tekst idzie do powłoki, gdzie klamry mają własne znaczenie:
    ///   awk '{print $1}'    — „print $1" nie jest znaną nazwą, więc zostaje nietknięte,
    ///   ${host}             — poprzedzone „$" to zmienna POWŁOKI, nie snippetu; nie ruszamy,
    ///   {{host}}            — jawne wyłączenie podstawienia, daje dosłowne „{host}".
    /// Nieznana nazwa zostaje dosłownie, a nie jest kasowana: ciche wycięcie fragmentu komendy jest gorsze
    /// od komendy, która widocznie nie zadziałała.
    ///
    /// Świadomie NIE MA zmiennej z hasłem. Hasło wpisane w wiersz poleceń ląduje w historii powłoki i na
    /// ekranie, a snippety są zapisane jawnie w pliku — to nie jest miejsce na sekrety.
    /// </summary>
    public static class SnippetVars
    {
        /// <summary>Nazwy zmiennych do podpowiedzi w interfejsie — w kolejności przydatności.</summary>
        public static readonly string[] Names = { "host", "port", "user", "name", "group", "domain", "protocol" };

        public static string Expand(string command, ServerInfo server)
            => Expand(command, Values(server));

        /// <summary>Wariant testowalny bez modelu serwera. Klucze bez rozróżniania wielkości liter.</summary>
        public static string Expand(string command, IDictionary<string, string> values)
        {
            if (string.IsNullOrEmpty(command)) return command ?? "";
            var sb = new StringBuilder(command.Length + 32);

            for (int i = 0; i < command.Length; i++)
            {
                char c = command[i];

                // Podwojone klamry = jedna dosłowna (wyłącznik podstawienia).
                if (c == '{' && i + 1 < command.Length && command[i + 1] == '{') { sb.Append('{'); i++; continue; }
                if (c == '}' && i + 1 < command.Length && command[i + 1] == '}') { sb.Append('}'); i++; continue; }

                if (c != '{') { sb.Append(c); continue; }

                int end = command.IndexOf('}', i + 1);
                if (end < 0) { sb.Append(c); continue; }

                string token = command.Substring(i + 1, end - i - 1);
                bool shellVar = i > 0 && command[i - 1] == '$';   // ${host} należy do powłoki

                if (!shellVar && IsName(token) && values != null && TryGet(values, token, out var value))
                {
                    sb.Append(value ?? "");
                    i = end;
                }
                else sb.Append(c);   // nieznane / zmienna powłoki — zostaw dosłownie i idź dalej znak po znaku
            }
            return sb.ToString();
        }

        /// <summary>Wartości zmiennych dla serwera. Puste pole daje pusty ciąg (nie „null").</summary>
        public static Dictionary<string, string> Values(ServerInfo s) => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["host"] = s?.Host ?? "",
            ["port"] = s == null ? "" : s.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["user"] = s?.Username ?? "",
            ["name"] = s?.Name ?? "",
            ["group"] = s?.Group ?? "",
            ["domain"] = s?.Domain ?? "",
            ["protocol"] = s == null ? "" : s.Protocol.ToString().ToLowerInvariant()
        };

        private static bool TryGet(IDictionary<string, string> values, string key, out string value)
        {
            if (values.TryGetValue(key, out value)) return true;
            foreach (var kv in values)   // słownik wołającego może być bez OrdinalIgnoreCase
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) { value = kv.Value; return true; }
            value = null;
            return false;
        }

        // Nazwa zmiennej: litery/cyfry/podkreślenie. Wszystko inne (spacje, $, cudzysłowy) to nie zmienna,
        // tylko zwykły tekst komendy — i takim ma zostać.
        private static bool IsName(string token)
        {
            if (token.Length == 0) return false;
            foreach (var ch in token)
                if (!char.IsLetterOrDigit(ch) && ch != '_') return false;
            return true;
        }

        /// <summary>
        /// Zamienia treść snippetu na dokładnie ten strumień znaków, który poszedłby z klawiatury.
        /// Enter w terminalu to CR (tak zgłasza go xterm w onData), a nie LF — dlatego łamania wierszy
        /// z pola tekstowego (CRLF/LF) idą jako CR. Dzięki temu snippet zachowuje się identycznie jak
        /// wpisanie tej samej komendy ręcznie, na każdym z trzech transportów (SSH, Telnet, port szeregowy).
        /// </summary>
        public static string ToKeystrokes(string expanded, bool sendEnter)
        {
            string s = (expanded ?? "").Replace("\r\n", "\r").Replace("\n", "\r");
            if (sendEnter && !s.EndsWith("\r", StringComparison.Ordinal)) s += "\r";
            return s;
        }
    }
}
