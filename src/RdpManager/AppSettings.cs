using System.Collections.Generic;

namespace RdpManager
{
    /// <summary>Ustawienia aplikacji (JSON w %APPDATA%\RdpManager\settings.json).</summary>
    public class AppSettings
    {
        public int DefaultPort { get; set; } = 3389;
        public int FullscreenBarDelayMs { get; set; } = 450;
        public bool ReachabilityEnabled { get; set; } = true;
        public int ReachabilityIntervalSec { get; set; } = 30;

        /// <summary>Skala interfejsu (zoom Ctrl+kółko), 1.0 = 100%.</summary>
        public double UiScale { get; set; } = 1.0;

        /// <summary>Domyślne ustawienia połączeń RDP.</summary>
        public bool AutoReconnect { get; set; } = true;
        public int ColorDepth { get; set; } = 32;

        /// <summary>Pytaj przed zamknięciem połączonej sesji.</summary>
        public bool ConfirmCloseConnected { get; set; } = true;

        /// <summary>Id serwerów w kolejności ostatnich połączeń (najnowsze pierwsze).</summary>
        public List<string> RecentIds { get; set; } = new List<string>();
    }
}
