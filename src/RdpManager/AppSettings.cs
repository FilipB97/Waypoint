using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RdpManager
{
    /// <summary>Ustawienia aplikacji (JSON w %APPDATA%\RdpManager\settings.json).</summary>
    public class AppSettings
    {
        /// <summary>Wersja kształtu pól. CELOWO bez domyślnej wartości = CurrentSchemaVersion — inaczej stary
        /// plik (bez tego pola) i świeży obiekt byłyby nie do odróżnienia (System.Text.Json nie zeruje pól
        /// nieobecnych w JSON, tylko zostawia wartość z inicjalizatora). 0 = nieoznaczone/sprzed wprowadzenia
        /// znacznika. SettingsStore.Save wpisuje bieżącą wersję. Żadne pole nie zmieniło jeszcze znaczenia,
        /// więc nie ma kroku Migrate() — sam znacznik na przyszłość (B5 z przeglądu). [JsonExtensionData]
        /// poniżej i tak chroni przed utratą nieznanych pól.</summary>
        public int SchemaVersion { get; set; }

        /// <summary>Publiczne dla testów (C5) i ewentualnej przyszłej migracji.</summary>
        public const int CurrentSchemaVersion = 1;

        public int DefaultPort { get; set; } = 3389;
        public int FullscreenBarDelayMs { get; set; } = 450;

        /// <summary>Krycie (0-100%) panelu bocznego wysuwanego w trybie skupienia. Panel ma prawie przezroczyste
        /// tło (Panel ~3%), więc nad jasną sesją treść przebijała — solidne podłoże o tym kryciu poprawia
        /// czytelność. 100 = w pełni kryjące. Domyślnie mocno kryjące.</summary>
        public int FocusPeekOpacity { get; set; } = 92;

        public bool ReachabilityEnabled { get; set; } = true;
        public int ReachabilityIntervalSec { get; set; } = 30;

        /// <summary>Skala interfejsu (zoom Ctrl+kółko), 1.0 = 100%.</summary>
        public double UiScale { get; set; } = 1.0;

        /// <summary>Motyw interfejsu: „Dark" | „Light" | „System".</summary>
        public string Theme { get; set; } = "Dark";

        /// <summary>Domyślny rozmiar czcionki terminala (SSH/Telnet/Serial), 8-24. Ctrl+kółko w terminalu
        /// zmienia go tylko na czas sesji — to jest wartość startowa dla nowych połączeń.</summary>
        public int TerminalFontSize { get; set; } = 14;

        /// <summary>Kolor obwódki (krawędzi) okien: "" = brak kolorowej ramki (domyślnie), "System" = akcent
        /// Windows, "#RRGGBB" = wybrany kolor. Steruje atrybutem DWMWA_BORDER_COLOR na Windows 11.</summary>
        public string WindowBorderColor { get; set; } = "";

        /// <summary>Język interfejsu: „pl" | „en".</summary>
        public string Language { get; set; } = "pl";

        /// <summary>Domyślne ustawienia połączeń RDP.</summary>
        public bool AutoReconnect { get; set; } = true;
        public int ColorDepth { get; set; } = 32;
        // Domyślne przekierowania dla NOWO tworzonych serwerów RDP (istniejące zachowują swoje).
        public bool DefaultRedirectClipboard { get; set; } = true;
        public bool DefaultRedirectDrives { get; set; } = false;   // dyski + drukarki
        // Ile sekund czekać na TCP zanim serwer uznamy za offline (sonda osiągalności).
        public int ProbeTimeoutSeconds { get; set; } = 2;

        /// <summary>Pytaj przed zamknięciem połączonej sesji.</summary>
        public bool ConfirmCloseConnected { get; set; } = true;

        /// <summary>Zapisuj dziennik audytu połączeń (metadane, bez haseł).</summary>
        public bool ConnectionLogEnabled { get; set; } = true;

        /// <summary>Pokazano już jednorazowe ostrzeżenie o braku szyfrowania: Telnet / VNC / zwykły FTP.</summary>
        public bool TelnetWarned { get; set; }
        public bool VncWarned { get; set; }
        public bool FtpWarned { get; set; }

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

        /// <summary>Pokazuj opóźnienie (ms) obok kropki dostępności w liście serwerów (Compass §4.3).
        /// Domyślnie wyłączone — dane z sondowania osiągalności w tle.</summary>
        public bool ShowLatency { get; set; }

        /// <summary>Własny kolor akcentu („#RRGGBB"); pusty = domyślny Compass (per motyw). Compass §4.7 —
        /// nadpisuje rodzinę kluczy Accent* i akcent kontrolek WPF-UI (ThemeManager).</summary>
        public string AccentColor { get; set; } = "";

        /// <summary>Nazwany preset palety (Compass §4.9), osobno dla trybu ciemnego i jasnego. „Waypoint" =
        /// domyślna baza. Id z <see cref="ThemePresets"/>. Akcent (AccentColor) nakłada się na wierzch presetu.</summary>
        public string ThemeVariantDark { get; set; } = "Waypoint";
        public string ThemeVariantLight { get; set; } = "Waypoint";

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
