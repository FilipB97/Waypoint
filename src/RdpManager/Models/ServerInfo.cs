using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RdpManager.Models
{
    /// <summary>
    /// Definicja serwera. Uwaga: hasło NIE jest tu przechowywane — na tym etapie
    /// wpisuje się je w pasku sesji. Docelowo: DPAPI / Windows Credential Manager.
    /// </summary>
    public class ServerInfo
    {
        /// <summary>Stały identyfikator — klucz poświadczeń w Windows Credential Manager.</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

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

        /// <summary>Czy hasło ma być zapisane w Windows Credential Manager.</summary>
        public bool SavePassword { get; set; }

        // Przekierowania zasobów lokalnych do sesji.
        public bool RedirectClipboard { get; set; } = true;
        public bool RedirectDrives { get; set; }
        public bool RedirectPrinters { get; set; }

        /// <summary>Dźwięk: 0 = odtwarzaj lokalnie, 1 = nie odtwarzaj, 2 = odtwarzaj na serwerze.</summary>
        public int AudioMode { get; set; }

        /// <summary>
        /// Weryfikacja tożsamości serwera (mapuje na IMsRdpClientAdvancedSettings.AuthenticationLevel):
        /// 0 = nie sprawdzaj (niebezpieczne, ryzyko MITM),
        /// 1 = wymagaj — połączenie się nie uda, jeśli weryfikacja zawiedzie,
        /// 2 = ostrzegaj — próbuj zweryfikować, ostrzeż przy niepowodzeniu (bezpieczny domyślny).
        /// </summary>
        public int AuthenticationLevel { get; set; } = 2;

        /// <summary>Klucz w Credential Manager (hasło NIE jest trzymane w tym modelu ani w JSON).</summary>
        [JsonIgnore]
        public string CredTarget => "RdpManager:" + Id;

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
