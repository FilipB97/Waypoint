using System;
using System.IO;
using System.Text.Json;
using RdpManager.Core;

namespace RdpManager
{
    public static class SettingsStore
    {
        public static readonly string Dir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RdpManager");
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions { WriteIndented = true };

        private static string FilePath(string dir) => Path.Combine(dir, "settings.json");

        public static AppSettings Load() => Load(Dir);

        public static void Save(AppSettings settings) => Save(settings, Dir);

        /// <summary>Wczytuje ustawienia z podanego katalogu (testowalne).</summary>
        public static AppSettings Load(string dir)
        {
            var path = FilePath(dir);
            var main = ReadOrNull(path, preserveCorrupt: true);

            // Self-heal: plik cofnięty z zewnątrz (np. rollback antywirusa)? Sygnał: .bak jest NOWSZY niż
            // plik (normalnie .bak powstaje tuż przed nadpisaniem, więc plik jest nie starszy). Przywracamy
            // JEDNAK tylko gdy .bak NIE jest uboższy niż bieżący plik — inaczej cofnęlibyśmy dobre ustawienia
            // do domyślnych (Norton potrafi „bujać" plik: świeży, ubogi .bak nad starszym, dobrym plikiem).
            if (AtomicFile.BackupLooksNewer(path))
            {
                var bak = ReadOrNull(path + ".bak", preserveCorrupt: false);
                if (bak != null && (main == null || DataScore(bak) >= DataScore(main)))
                {
                    PersistLog.Write(dir, $"settings.Load: SELF-HEAL z .bak (main={(main == null ? -1 : DataScore(main))}, bak={DataScore(bak)})");
                    try { File.Copy(path + ".bak", path, overwrite: true); } catch { /* best-effort */ }
                    HealthNotices.Add(HealthNoticeKind.SettingsRestored);
                    return bak;
                }
            }
            PersistLog.Write(dir, $"settings.Load: główny (score={(main == null ? -1 : DataScore(main))}, istnieje={File.Exists(path)})");
            return main ?? new AppSettings();
        }

        private static AppSettings ReadOrNull(string p, bool preserveCorrupt)
        {
            try
            {
                if (File.Exists(p))
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(p));
                    if (s != null) return s;
                }
            }
            catch
            {
                // Uszkodzony plik: odłóż na bok (.corrupt) zamiast po cichu stracić.
                if (preserveCorrupt)
                {
                    AtomicFile.PreserveCorrupt(p);
                    HealthNotices.Add(HealthNoticeKind.FileQuarantined, Path.GetFileName(p));
                }
            }
            return null;
        }

        // „Ilość danych użytkownika" — liczba wpisów w listach, które reset do domyślnych kasuje. Używane
        // przy self-heal do wyboru bogatszej wersji (nie cofamy bogatych ustawień do uboższych).
        private static int DataScore(AppSettings s) =>
            (s.RecentIds?.Count ?? 0) + (s.AutoConnectServerIds?.Count ?? 0) +
            (s.LastOpenServerIds?.Count ?? 0) + (s.CollapsedGroups?.Count ?? 0) +
            (s.TabGroups?.Count ?? 0);

        /// <summary>Zapisuje ustawienia do podanego katalogu (testowalne).</summary>
        public static void Save(AppSettings settings, string dir)
        {
            settings.SchemaVersion = AppSettings.CurrentSchemaVersion;   // B5: plik zapisany tym buildem = aktualna wersja
            var path = FilePath(dir);
            PersistLog.Write(dir, $"settings.Save: score={DataScore(settings)}");
            AtomicFile.Backup(path);   // kopia poprzedniej wersji na wypadek błędnego zapisu
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
        }

        private static string SnapshotPath(string dir) => Path.Combine(dir, "settings.preupdate.json");

        public static void SnapshotForUpdate(AppSettings settings) => SnapshotForUpdate(settings, Dir);

        /// <summary>Migawka bieżących ustawień PRZED aktualizacją (z PAMIĘCI — źródło prawdy). Po restarcie
        /// na nową wersję ConsumeUpdateSnapshot przywróci je deterministycznie, niezależnie od tego, co stanie
        /// się z settings.json podczas podmiany exe.</summary>
        public static void SnapshotForUpdate(AppSettings settings, string dir)
        {
            try
            {
                AtomicFile.WriteAllText(SnapshotPath(dir), JsonSerializer.Serialize(settings, Options));
                PersistLog.Write(dir, $"settings.Snapshot(pre-update): score={DataScore(settings)}");
            }
            catch { /* migawka jest best-effort */ }
        }

        public static AppSettings ConsumeUpdateSnapshot(AppSettings current) => ConsumeUpdateSnapshot(current, Dir);

        /// <summary>Jednorazowo po aktualizacji: jeśli migawka sprzed update jest BOGATSZA niż wczytany stan,
        /// przywróć ją (wczytany zapewne zubożał w trakcie podmiany). Zawsze usuwa migawkę po użyciu.</summary>
        public static AppSettings ConsumeUpdateSnapshot(AppSettings current, string dir)
        {
            var snap = SnapshotPath(dir);
            if (!File.Exists(snap)) return current ?? new AppSettings();
            var chosen = current;
            try
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(snap));
                if (s != null && (current == null || DataScore(s) > DataScore(current)))
                {
                    PersistLog.Write(dir, $"settings.ConsumeSnapshot: PRZYWRACAM sprzed update (snapshot={DataScore(s)} > bieżące={(current == null ? -1 : DataScore(current))})");
                    chosen = s;
                    // Bez HealthNotice: przywrócenie z migawki to NORMALNA ścieżka aktualizacji (przenosimy stan
                    // przez podmianę exe), nie anomalia — nie zawracamy użytkownikowi głowy. Ślad zostaje w persist.log.
                    Save(chosen, dir);   // utrwal przywrócone i odśwież .bak dobrą wersją
                }
                else
                {
                    PersistLog.Write(dir, $"settings.ConsumeSnapshot: bieżące OK, pomijam (snapshot={(s == null ? -1 : DataScore(s))}, bieżące={(current == null ? -1 : DataScore(current))})");
                }
            }
            catch { /* uszkodzona migawka — zostaje bieżące */ }
            try { File.Delete(snap); } catch { }
            return chosen ?? new AppSettings();
        }
    }
}
