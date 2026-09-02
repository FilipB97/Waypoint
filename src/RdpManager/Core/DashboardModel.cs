using System;
using System.Collections.Generic;
using System.Linq;
using RdpManager.Models;

namespace RdpManager.Core
{
    /// <summary>
    /// Dane pulpitu w postaci gotowej do wysłania do widoku. Czysta struktura, bez WPF — pulpit rysuje
    /// się teraz w WebView2, więc granica między „co pokazać" a „jak narysować" musi być jawna i dająca
    /// się sprawdzić testem.
    /// </summary>
    public sealed class DashboardModel
    {
        public int Servers { get; set; }
        public int Online { get; set; }
        public int Idle { get; set; }
        public int Offline { get; set; }
        public int OpenSessions { get; set; }
        public int Groups { get; set; }

        /// <summary>Średnie opóźnienie osiągalnych hostów; -1 gdy nie ma z czego liczyć.</summary>
        public int AvgLatency { get; set; } = -1;

        /// <summary>Próbki średniego opóźnienia (najstarsza pierwsza) — wykres 24 h.</summary>
        public List<int> LatencySeries { get; set; } = new List<int>();

        /// <summary>Połączenia wg dnia tygodnia, indeks 0 = poniedziałek.</summary>
        public List<int> Weekday { get; set; } = new List<int>();

        /// <summary>Liczba serwerów wg protokołu, malejąco.</summary>
        public List<ProtocolCount> Protocols { get; set; } = new List<ProtocolCount>();

        public sealed class ProtocolCount
        {
            public string Name { get; set; }
            /// <summary>Klucz palety z barwą protokołu (ProtoRdp, ProtoSsh…) — identyfikacja, nie skala.</summary>
            public string ColorKey { get; set; }
            public int Count { get; set; }
        }

        /// <summary>
        /// Buduje model z listy serwerów i statystyk połączeń.
        ///
        /// Wpisy REST są POMIJANE w liczbach serwerowych: kolekcje nie są sondowane, więc zawsze
        /// wyglądałyby na niedostępne i zawyżały segment „offline". Mają własny moduł, nie miejsce
        /// na liście serwerów.
        /// </summary>
        public static DashboardModel Build(
            IEnumerable<ServerInfo> allServers,
            IEnumerable<int> latencySamples,
            IReadOnlyList<int> weekday,
            int openSessions,
            string defaultGroupName)
        {
            var srvs = (allServers ?? Enumerable.Empty<ServerInfo>())
                .Where(s => s.Protocol != RemoteProtocol.Rest)
                .ToList();

            var lats = srvs.Where(s => s.LatencyMs >= 0).Select(s => s.LatencyMs).ToList();

            var byProto = srvs
                .GroupBy(s => s.Protocol)
                .Select(g => new ProtocolCount
                {
                    Name = ProtocolName(g.Key),
                    ColorKey = ProtocolColorKey(g.Key),
                    Count = g.Count()
                })
                .OrderByDescending(p => p.Count)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new DashboardModel
            {
                Servers = srvs.Count,
                Online = srvs.Count(s => s.Status == ServerStatus.Online),
                Idle = srvs.Count(s => s.Status == ServerStatus.Idle),
                Offline = srvs.Count(s => s.Status == ServerStatus.Offline),
                OpenSessions = openSessions,
                Groups = srvs
                    .Select(s => string.IsNullOrWhiteSpace(s.Group) ? defaultGroupName : s.Group)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                AvgLatency = lats.Count > 0 ? (int)Math.Round(lats.Average()) : -1,
                LatencySeries = (latencySamples ?? Enumerable.Empty<int>()).ToList(),
                Weekday = (weekday ?? Array.Empty<int>()).ToList(),
                Protocols = byProto
            };
        }

        private static string ProtocolName(RemoteProtocol p)
        {
            switch (p)
            {
                case RemoteProtocol.Rdp: return "RDP";
                case RemoteProtocol.Ssh: return "SSH";
                case RemoteProtocol.Vnc: return "VNC";
                case RemoteProtocol.Sftp: return "SFTP";
                case RemoteProtocol.Ftp: return "FTP";
                case RemoteProtocol.Telnet: return "Telnet";
                case RemoteProtocol.Serial: return "Serial";
                case RemoteProtocol.Http: return "HTTP";
                default: return p.ToString().ToUpperInvariant();
            }
        }

        // Barwa protokołu jest ETYKIETĄ TOŻSAMOŚCI (ta sama co glif w liście i kafelek karty), a nie skalą
        // wielkości — dlatego niesie ją mała kropka przy nazwie, nie długość słupka. Patrz komentarz
        // w DashboardHtml: paleta protokołów oblała walidację jako paleta kategoryczna wykresu.
        private static string ProtocolColorKey(RemoteProtocol p)
        {
            // Odwzorowanie 1:1 z MainWindow.ProtocolBrush — ten sam protokół nie może mieć na pulpicie
            // innej barwy niż na liście serwerów i na karcie sesji.
            switch (p)
            {
                case RemoteProtocol.Rdp:
                case RemoteProtocol.Vnc: return "ProtoRdp";
                case RemoteProtocol.Ssh: return "ProtoSsh";
                case RemoteProtocol.Sftp:
                case RemoteProtocol.Ftp: return "ProtoSftp";
                case RemoteProtocol.Rest: return "ProtoRest";
                case RemoteProtocol.Http: return "ProtoWeb";
                default: return "ProtoTelnet";
            }
        }
    }
}
