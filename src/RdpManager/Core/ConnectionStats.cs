using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RdpManager.Core
{
    /// <summary>
    /// Statystyki do dashboardu, liczone z linii dziennika połączeń (connections.log).
    /// Liczymy tylko zdarzenia CONNECTED. Format linii: „yyyy-MM-dd HH:mm:ss  EVENT  name (host:port) user=…".
    /// Czysta, testowalna logika — bez WPF.
    /// </summary>
    public sealed class ConnectionStats
    {
        /// <summary>Liczba połączeń w kolejnych dniach; indeks 0 = najstarszy, ostatni = dziś.</summary>
        public int[] PerDay { get; set; } = Array.Empty<int>();

        /// <summary>Łączna liczba zdarzeń CONNECTED w całym dzienniku.</summary>
        public int TotalConnects { get; set; }

        /// <summary>Najczęściej używane serwery (nazwa → liczba połączeń), malejąco.</summary>
        public List<KeyValuePair<string, int>> TopServers { get; set; } = new List<KeyValuePair<string, int>>();

        /// <summary>Liczba połączeń wg dnia tygodnia z CAŁEGO dziennika; indeks 0 = poniedziałek … 6 = niedziela.</summary>
        public int[] PerWeekday { get; set; } = new int[7];

        public static ConnectionStats Compute(IEnumerable<string> lines, DateTime now, int days, int topCount = 5)
        {
            var perDay = new int[Math.Max(1, days)];
            var weekday = new int[7];
            var byServer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int total = 0;
            DateTime start = now.Date.AddDays(-(perDay.Length - 1));

            foreach (var raw in lines ?? Enumerable.Empty<string>())
            {
                var line = raw ?? "";
                if (line.Length < 20) continue;
                if (!DateTime.TryParseExact(line.Substring(0, 19), "yyyy-MM-dd HH:mm:ss",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts)) continue;

                string afterTs = line.Substring(19).TrimStart();     // "CONNECTED   name (…) user=…"
                int sp = afterTs.IndexOf(' ');
                if (sp <= 0) continue;
                if (!afterTs.Substring(0, sp).Equals("CONNECTED", StringComparison.OrdinalIgnoreCase)) continue;

                total++;
                weekday[((int)ts.DayOfWeek + 6) % 7]++;               // .NET: niedziela=0 → nasze: poniedziałek=0
                string rest = afterTs.Substring(sp).TrimStart();     // "name (host:port) user=…"
                int par = rest.IndexOf(" (", StringComparison.Ordinal);
                string name = (par > 0 ? rest.Substring(0, par) : rest).Trim();
                if (name.Length > 0)
                    byServer[name] = byServer.TryGetValue(name, out var c) ? c + 1 : 1;

                int idx = (int)(ts.Date - start).TotalDays;
                if (idx >= 0 && idx < perDay.Length) perDay[idx]++;
            }

            return new ConnectionStats
            {
                PerDay = perDay,
                PerWeekday = weekday,
                TotalConnects = total,
                TopServers = byServer.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
                                     .Take(Math.Max(0, topCount)).ToList()
            };
        }
    }
}
