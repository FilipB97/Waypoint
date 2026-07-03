using System;
using System.IO;

namespace RdpManager.Core
{
    /// <summary>
    /// Lekki dziennik diagnostyczny operacji na plikach konfiguracji (persist.log w katalogu danych).
    /// Cel: gdy ustawienia „znikają", pokazać DOKŁADNIE co i kiedy zrobiły Load/Save/self-heal (ze scorem
    /// danych i pid procesu) — zamiast rekonstruować ze znaczników czasu plików. Best-effort, nigdy nie rzuca.
    /// Pisze do katalogu przekazanego przez wołającego (w testach = katalog tymczasowy, więc nie brudzi %APPDATA%).
    /// </summary>
    public static class PersistLog
    {
        public static void Write(string dir, string message)
        {
            try
            {
                if (string.IsNullOrEmpty(dir)) return;
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "persist.log");
                // Prosty limit rozmiaru — log nie rośnie w nieskończoność.
                try { if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path); }
                catch { /* rotacja opcjonalna */ }
                File.AppendAllText(path, string.Format("{0:yyyy-MM-dd HH:mm:ss.fff}  pid={1}  {2}{3}",
                    DateTime.Now, Environment.ProcessId, message, Environment.NewLine));
            }
            catch { /* dziennik nie może wywołać kolejnego błędu */ }
        }
    }
}
