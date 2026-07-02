using System;
using System.Collections.Generic;

namespace RdpManager.Core
{
    /// <summary>
    /// Reguły tuneli SSH w składni jak <c>ssh -L</c>: „portLokalny:hostZdalny:portZdalny"
    /// (np. „5433:db.internal:5432" = 127.0.0.1:5433 → db.internal:5432 przez serwer SSH).
    /// Jedna reguła na linię; puste linie i linie zaczynające się od '#' są pomijane.
    /// </summary>
    public static class TunnelSpec
    {
        public static bool TryParse(string line, out int localPort, out string host, out int remotePort)
        {
            localPort = remotePort = 0;
            host = "";
            string s = (line ?? "").Trim();
            if (s.Length == 0) return false;

            var parts = s.Split(':');
            if (parts.Length != 3) return false;   // IPv6 w tej prostej składni nieobsługiwane (jak w ssh -L bez nawiasów)
            if (!int.TryParse(parts[0].Trim(), out localPort) || localPort < 1 || localPort > 65535) return false;
            host = parts[1].Trim();
            if (host.Length == 0) return false;
            if (!int.TryParse(parts[2].Trim(), out remotePort) || remotePort < 1 || remotePort > 65535) return false;
            return true;
        }

        /// <summary>
        /// Parsuje tekst z edytora (linia = reguła). Zwraca poprawne reguły; pierwsza błędna
        /// linia trafia do <paramref name="badLine"/> (null = wszystko poprawne).
        /// </summary>
        public static List<string> ParseAll(string text, out string badLine)
        {
            badLine = null;
            var result = new List<string>();
            foreach (var raw in (text ?? "").Split('\n'))
            {
                string line = raw.Trim().TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith("#")) continue;
                if (!TryParse(line, out _, out _, out _)) { if (badLine == null) badLine = line; continue; }
                result.Add(line);
            }
            return result;
        }
    }
}
