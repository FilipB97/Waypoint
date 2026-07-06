using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using RdpManager.Core;
using RdpManager.Models;

namespace RdpManager
{
    /// <summary>
    /// Lista współdzielonych profili poświadczeń w %APPDATA%\RdpManager\credprofiles.json. Hasła NIE tu —
    /// idą do Windows Credential Manager ([[CredentialStore]]). Wzorzec jak ServerRepository: .bak przed
    /// zapisem, .corrupt przy nieparsowalnym pliku, self-heal z .bak (gdy cofnięty z zewnątrz i nie uboższy).
    /// Bez seedu — pierwsze uruchomienie = pusta lista.
    /// </summary>
    public static class CredentialProfileRepository
    {
        private static readonly string DefaultDir =
            Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "RdpManager");

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions { WriteIndented = true };

        private static string FilePath(string dir) => Path.Combine(dir, "credprofiles.json");

        public static List<CredentialProfile> Load() => Load(DefaultDir);

        public static void Save(List<CredentialProfile> profiles) => Save(profiles, DefaultDir);

        /// <summary>Wczytuje profile z podanego katalogu (testowalne).</summary>
        public static List<CredentialProfile> Load(string dir)
        {
            var path = FilePath(dir);
            var main = ReadOrNull(path, preserveCorrupt: true);

            // Self-heal jak w ServerRepository: plik cofnięty z zewnątrz (.bak NOWSZY niż plik) przywracamy
            // tylko, gdy .bak ma NIE MNIEJ profili niż bieżący — nie „wskrzeszamy" świadomie usuniętych.
            if (AtomicFile.BackupLooksNewer(path))
            {
                var bak = ReadOrNull(path + ".bak", preserveCorrupt: false);
                if (bak != null && (main == null || bak.Count >= main.Count))
                {
                    try { File.Copy(path + ".bak", path, overwrite: true); } catch { /* best-effort */ }
                    return bak;
                }
            }
            return main ?? new List<CredentialProfile>();
        }

        private static List<CredentialProfile> ReadOrNull(string p, bool preserveCorrupt)
        {
            try
            {
                if (File.Exists(p))
                {
                    var list = JsonSerializer.Deserialize<List<CredentialProfile>>(File.ReadAllText(p));
                    if (list != null) return list;
                }
            }
            catch
            {
                if (preserveCorrupt) AtomicFile.PreserveCorrupt(p);
            }
            return null;
        }

        /// <summary>Zapisuje profile do podanego katalogu (testowalne).</summary>
        public static void Save(List<CredentialProfile> profiles, string dir)
        {
            var path = FilePath(dir);
            AtomicFile.Backup(path);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(profiles, Options));
        }
    }
}
