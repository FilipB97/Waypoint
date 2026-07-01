using System.Collections.Generic;

namespace RdpManager.Models
{
    /// <summary>
    /// Przykładowe wpisy pokazywane przy pierwszym uruchomieniu (pusty %APPDATA%).
    /// Wyłącznie neutralne, bezpieczne dane: adresy z RFC 5737 (TEST-NET, zarezerwowane
    /// do dokumentacji) i domeny example.com — NIGDY realne hosty. Użytkownik zastępuje
    /// je własnymi serwerami. Docelowo (Faza 2, SQLite) start może być pusty.
    /// </summary>
    public static class TestData
    {
        public static List<ServerGroup> Groups()
        {
            return new List<ServerGroup>
            {
                new ServerGroup
                {
                    Name = "Przykłady",
                    Servers = new List<ServerInfo>
                    {
                        new ServerInfo { Name = "Ten komputer", Host = "localhost",       Initials = "TK", Group = "Przykłady", Status = ServerStatus.Offline },
                        new ServerInfo { Name = "Przykład 1",    Host = "192.0.2.10",      Initials = "P1", Group = "Przykłady", Status = ServerStatus.Offline },
                        new ServerInfo { Name = "Przykład 2",    Host = "rdp.example.com", Initials = "P2", Group = "Przykłady", Status = ServerStatus.Offline },
                    }
                },
            };
        }
    }
}
