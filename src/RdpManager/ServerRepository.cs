using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using RdpManager.Core;
using RdpManager.Models;

namespace RdpManager
{
    /// <summary>
    /// Lista serwerów w pliku JSON (%APPDATA%\RdpManager\servers.json). Hasła NIE tu —
    /// idą do Windows Credential Manager ([[CredentialStore]]).
    /// </summary>
    public static class ServerRepository
    {
        private static readonly string DefaultDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RdpManager");

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions { WriteIndented = true };

        private static string FilePath(string dir) => Path.Combine(dir, "servers.json");

        public static List<ServerInfo> Load() => Load(DefaultDir);

        public static void Save(List<ServerInfo> servers) => Save(servers, DefaultDir);

        /// <summary>Wczytuje listę z podanego katalogu (testowalne). Pierwszy start — seed.</summary>
        public static List<ServerInfo> Load(string dir)
        {
            var path = FilePath(dir);
            var main = ReadOrNull(path, preserveCorrupt: true);

            // Self-heal jak w ustawieniach: cofnięty z zewnątrz plik (bak NOWSZY niż plik) przywracamy
            // tylko, gdy .bak ma NIE MNIEJ serwerów niż bieżący — żeby nie „odtworzyć" świadomie usuniętych
            // serwerów (usunięcie daje plik NOWSZY niż .bak, więc self-heal się wtedy nie odpala).
            if (AtomicFile.BackupLooksNewer(path))
            {
                var bak = ReadOrNull(path + ".bak", preserveCorrupt: false);
                if (bak != null && (main == null || bak.Count >= main.Count))
                {
                    try { File.Copy(path + ".bak", path, overwrite: true); } catch { /* best-effort */ }
                    HealthNotices.Add(HealthNoticeKind.ServersRestored);
                    return bak;
                }
            }
            if (main != null) return main;

            // Pierwsze uruchomienie: seed z przykładowych danych, potem zapis.
            var seed = new List<ServerInfo>();
            foreach (var g in TestData.Groups())
                foreach (var s in g.Servers)
                    seed.Add(s);
            Save(seed, dir);
            return seed;
        }

        private static List<ServerInfo> ReadOrNull(string p, bool preserveCorrupt)
        {
            try
            {
                if (File.Exists(p))
                {
                    var list = JsonSerializer.Deserialize<List<ServerInfo>>(File.ReadAllText(p));
                    if (list != null) return list;
                }
            }
            catch
            {
                // Uszkodzony/niekompatybilny plik z realnymi serwerami — zachowaj kopię (.corrupt).
                if (preserveCorrupt)
                {
                    AtomicFile.PreserveCorrupt(p);
                    HealthNotices.Add(HealthNoticeKind.FileQuarantined, Path.GetFileName(p));
                }
            }
            return null;
        }

        /// <summary>Zapisuje listę do podanego katalogu (testowalne).</summary>
        public static void Save(List<ServerInfo> servers, string dir)
        {
            foreach (var s in servers) s.SchemaVersion = ServerInfo.CurrentSchemaVersion;   // B5: zapis tym buildem = aktualna wersja
            var path = FilePath(dir);
            AtomicFile.Backup(path);   // kopia poprzedniej listy na wypadek błędnego zapisu
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(servers, Options));
        }
    }
}
