using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using RdpManager.Core;
using RdpManager.Models;

namespace RdpManager
{
    /// <summary>
    /// Globalna lista środowisk REST w %APPDATA%\RdpManager\environments.json — wspólna dla WSZYSTKICH
    /// kolekcji (użytkownik dodaje środowisko raz i wybiera je w każdej kolekcji przez
    /// <see cref="RestCollection.ActiveEnvironmentId"/>). Zmienne są jawne (nie na sekrety).
    ///
    /// Wzorzec jak <see cref="CredentialProfileRepository"/>: .bak przed zapisem, .corrupt przy
    /// nieparsowalnym pliku, self-heal z .bak (gdy cofnięty z zewnątrz i nie uboższy).
    ///
    /// Migracja: wcześniej środowiska żyły w kolekcji (rest.json). Przy pierwszym Load (brak
    /// environments.json) zbieramy je ze WSZYSTKICH kolekcji — dedup po Id (nie po nazwie, bo
    /// ActiveEnvironmentId kolekcji wskazuje konkretny Id; usunięcie po nazwie zerwałoby te odwołania).
    /// Stare kopie w rest.json zostają, ale są ignorowane (RestConsole czyta już wyłącznie ten store).
    /// </summary>
    public static class EnvironmentStore
    {
        private static readonly string DefaultDir =
            Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "RdpManager");

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions { WriteIndented = true };

        private static string FilePath(string dir) => Path.Combine(dir, "environments.json");
        private static string ActivePath(string dir) => Path.Combine(dir, "rest-active-env");

        // Aktywne środowisko jest GLOBALNE dla całego modułu REST (jak w Postmanie — jeden selektor u góry),
        // a nie per-kolekcja. Przechowywane w osobnym pliku tekstowym (samo Id). Puste = „brak środowiska".
        public static string GetActiveId() => GetActiveId(DefaultDir);
        public static string GetActiveId(string dir)
        {
            try { var p = ActivePath(dir); return File.Exists(p) ? File.ReadAllText(p).Trim() : ""; }
            catch { return ""; }
        }

        public static void SetActiveId(string id) => SetActiveId(id, DefaultDir);
        public static void SetActiveId(string id, string dir)
        {
            try { File.WriteAllText(ActivePath(dir), id ?? ""); } catch { /* best-effort */ }
        }

        public static List<RestEnvironment> Load() => Load(DefaultDir);

        public static void Save(List<RestEnvironment> envs) => Save(envs, DefaultDir);

        /// <summary>Wczytuje środowiska z podanego katalogu (testowalne). Przy braku pliku — jednorazowa
        /// migracja z kolekcji (rest.json), po czym zapisuje environments.json (także pusty = znacznik migracji).</summary>
        public static List<RestEnvironment> Load(string dir)
        {
            var path = FilePath(dir);

            if (!File.Exists(path))
            {
                var migrated = MigrateFromCollections(dir);
                Save(migrated, dir);   // twórz plik nawet pusty — znacznik „już zmigrowano"
                return migrated;
            }

            var main = ReadOrNull(path, preserveCorrupt: true);

            // Self-heal jak w CredentialProfileRepository: plik cofnięty z zewnątrz (.bak NOWSZY niż plik)
            // przywracamy tylko, gdy .bak ma NIE MNIEJ środowisk — nie „wskrzeszamy" świadomie usuniętych.
            if (AtomicFile.BackupLooksNewer(path))
            {
                var bak = ReadOrNull(path + ".bak", preserveCorrupt: false);
                if (bak != null && (main == null || bak.Count >= main.Count))
                {
                    try { File.Copy(path + ".bak", path, overwrite: true); } catch { /* best-effort */ }
                    return bak;
                }
            }
            return main ?? new List<RestEnvironment>();
        }

        // Zbiera środowiska ze wszystkich kolekcji, dedup po Id (zachowuje odwołania ActiveEnvironmentId).
        private static List<RestEnvironment> MigrateFromCollections(string dir)
        {
            var all = new List<RestEnvironment>();
            var seen = new HashSet<string>();
            try
            {
                foreach (var coll in RestStore.Load(dir).Values)
                {
                    if (coll?.Environments == null) continue;
                    foreach (var env in coll.Environments)
                        if (env != null && !string.IsNullOrEmpty(env.Id) && seen.Add(env.Id)) all.Add(env);
                }
            }
            catch { /* brak/uszkodzony rest.json — zaczynamy z pustą listą globalną */ }
            return all;
        }

        private static List<RestEnvironment> ReadOrNull(string p, bool preserveCorrupt)
        {
            try
            {
                if (File.Exists(p))
                {
                    var list = JsonSerializer.Deserialize<List<RestEnvironment>>(File.ReadAllText(p));
                    if (list != null) return list;
                }
            }
            catch
            {
                if (preserveCorrupt) AtomicFile.PreserveCorrupt(p);
            }
            return null;
        }

        /// <summary>Zapisuje środowiska do podanego katalogu (testowalne).</summary>
        public static void Save(List<RestEnvironment> envs, string dir)
        {
            var path = FilePath(dir);
            AtomicFile.Backup(path);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(envs ?? new List<RestEnvironment>(), Options));
        }
    }
}
