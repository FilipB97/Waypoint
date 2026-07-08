using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RdpManager.Models
{
    /// <summary>
    /// Definicja serwera. Uwaga: hasło NIE jest tu przechowywane — na tym etapie
    /// wpisuje się je w pasku sesji. Docelowo: DPAPI / Windows Credential Manager.
    /// </summary>
    public class ServerInfo
    {
        /// <summary>Wersja kształtu pól tego wpisu. CELOWO bez domyślnej wartości = CurrentSchemaVersion —
        /// inaczej deserializacja starego pliku (bez tego pola) i świeży obiekt byłyby nie do odróżnienia
        /// (System.Text.Json nie zeruje pól nieobecnych w JSON, tylko zostawia wartość z inicjalizatora).
        /// 0 = nieoznaczone/sprzed wprowadzenia znacznika. ServerRepository.Save wpisuje bieżącą wersję.
        /// Dziś żadne pole nie zmieniło znaczenia, więc nie ma jeszcze kroku Migrate() — sam znacznik
        /// na przyszłość (B5 z przeglądu).</summary>
        public int SchemaVersion { get; set; }

        /// <summary>Publiczne dla testów (C5) i ewentualnej przyszłej migracji.</summary>
        public const int CurrentSchemaVersion = 1;

        /// <summary>Stały identyfikator — klucz poświadczeń w Windows Credential Manager.</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Name { get; set; }
        public string Host { get; set; }
        public int Port { get; set; } = 3389;
        public string Username { get; set; } = "";
        public string Domain { get; set; } = "";

        /// <summary>Protokół połączenia — RDP (domyślnie) lub SSH (terminal).</summary>
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public RemoteProtocol Protocol { get; set; } = RemoteProtocol.Rdp;

        /// <summary>SSH: ścieżka do pliku klucza prywatnego (puste = uwierzytelnianie hasłem).</summary>
        public string PrivateKeyPath { get; set; } = "";

        /// <summary>SSH: tunele lokalne w składni ssh -L („portLokalny:host:portZdalny", po jednej regule).</summary>
        public List<string> Tunnels { get; set; } = new List<string>();

        /// <summary>FTP: tryb szyfrowania — 0 = jawne FTPS, 1 = niejawne FTPS, 2 = brak (zwykły FTP).</summary>
        public int FtpEncryption { get; set; }

        /// <summary>FTP: logowanie anonimowe (login „anonymous", bez hasła).</summary>
        public bool FtpAnonymous { get; set; }

        /// <summary>Logowanie zintegrowane kontem Windows (bez podawania loginu/hasła).</summary>
        public bool UseWindowsAccount { get; set; }

        public string Group { get; set; }

        /// <summary>Etykiety/tagi do filtrowania w wyszukiwarce (np. „prod", „klientA"). Nie wpływają na łączenie.</summary>
        public List<string> Tags { get; set; } = new List<string>();

        /// <summary>Dowolna notatka o serwerze (widoczna w tooltipie wiersza i w edytorze). Nie wpływa na łączenie.</summary>
        public string Notes { get; set; } = "";

        /// <summary>Inicjały do "awatara" na liście (jak w mockupie). Liczone z nazwy przy renderowaniu.</summary>
        public string Initials { get; set; }

        /// <summary>Kolor awatara (hex, np. „#3B82F6"). Puste = kolor automatyczny wg grupy.</summary>
        public string AvatarColor { get; set; } = "";

        /// <summary>Czy hasło ma być zapisane w Windows Credential Manager.</summary>
        public bool SavePassword { get; set; }

        /// <summary>Odnośnik do współdzielonego profilu poświadczeń ([[CredentialProfile]]); puste = własny login.
        /// Gdy ustawiony, przy łączeniu login/domena/hasło pochodzą z profilu, nie z pól tego serwera.</summary>
        public string CredentialProfileId { get; set; } = "";

        /// <summary>Ulubiony / przypięty — pokazywany w sekcji „Przypięte" na górze listy.</summary>
        public bool Pinned { get; set; }

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

        /// <summary>
        /// Pełny ekran na wszystkich monitorach (mstsc „use multimon"). EKSPERYMENTALNE:
        /// aktywne tylko, gdy system ma więcej niż jeden monitor — na jednym zachowanie bez zmian.
        /// </summary>
        public bool UseAllMonitors { get; set; }

        /// <summary>Sesja administracyjna/konsolowa (mstsc /admin).</summary>
        public bool AdminSession { get; set; }

        /// <summary>
        /// RemoteApp: program/alias uruchamiany zamiast pełnego pulpitu (mstsc „remoteapplicationprogram").
        /// Ścieżka („C:\…\app.exe"), sama nazwa („notepad.exe") lub opublikowany alias („||AppAlias").
        /// Puste = zwykły pulpit zdalny.
        /// </summary>
        public string RemoteAppProgram { get; set; } = "";

        /// <summary>RemoteApp: argumenty wiersza poleceń (mstsc „remoteapplicationcmdline").</summary>
        public string RemoteAppArgs { get; set; } = "";

        /// <summary>Adres MAC do Wake-on-LAN (puste = funkcja nieaktywna dla tego serwera).</summary>
        public string MacAddress { get; set; } = "";

        /// <summary>Host bramy RD Gateway / jump-hosta (puste = połączenie bezpośrednie).</summary>
        public string GatewayHostname { get; set; } = "";

        /// <summary>
        /// Tryb użycia bramy (mapuje na IMsRdpClientTransportSettings.GatewayUsageMethod):
        /// 0 = nie używaj, 1 = zawsze przez bramę, 2 = wykryj automatycznie.
        /// Gdy pusty GatewayHostname, brama jest ignorowana niezależnie od tej wartości.
        /// </summary>
        public int GatewayUsageMethod { get; set; }

        /// <summary>Klucz w Credential Manager (hasło NIE jest trzymane w tym modelu ani w JSON).</summary>
        [JsonIgnore]
        public string CredTarget => "RdpManager:" + Id;

        /// <summary>Płytka kopia — do łączenia z loginem podmienionym z profilu poświadczeń (transient, nie zapisywana).</summary>
        public ServerInfo ShallowClone() => (ServerInfo)MemberwiseClone();

        /// <summary>
        /// Status prezentacyjny (kropka online/idle/offline). Na razie dane testowe —
        /// docelowo z monitoringu osiągalności w tle (TCP 3389 / ping).
        /// </summary>
        public ServerStatus Status { get; set; } = ServerStatus.Offline;

        /// <summary>Zachowuje pola zapisane przez NOWSZĄ wersję aplikacji, których ta (starsza) wersja
        /// jeszcze nie zna (analogicznie do AppSettings.Extra) — chroni definicje serwerów przed utratą
        /// pól przy uruchomieniu starszego builda. Hasła i tak nigdy nie trafiają do JSON.</summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement> Extra { get; set; }
    }

    public enum ServerStatus
    {
        Online,
        Idle,
        Offline
    }

    /// <summary>
    /// Protokół zdalnego połączenia. Serial: Host = nazwa portu COM, Port = baud.
    /// Http: Host = pełny adres URL (panel webowy otwierany w przeglądarce).
    /// </summary>
    public enum RemoteProtocol
    {
        Rdp,
        Ssh,
        Telnet,
        Serial,
        Http,
        Vnc,
        Sftp,  // dopisywać na KOŃCU (enum serializowany po nazwie; kolejność ważna dla combo w edytorze)
        Ftp,
        Rest   // klient HTTP/REST („wpis = jedno API"); Host = bazowy URL
    }

    /// <summary>Grupa serwerów w drzewie (Produkcja / Staging / Klienci …).</summary>
    public class ServerGroup
    {
        public string Name { get; set; }
        public List<ServerInfo> Servers { get; set; } = new List<ServerInfo>();
    }
}
