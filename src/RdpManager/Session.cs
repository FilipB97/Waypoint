using System.Windows;
using System.Windows.Forms.Integration;
using AxMSTSCLib;
using RdpManager.Models;

namespace RdpManager
{
    /// <summary>
    /// Jedna otwarta sesja RDP. Trzyma własną kontrolkę ActiveX i host WPF.
    /// Kontrolki wszystkich sesji żyją równolegle w kontenerze — przełączanie
    /// zakładek to zmiana Visibility (bez reparentowania HWND, żeby nie zrywać sesji).
    /// </summary>
    public class Session
    {
        public ServerInfo Server { get; }
        public AxMsRdpClient11NotSafeForScripting Rdp { get; }
        public WindowsFormsHost Host { get; }

        /// <summary>Terminal tekstowy (SSH/Telnet/Serial na xterm) — null dla sesji RDP.</summary>
        public XtermControl Term { get; }
        public bool IsTerm => Term != null;

        /// <summary>Kontrolka VNC (RemoteViewing, WinForms w Host) — null dla RDP/terminala.</summary>
        public RemoteViewing.Windows.Forms.VncControl Vnc { get; }
        public bool IsVnc => Vnc != null;

        /// <summary>Terminal SSH, jeśli to sesja SSH (skrót dla ścieżek SSH-owych: SFTP, tunele).</summary>
        public SshTerminalControl Ssh => Term as SshTerminalControl;
        public bool IsSsh => Term is SshTerminalControl;

        /// <summary>Widok plików (SFTP/FTP): dwupanelowy lokalny↔zdalny — null dla pozostałych protokołów.</summary>
        public DualFilePanel Files { get; }
        public bool IsFiles => Files != null;

        /// <summary>Konektor sesji plikowej: poświadczenia + fabryka <see cref="IRemoteFs"/> (null poza SFTP/FTP).</summary>
        public IFileConnector FilesConn { get; }

        /// <summary>Konsola HTTP/REST — null poza protokołem Rest.</summary>
        public RestConsole Rest { get; }
        public bool IsRest => Rest != null;

        /// <summary>Element wizualny sesji w kontenerze (host RDP/VNC, terminal, panel plików albo konsola REST).</summary>
        public FrameworkElement View => IsTerm ? (FrameworkElement)Term
                                      : IsFiles ? (FrameworkElement)Files
                                      : IsRest ? (FrameworkElement)Rest
                                      : Host;

        public FrameworkElement TabButton { get; set; }
        public bool Connected { get; set; }

        /// <summary>Czy sesja doszła do pełnego zalogowania (odróżnia błąd połączenia od zwykłego rozłączenia).</summary>
        public bool LoggedIn { get; set; }

        public string Status { get; set; } = LocalizationManager.S("S.st.disconnectedShort");
        public StatusKind StatusKind { get; set; } = StatusKind.Info;

        /// <summary>Hasło — wyłącznie w pamięci, na czas życia sesji. Nigdzie nie zapisywane.</summary>
        public string Password { get; set; } = "";

        /// <summary>Utrzymuje rozdzielczość sesji = rozmiar kontrolki (pełny ekran/resize).</summary>
        public RdpDynamicResolution Resizer { get; set; }

        public Session(ServerInfo server, AxMsRdpClient11NotSafeForScripting rdp, WindowsFormsHost host)
        {
            Server = server;
            Rdp = rdp;
            Host = host;
        }

        /// <summary>Sesja terminalowa (SSH/Telnet/Serial) — zamiast kontrolki RDP żyje terminal.</summary>
        public Session(ServerInfo server, XtermControl term)
        {
            Server = server;
            Term = term;
        }

        /// <summary>Sesja VNC — kontrolka RemoteViewing (WinForms) w hoście WPF, jak RDP.</summary>
        public Session(ServerInfo server, RemoteViewing.Windows.Forms.VncControl vnc, WindowsFormsHost host)
        {
            Server = server;
            Vnc = vnc;
            Host = host;
        }

        /// <summary>Sesja plikowa (SFTP/FTP) — panel plików zamiast kontrolki/terminala; łączy się leniwie.</summary>
        public Session(ServerInfo server, DualFilePanel files, IFileConnector conn)
        {
            Server = server;
            Files = files;
            FilesConn = conn;
        }

        /// <summary>Sesja REST — konsola HTTP zamiast kontrolki/terminala; brak cyklu łączenia (gotowa od razu).</summary>
        public Session(ServerInfo server, RestConsole rest)
        {
            Server = server;
            Rest = rest;
        }
    }

    /// <summary>Rodzaj komunikatu statusu (koloruje pasek sesji).</summary>
    public enum StatusKind
    {
        Info,
        Connecting,
        Ok,
        Error
    }
}
