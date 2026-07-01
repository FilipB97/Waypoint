using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
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
            try
            {
                var path = FilePath(dir);
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var list = JsonSerializer.Deserialize<List<ServerInfo>>(json);
                    if (list != null) return list;
                }
            }
            catch
            {
                // uszkodzony/niekompatybilny plik — seedujemy poniżej
            }

            // Pierwsze uruchomienie: seed z przykładowych danych, potem zapis.
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
            Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath(dir), JsonSerializer.Serialize(servers, Options));
        }
    }
}
