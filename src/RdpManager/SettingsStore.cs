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
            AtomicFile.RecoverIfReverted(path);   // self-heal: cofnięty z zewnątrz plik → przywróć z .bak (przed odczytem)
            try
            {
                if (File.Exists(path))
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
                    if (s != null) return s;
                }
            }
            catch
            {
                // Uszkodzony plik: odłóż na bok (.corrupt) zamiast po cichu stracić, potem wróć do domyślnych.
                AtomicFile.PreserveCorrupt(path);
            }
            return new AppSettings();
        }

        /// <summary>Zapisuje ustawienia do podanego katalogu (testowalne).</summary>
        public static void Save(AppSettings settings, string dir)
        {
            var path = FilePath(dir);
            AtomicFile.Backup(path);   // kopia poprzedniej wersji na wypadek błędnego zapisu
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
        }
    }
}
