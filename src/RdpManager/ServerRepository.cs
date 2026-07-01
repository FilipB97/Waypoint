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
        private static readonly string Dir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RdpManager");
        private static readonly string FilePath = Path.Combine(Dir, "servers.json");

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions { WriteIndented = true };

        public static List<ServerInfo> Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var list = JsonSerializer.Deserialize<List<ServerInfo>>(json);
                    if (list != null) return list;
                }
            }
            catch
            {
                // uszkodzony/niekompatybilny plik — seedujemy poniżej
            }

            // Pierwsze uruchomienie: seed z danych testowych, potem zapis.
            var seed = new List<ServerInfo>();
            foreach (var g in TestData.Groups())
                foreach (var s in g.Servers)
                    seed.Add(s);
            Save(seed);
            return seed;
        }

        public static void Save(List<ServerInfo> servers)
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(servers, Options));
        }
    }
}
