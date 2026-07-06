using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

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

        /// <summary>Pokazano już jednorazowe ostrzeżenie o jawnym tekście Telnet / braku szyfrowania VNC.</summary>
        public bool TelnetWarned { get; set; }
        public bool VncWarned { get; set; }

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

        /// <summary>Wersja aplikacji przy ostatnim starcie — do wykrycia „właśnie zaktualizowano" i pokazania „co nowego".</summary>
        public string LastRunVersion { get; set; } = "";

        /// <summary>Minimalizacja chowa okno do zasobnika (powrót przez ikonę).</summary>
        public bool MinimizeToTray { get; set; }

        /// <summary>Globalny skrót Ctrl+Alt+Q otwiera Szybkie połączenie (działa też z zasobnika).</summary>
        public bool QuickConnectHotkey { get; set; }

        /// <summary>Id serwerów otwartych jako karty przy ostatnim zamknięciu (do przywrócenia sesji).</summary>
        public List<string> LastOpenServerIds { get; set; } = new List<string>();

        /// <summary>Pytaj na starcie o przywrócenie ostatnio otwartych połączeń.</summary>
        public bool RestorePrompt { get; set; } = true;

        /// <summary>Serwery, z którymi łączyć się automatycznie na starcie, W KOLEJNOŚCI uruchamiania.
        /// Niepuste = ma priorytet nad popupem przywracania (łączymy z tymi i popup się nie pokazuje).</summary>
        public List<string> AutoConnectServerIds { get; set; } = new List<string>();

        /// <summary>Zapisane grupy kart (stosy jak w Vivaldi). Odtwarzane przy starcie — sesje serwerów
        /// z danej grupy trafiają do niej automatycznie.</summary>
        public List<TabGroupDef> TabGroups { get; set; } = new List<TabGroupDef>();

        /// <summary>Styl renderowania listy serwerów i paska kart: „Default" | „Minimal".</summary>
        public string ListStyle { get; set; } = "Default";

        /// <summary>Zachowuje pola zapisane przez NOWSZĄ wersję aplikacji, których ta (starsza) wersja
        /// jeszcze nie zna: [JsonExtensionData] wczytuje nieznane właściwości tutaj i zapisuje je z powrotem.
        /// Dzięki temu uruchomienie starszego builda nie kasuje ustawień dodanych przez nowszy
        /// (np. AutoConnectServerIds). Główny bezpiecznik przeciw utracie konfiguracji przy mieszaniu wersji.</summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement> Extra { get; set; }
    }

    /// <summary>Zapis grupy kart w settings.json (kolor jako #AARRGGBB).</summary>
    public class TabGroupDef
    {
        public string Name { get; set; }
        public string Color { get; set; }
        public bool Collapsed { get; set; }
        public List<string> ServerIds { get; set; } = new List<string>();
    }
}
