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
                    try { File.Copy(path + ".bak", path, overwrite: true); } catch { /* best-effort */ }
                    return bak;
                }
            }
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
                if (preserveCorrupt) AtomicFile.PreserveCorrupt(p);
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
            var path = FilePath(dir);
            AtomicFile.Backup(path);   // kopia poprzedniej wersji na wypadek błędnego zapisu
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
        }
    }
}
