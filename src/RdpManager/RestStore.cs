using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using RdpManager.Core;
using RdpManager.Models;

namespace RdpManager
{
    /// <summary>
    /// Kolekcje REST w pliku JSON (%APPDATA%\RdpManager\rest.json), mapowane po Id wpisu na liście
    /// („wpis = jedno API"). Sekrety uwierzytelniania NIE tu — idą do Windows Credential Manager
    /// ([[CredentialStore]]). Zapis atomowy z kopią .bak (jak [[ServerRepository]]).
    /// </summary>
    public static class RestStore
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions { WriteIndented = true };

        private static string FilePath(string dir) => Path.Combine(dir, "rest.json");

        public static Dictionary<string, RestCollection> Load() => Load(SettingsStore.Dir);

        public static Dictionary<string, RestCollection> Load(string dir)
        {
            var path = FilePath(dir);
            try
            {
                if (File.Exists(path))
                {
                    var data = JsonSerializer.Deserialize<Dictionary<string, RestCollection>>(File.ReadAllText(path));
                    if (data != null) return data;
                }
            }
            catch
            {
                AtomicFile.PreserveCorrupt(path);   // realne kolekcje w uszkodzonym pliku — zachowaj kopię
                HealthNotices.Add(HealthNoticeKind.FileQuarantined, Path.GetFileName(path));
            }
            return new Dictionary<string, RestCollection>();
        }

        public static void Save(Dictionary<string, RestCollection> data) => Save(data, SettingsStore.Dir);

        public static void Save(Dictionary<string, RestCollection> data, string dir)
        {
            var path = FilePath(dir);
            AtomicFile.Backup(path);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(data, Options));
        }

        /// <summary>Kolekcja dla wpisu (nowa, pusta, gdy brak). Nie zapisuje — patrz <see cref="Put"/>.</summary>
        public static RestCollection For(string serverId)
        {
            var all = Load();
            return all.TryGetValue(serverId, out var c) ? c : new RestCollection();
        }

        /// <summary>Zapisuje kolekcję pod Id wpisu (scala z istniejącym plikiem).</summary>
        public static void Put(string serverId, RestCollection collection)
        {
            var all = Load();
            all[serverId] = collection;
            Save(all);
        }
    }
}
