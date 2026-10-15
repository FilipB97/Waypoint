using AxMSTSCLib;

namespace RdpManager
{
    /// <summary>
    /// Zwolnienie kontrolki RDP przy WYJŚCIU z aplikacji (#238). Wcześniej Window_Closing wołał
    /// Disconnect() i od razu WindowsFormsHost.Dispose(): Dispose dezaktywuje kontrolkę ActiveX
    /// (AxHost.InPlaceDeactivate → mstscax), a ta — zwłaszcza po wcześniejszym rozłączeniu albo w trakcie
    /// rozłączania (np. „sesja przejęta przez inne połączenie", RD Gateway) — potrafiła zablokować wątek UI
    /// w obsłudze WM_CLOSE. Okno zostawało w stanie „zamykania", a kolejne próby kończyły się
    /// InvalidOperationException z Window.VerifyNotClosing.
    ///
    /// Przy wyjściu z aplikacji: rozłączamy tylko sesję, która faktycznie jest połączona (serwer od razu
    /// zwalnia sesję), a hosta ActiveX NIE niszczymy — proces i tak się kończy, a system sprząta po nim
    /// okna i uchwyty (App.OnExit kończy proces, zanim zamknięcie dispatchera WPF zrobiłoby ten sam
    /// Dispose). Zamykanie pojedynczej karty działa jak dotąd.
    /// </summary>
    internal static class RdpShutdown
    {
        /// <summary>Zostały hosty RDP do posprzątania przez system — App.OnExit kończy wtedy proces od razu.</summary>
        internal static bool HostsLeftAlive { get; private set; }

        internal static void Release(AxMsRdpClient11NotSafeForScripting rdp)
        {
            if (rdp == null) return;
            HostsLeftAlive = true;
            try
            {
                // 0 = rozłączona, 1 = połączona, 2 = łączenie
                if (rdp.Connected != 0) rdp.Disconnect();
            }
            catch { /* kontrolka mogła już nie mieć okna */ }
        }
    }
}
