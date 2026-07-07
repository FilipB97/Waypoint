using System;
using AxMSTSCLib;
using MSTSCLib;
using RdpManager.Core;
using RdpManager.Models;

namespace RdpManager
{
    /// <summary>
    /// Wspólna konfiguracja kontrolki RDP tuż PRZED <c>Connect()</c> — jedna prawda dla sesji w karcie
    /// (MainWindow) i w osobnym oknie (SessionWindow). Kolejność operacji 1:1 jak w oryginale. RemoteApp
    /// NIE jest tu ustawiany — ma go tylko MainWindow i robi to po Apply. Login/domena/hasło przychodzą
    /// z zewnątrz jako EFEKTYWNE (MainWindow rozwiązuje profil poświadczeń, SessionWindow ma je gotowe).
    /// </summary>
    internal static class RdpConnect
    {
        public static void Apply(AxMsRdpClient11NotSafeForScripting rdp, ServerInfo server, AppSettings settings,
                                 string user, string domain, string password)
        {
            IMsRdpClientAdvancedSettings8 adv = rdp.AdvancedSettings9;
            adv.RDPPort = server.Port;
            // Weryfikacja tożsamości serwera (domyślnie 2 = ostrzegaj) — chroni przed MITM.
            adv.AuthenticationLevel = RdpConfigurator.ClampAuthLevel(server.AuthenticationLevel);
            adv.EnableCredSspSupport = true;
            adv.ConnectToAdministerServer = server.AdminSession;   // sesja konsolowa (mstsc /admin)
            adv.SmartSizing = false;   // dynamiczna rozdzielczość zajmie się dopasowaniem
            adv.EnableAutoReconnect = settings.AutoReconnect;
            rdp.ColorDepth = settings.ColorDepth;
            adv.RedirectClipboard = server.RedirectClipboard;
            adv.RedirectDrives = server.RedirectDrives;
            adv.RedirectPrinters = server.RedirectPrinters;
            adv.AudioRedirectionMode = RdpConfigurator.ClampAudioMode(server.AudioMode);
            try { rdp.SecuredSettings2.KeyboardHookMode = 2; } catch { }  // Alt+Tab/Win -> zdalna w pełnym ekranie

            // UseMultimon = false: pełny ekran kontrolki (FullScreen + UseMultimon) crashuje w WindowsFormsHost
            // (SEH w DispatchMessage). Multi-monitor realizujemy rozpięciem NASZEGO okna, nie kontrolki.
            try { ((IMsRdpClientNonScriptable5)rdp.GetOcx()).UseMultimon = false; } catch { }

            ApplyGateway(rdp, server);

            rdp.Server = server.Host;
            if (server.UseWindowsAccount)
            {
                rdp.UserName = ""; rdp.Domain = ""; adv.ClearTextPassword = "";
            }
            else
            {
                rdp.UserName = user; rdp.Domain = domain; adv.ClearTextPassword = password;
            }
        }

        /// <summary>Konfiguruje bramę RD Gateway / jump-host, jeśli serwer ją ma. Bezpieczne dla starszych kontrolek.</summary>
        public static void ApplyGateway(AxMsRdpClient11NotSafeForScripting rdp, ServerInfo server)
        {
            try
            {
                var ts = rdp.TransportSettings;
                if (string.IsNullOrWhiteSpace(server.GatewayHostname))
                {
                    ts.GatewayUsageMethod = 0; // brak bramy
                    return;
                }
                ts.GatewayHostname = server.GatewayHostname;
                ts.GatewayUsageMethod = RdpConfigurator.GatewayUsageMethod(server.GatewayHostname, server.GatewayUsageMethod);
                ts.GatewayProfileUsageMethod = 1; // 1 = jawnie z ustawień połączenia
                ts.GatewayCredsSource = 0;        // 0 = login/hasło (TSC_PROXY_CREDS_MODE_USERPASS)
            }
            catch (Exception) { /* kontrolka bez obsługi bramy — pomijamy */ }
        }
    }
}
