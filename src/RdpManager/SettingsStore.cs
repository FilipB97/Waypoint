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
            try
            {
                var path = FilePath(dir);
                if (File.Exists(path))
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
                    if (s != null) return s;
                }
            }
            catch { /* uszkodzony plik -> domyślne */ }
            return new AppSettings();
        }

        /// <summary>Zapisuje ustawienia do podanego katalogu (testowalne).</summary>
        public static void Save(AppSettings settings, string dir)
        {
            AtomicFile.WriteAllText(FilePath(dir), JsonSerializer.Serialize(settings, Options));
        }
    }
}
