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

        public FrameworkElement TabButton { get; set; }
        public bool Connected { get; set; }

        /// <summary>Czy sesja doszła do pełnego zalogowania (odróżnia błąd połączenia od zwykłego rozłączenia).</summary>
        public bool LoggedIn { get; set; }

        public string Status { get; set; } = "Rozłączony";
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
