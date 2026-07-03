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
            AtomicFile.RecoverIfReverted(path);   // self-heal: cofnięty z zewnątrz plik → przywróć z .bak (przed odczytem)
            try
            {
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var list = JsonSerializer.Deserialize<List<ServerInfo>>(json);
                    if (list != null) return list;
                }
            }
            catch
            {
                // Uszkodzony/niekompatybilny plik z realnymi serwerami — zachowaj kopię (.corrupt)
                // ZANIM poniżej nadpiszemy go danymi przykładowymi, żeby dało się go odzyskać.
                AtomicFile.PreserveCorrupt(path);
            }

            // Pierwsze uruchomienie (albo plik uszkodzony): seed z przykładowych danych, potem zapis.
            var seed = new List<ServerInfo>();
            foreach (var g in TestData.Groups())
                foreach (var s in g.Servers)
                    seed.Add(s);
            Save(seed, dir);
            return seed;
        }

        /// <summary>Zapisuje listę do podanego katalogu (testowalne).</summary>
        public static void Save(List<ServerInfo> servers, string dir)
        {
            var path = FilePath(dir);
            AtomicFile.Backup(path);   // kopia poprzedniej listy na wypadek błędnego zapisu
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(servers, Options));
        }
    }
}
