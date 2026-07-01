using System;
using System.IO;
using System.Text.Json;

namespace RdpManager
{
    public static class SettingsStore
    {
        public static readonly string Dir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RdpManager");
        private static readonly string FilePath = Path.Combine(Dir, "settings.json");
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions { WriteIndented = true };

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                    if (s != null) return s;
                }
            }
            catch { /* uszkodzony plik -> domyślne */ }
            return new AppSettings();
        }

        public static void Save(AppSettings settings)
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
    }
}
