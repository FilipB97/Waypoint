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
            foreach (var c in data.Values) c.SchemaVersion = RestCollection.CurrentSchemaVersion;   // B5: zapis tym buildem = aktualna wersja
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

        /// <summary>Usuwa kolekcję wpisu z rest.json (sprzątanie przy kasowaniu wpisu REST). Brak wpisu = no-op.</summary>
        public static void Remove(string serverId)
        {
            var all = Load();
            if (all.Remove(serverId)) Save(all);
        }

        /// <summary>Głęboka kopia kolekcji ze świeżymi Id żądań I folderów (do duplikowania wpisu). Historia czyszczona.
        /// Foldery też dostają świeże Id, bo od (patrz auth dziedziczone) mają własny sekret w Credential Managerze
        /// pod celem liczonym z Id — bez tego duplikat współdzieliłby cel z oryginałem. Referencje ParentId/FolderId
        /// przepisane na nowe Id. <paramref name="requestIdMap"/>/<paramref name="folderIdMap"/> = stare Id → nowe Id
        /// (do przeniesienia sekretów w Credential Manager).</summary>
        public static RestCollection DeepCopy(RestCollection src, out Dictionary<string, string> requestIdMap, out Dictionary<string, string> folderIdMap)
        {
            requestIdMap = new Dictionary<string, string>();
            folderIdMap = new Dictionary<string, string>();
            var copy = JsonSerializer.Deserialize<RestCollection>(JsonSerializer.Serialize(src, Options)) ?? new RestCollection();
            copy.History.Clear();

            foreach (var f in copy.Folders)
            {
                string oldId = f.Id;
                f.Id = System.Guid.NewGuid().ToString("N");
                folderIdMap[oldId] = f.Id;
            }
            foreach (var f in copy.Folders)
                if (!string.IsNullOrEmpty(f.ParentId) && folderIdMap.TryGetValue(f.ParentId, out var newParent))
                    f.ParentId = newParent;
            foreach (var r in copy.Requests)
                if (!string.IsNullOrEmpty(r.FolderId) && folderIdMap.TryGetValue(r.FolderId, out var newFolder))
                    r.FolderId = newFolder;

            foreach (var r in copy.Requests)
            {
                string oldId = r.Id;
                r.Id = System.Guid.NewGuid().ToString("N");   // nowy Id → osobny CredTarget (sekrety nie współdzielone)
                requestIdMap[oldId] = r.Id;
            }
            return copy;
        }
    }
}
