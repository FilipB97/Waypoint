using System.Collections.Generic;

namespace RdpManager.Core
{
    /// <summary>Rodzaj zdarzenia „samonaprawy" konfiguracji, które warto POKAZAĆ użytkownikowi (nieblokująco).
    /// Zbierane w trakcie ładowania — ZANIM wstanie okno i zostanie zastosowany słownik języka — więc trzymamy
    /// KIND, nie gotowy tekst; lokalizacja następuje dopiero przy wyświetleniu. [[waypoint-persistence-version-mixing]]</summary>
    public enum HealthNoticeKind
    {
        SettingsRestored,             // ustawienia przywrócone z kopii .bak (cofnięte z zewnątrz)
        SettingsRestoredAfterUpdate,  // ustawienia przywrócone z migawki sprzed aktualizacji
        ServersRestored,              // lista serwerów przywrócona z kopii .bak
        FileQuarantined               // uszkodzony plik odłożony jako .corrupt (Detail = nazwa pliku)
    }

    public readonly struct HealthNotice
    {
        public HealthNotice(HealthNoticeKind kind, string detail) { Kind = kind; Detail = detail; }
        public HealthNoticeKind Kind { get; }
        public string Detail { get; }
    }

    /// <summary>Zbiornik zdarzeń samonaprawy z ładowania konfiguracji. UI (MainWindow) opróżnia go raz na starcie
    /// i pokazuje InfoBar — zamiast milczeć jak dotąd (tylko persist.log). W pamięci, best-effort. Deduplikacja
    /// przy opróżnianiu, bo Load bywa wołany kilka razy (App.OnStartup dla motywu + Window_Loaded).</summary>
    public static class HealthNotices
    {
        private static readonly List<HealthNotice> _items = new List<HealthNotice>();
        private static readonly object _lock = new object();

        public static void Add(HealthNoticeKind kind, string detail = null)
        {
            lock (_lock) _items.Add(new HealthNotice(kind, detail));
        }

        /// <summary>Zwraca zebrane, ODRÓŻNIONE zdarzenia i czyści zbiornik.</summary>
        public static List<HealthNotice> Drain()
        {
            lock (_lock)
            {
                var seen = new HashSet<string>();
                var result = new List<HealthNotice>();
                foreach (var n in _items)
                    if (seen.Add(n.Kind + "|" + (n.Detail ?? "")))
                        result.Add(n);
                _items.Clear();
                return result;
            }
        }
    }
}
