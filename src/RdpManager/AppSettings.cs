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

        /// <summary>Motyw interfejsu: „Dark" | „Light" | „System".</summary>
        public string Theme { get; set; } = "Dark";

        /// <summary>Język interfejsu: „pl" | „en".</summary>
        public string Language { get; set; } = "pl";

        /// <summary>Domyślne ustawienia połączeń RDP.</summary>
        public bool AutoReconnect { get; set; } = true;
        public int ColorDepth { get; set; } = 32;

        /// <summary>Pytaj przed zamknięciem połączonej sesji.</summary>
        public bool ConfirmCloseConnected { get; set; } = true;

        /// <summary>Zapisuj dziennik audytu połączeń (metadane, bez haseł).</summary>
        public bool ConnectionLogEnabled { get; set; } = true;

        /// <summary>Id serwerów w kolejności ostatnich połączeń (najnowsze pierwsze).</summary>
        public List<string> RecentIds { get; set; } = new List<string>();

        /// <summary>Nazwy grup zwiniętych w drzewie (klucz „__pinned__" = sekcja Przypięte).</summary>
        public List<string> CollapsedGroups { get; set; } = new List<string>();

        /// <summary>Klik na serwer otwiera go od razu w osobnym oknie zamiast jako karta w managerze.</summary>
        public bool OpenInNewWindowByDefault { get; set; }

        /// <summary>Po zmaksymalizowaniu okna ukryj panel boczny — zostają tylko karty + sesja (tryb skupienia).</summary>
        public bool ImmersiveOnMaximize { get; set; } = true;

        /// <summary>Przy starcie sprawdź w tle, czy na GitHubie jest nowsza wersja (tylko powiadomienie).</summary>
        public bool CheckUpdates { get; set; } = true;

        /// <summary>Minimalizacja chowa okno do zasobnika (powrót przez ikonę).</summary>
        public bool MinimizeToTray { get; set; }

        /// <summary>Globalny skrót Ctrl+Alt+Q otwiera Szybkie połączenie (działa też z zasobnika).</summary>
        public bool QuickConnectHotkey { get; set; }

        /// <summary>Id serwerów otwartych jako karty przy ostatnim zamknięciu (do przywrócenia sesji).</summary>
        public List<string> LastOpenServerIds { get; set; } = new List<string>();

        /// <summary>Pytaj na starcie o przywrócenie ostatnio otwartych połączeń.</summary>
        public bool RestorePrompt { get; set; } = true;

        /// <summary>Serwery, z którymi łączyć się automatycznie na starcie. Niepuste = ma priorytet
        /// nad popupem przywracania (łączymy z tymi i popup się nie pokazuje).</summary>
        public List<string> AutoConnectServerIds { get; set; } = new List<string>();
    }
}
