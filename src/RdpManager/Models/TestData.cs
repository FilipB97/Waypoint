using System.Collections.Generic;

namespace RdpManager.Models
{
    /// <summary>Dane testowe do czasu wdrożenia repozytorium (Faza 2 — SQLite).</summary>
    public static class TestData
    {
        public static List<ServerGroup> Groups()
        {
            return new List<ServerGroup>
            {
                new ServerGroup
                {
                    Name = "Produkcja",
                    Servers = new List<ServerInfo>
                    {
                        // Prawdziwy host z Fazy 1 — do testu połączenia.
                        new ServerInfo { Name = "host-test", Host = "10.10.15.17", Initials = "HT", Group = "Produkcja", Status = ServerStatus.Online },
                        new ServerInfo { Name = "prod-web1", Host = "10.20.1.11", Initials = "PW", Group = "Produkcja", Status = ServerStatus.Online },
                        new ServerInfo { Name = "prod-db1",  Host = "10.20.1.20", Initials = "PD", Group = "Produkcja", Status = ServerStatus.Online },
                    }
                },
                new ServerGroup
                {
                    Name = "Staging",
                    Servers = new List<ServerInfo>
                    {
                        new ServerInfo { Name = "staging-app01", Host = "10.30.4.5", Initials = "SA", Group = "Staging", Status = ServerStatus.Idle },
                    }
                },
                new ServerGroup
                {
                    Name = "Klienci",
                    Servers = new List<ServerInfo>
                    {
                        new ServerInfo { Name = "client-ts01",  Host = "rdp.client01.net", Initials = "C1", Group = "Klienci", Status = ServerStatus.Offline },
                        new ServerInfo { Name = "client-app02", Host = "rdp.client02.net", Initials = "C2", Group = "Klienci", Status = ServerStatus.Offline },
                    }
                },
            };
        }
    }
}
