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
        public string Status { get; set; } = "Rozłączony";

        /// <summary>Utrzymuje rozdzielczość sesji = rozmiar kontrolki (pełny ekran/resize).</summary>
        public RdpDynamicResolution Resizer { get; set; }

        /// <summary>Hasło — wyłącznie w pamięci, na czas życia sesji. Nigdzie nie zapisywane.</summary>
        public string Password { get; set; } = "";

        public Session(ServerInfo server, AxMsRdpClient11NotSafeForScripting rdp, WindowsFormsHost host)
        {
            Server = server;
            Rdp = rdp;
            Host = host;
        }
    }
}
