using System;

namespace RdpManager.Core
{
    /// <summary>
    /// Czyste, testowalne przeliczenia ustawień RDP (bez dotykania kontrolki ActiveX). Właściwe
    /// zastosowanie na kontrolce robi <c>RdpConnect</c> (osobno, bo dotyka COM/WinForms).
    /// </summary>
    public static class RdpConfigurator
    {
        /// <summary>Poziom weryfikacji tożsamości serwera → zakres 0..2 (mapuje na AuthenticationLevel).</summary>
        public static uint ClampAuthLevel(int level) => (uint)Math.Clamp(level, 0, 2);

        /// <summary>Tryb dźwięku → zakres 0..2 (mapuje na AudioRedirectionMode).</summary>
        public static uint ClampAudioMode(int mode) => (uint)Math.Clamp(mode, 0, 2);

        /// <summary>Tryb skrótów Windows (SecuredSettings2.KeyboardHookMode): ustawienie serwera (0/1) wygrywa,
        /// inaczej globalne. Tryb 2 („zdalnie tylko w pełnym ekranie") pomijamy: dotyczy pełnego ekranu
        /// samej kontrolki, a Waypoint robi pełny ekran własnym oknem, więc 2 działało jak 0.</summary>
        public static int KeyboardHookMode(int serverMode, int globalMode)
        {
            if (serverMode == 0 || serverMode == 1) return serverMode;
            return globalMode == 1 ? 1 : 0;
        }

        /// <summary>Metoda użycia bramy RD Gateway: 0 gdy brak hosta bramy; inaczej wartość skonfigurowana,
        /// a 0 przy podanym hoście traktujemy jak 1 („zawsze przez bramę").</summary>
        public static uint GatewayUsageMethod(string gatewayHostname, int configured)
            => string.IsNullOrWhiteSpace(gatewayHostname) ? 0u : (uint)(configured == 0 ? 1 : configured);
    }
}
