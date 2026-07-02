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

        /// <summary>Terminal SSH (WebView2 + xterm.js) — null dla sesji RDP.</summary>
        public SshTerminalControl Ssh { get; }
        public bool IsSsh => Ssh != null;

        /// <summary>Element wizualny sesji w kontenerze (host RDP albo terminal SSH).</summary>
        public FrameworkElement View => IsSsh ? (FrameworkElement)Ssh : Host;

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

        /// <summary>Sesja SSH — zamiast kontrolki RDP żyje terminal.</summary>
        public Session(ServerInfo server, SshTerminalControl ssh)
        {
            Server = server;
            Ssh = ssh;
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
