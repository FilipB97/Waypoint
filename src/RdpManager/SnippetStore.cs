using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using RdpManager.Core;
using RdpManager.Models;

namespace RdpManager
{
    /// <summary>
    /// Snippety komend w %APPDATA%\RdpManager\snippets.json — globalne, nie per serwer: sens snippetu
    /// polega na tym, że ta sama komenda działa na wielu maszynach (zmienne serwera podstawia
    /// <see cref="SnippetVars"/> dopiero przy wysyłce).
    ///
    /// Wzorzec trwałości jak w <see cref="EnvironmentStore"/>: .bak przed zapisem, .corrupt przy pliku
    /// nie do sparsowania, self-heal z .bak tylko gdy ten NIE JEST uboższy (żeby nie wskrzeszać snippetów
    /// świadomie usuniętych).
    ///
    /// Lista startowa jest pusta z rozmysłem. Waypoint łączy się i do Linuksa, i do sprzętu sieciowego
    /// po Telnecie, i do portu szeregowego — nie ma zestawu komend sensownego dla wszystkich trzech,
    /// a podsunięcie linuksowych na konsoli przełącznika byłoby mylące.
    /// </summary>
    public static class SnippetStore
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions { WriteIndented = true };

        private static string FilePath(string dir) => Path.Combine(dir, "snippets.json");

        public static List<CommandSnippet> Load() => Load(SettingsStore.Dir);

        public static void Save(List<CommandSnippet> snippets) => Save(snippets, SettingsStore.Dir);

        /// <summary>Wczytuje snippety z podanego katalogu (testowalne). Brak pliku = pusta lista.</summary>
        public static List<CommandSnippet> Load(string dir)
        {
            var path = FilePath(dir);
            if (!File.Exists(path)) return new List<CommandSnippet>();

            var main = ReadOrNull(path, preserveCorrupt: true);

            if (AtomicFile.BackupLooksNewer(path))
            {
                var bak = ReadOrNull(path + ".bak", preserveCorrupt: false);
                if (bak != null && (main == null || bak.Count >= main.Count))
                {
                    try { File.Copy(path + ".bak", path, overwrite: true); } catch { /* best-effort */ }
                    return Sanitize(bak);
                }
            }
            return Sanitize(main ?? new List<CommandSnippet>());
        }

        /// <summary>Zapisuje snippety do podanego katalogu (testowalne).</summary>
        public static void Save(List<CommandSnippet> snippets, string dir)
        {
            var path = FilePath(dir);
            AtomicFile.Backup(path);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(Sanitize(snippets), Options));
        }

        // Plik jest edytowalny ręcznie (i przenoszony między maszynami), więc wpisy bez treści albo bez Id
        // trzeba odsiać tutaj — inaczej pusty wiersz zajmowałby skrót Ctrl+Shift+1..9 i „wysyłał" nic.
        private static List<CommandSnippet> Sanitize(List<CommandSnippet> list)
        {
            var ok = new List<CommandSnippet>();
            if (list == null) return ok;
            foreach (var s in list)
            {
                if (s == null || string.IsNullOrWhiteSpace(s.Command)) continue;
                if (string.IsNullOrWhiteSpace(s.Id)) s.Id = System.Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(s.Name)) s.Name = FirstLine(s.Command);
                ok.Add(s);
            }
            return ok;
        }

        /// <summary>Nazwa zastępcza dla wpisu bez nazwy — pierwszy wiersz komendy, przycięty.</summary>
        public static string FirstLine(string command)
        {
            string s = (command ?? "").Replace("\r", "\n");
            int nl = s.IndexOf('\n');
            if (nl >= 0) s = s.Substring(0, nl);
            s = s.Trim();
            return s.Length <= 48 ? s : s.Substring(0, 47) + "…";
        }

        private static List<CommandSnippet> ReadOrNull(string p, bool preserveCorrupt)
        {
            try
            {
                if (File.Exists(p))
                {
                    var list = JsonSerializer.Deserialize<List<CommandSnippet>>(File.ReadAllText(p));
                    if (list != null) return list;
                }
            }
            catch
            {
                if (preserveCorrupt) AtomicFile.PreserveCorrupt(p);
            }
            return null;
        }
    }
}
