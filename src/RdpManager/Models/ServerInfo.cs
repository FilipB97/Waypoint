using System.Collections.Generic;

namespace RdpManager.Models
{
    /// <summary>
    /// Definicja serwera. Uwaga: hasło NIE jest tu przechowywane — na tym etapie
    /// wpisuje się je w pasku sesji. Docelowo: DPAPI / Windows Credential Manager.
    /// </summary>
    public class ServerInfo
    {
        public string Name { get; set; }
        public string Host { get; set; }
        public int Port { get; set; } = 3389;
        public string Username { get; set; } = "";
        public string Domain { get; set; } = "";

        /// <summary>Logowanie zintegrowane kontem Windows (bez podawania loginu/hasła).</summary>
        public bool UseWindowsAccount { get; set; }

        public string Group { get; set; }

        /// <summary>Inicjały do "awatara" na liście (jak w mockupie).</summary>
        public string Initials { get; set; }

        /// <summary>
        /// Status prezentacyjny (kropka online/idle/offline). Na razie dane testowe —
        /// docelowo z monitoringu osiągalności w tle (TCP 3389 / ping).
        /// </summary>
        public ServerStatus Status { get; set; } = ServerStatus.Offline;
    }

    public enum ServerStatus
    {
        Online,
        Idle,
        Offline
    }

    /// <summary>Grupa serwerów w drzewie (Produkcja / Staging / Klienci …).</summary>
    public class ServerGroup
    {
        public string Name { get; set; }
        public List<ServerInfo> Servers { get; set; } = new List<ServerInfo>();
    }
}
