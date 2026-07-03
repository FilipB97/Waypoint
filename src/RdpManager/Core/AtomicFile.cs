using System.IO;
using System.Text;

namespace RdpManager.Core
{
    /// <summary>
    /// Atomowy zapis tekstu: pisze do pliku tymczasowego obok celu i podmienia go przez
    /// File.Move(overwrite) — crash/utrata zasilania w trakcie nie zostawia uciętego pliku
    /// (stary plik zostaje nienaruszony). Zapis jako UTF-8 bez BOM.
    /// </summary>
    public static class AtomicFile
    {
        public static void WriteAllText(string path, string contents)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string tmp = path + ".tmp";
            File.WriteAllText(tmp, contents, new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }

        /// <summary>Kopia zapasowa istniejącego pliku (sufiks „.bak") przed nadpisaniem — jeden poziom wstecz.
        /// Najlepszy wysiłek: błąd kopiowania nie może przerwać właściwego zapisu.</summary>
        public static void Backup(string path)
        {
            try { if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true); }
            catch { /* backup jest opcjonalny */ }
        }

        /// <summary>Odkłada uszkodzony/nieparsowalny plik na bok (sufiks „.corrupt") zamiast go stracić —
        /// żeby dało się ręcznie odzyskać dane, zanim wołający wróci do wartości domyślnych. Najlepszy wysiłek.</summary>
        public static void PreserveCorrupt(string path)
        {
            try { if (File.Exists(path)) File.Copy(path, path + ".corrupt", overwrite: true); }
            catch { /* najlepszy wysiłek */ }
        }
    }
}
