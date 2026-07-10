using System;
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
            // Flush(true) wymusza zrzut bajtów tmp na dysk PRZED atomowym rename — bez tego rename jest
            // atomowy (stary plik nietknięty), ale zawartość tmp może jeszcze nie być trwała i utrata
            // zasilania tuż po Move mogłaby zostawić pod `path` plik zerowy/uszkodzony.
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                sw.Write(contents);
                sw.Flush();
                fs.Flush(true);
            }
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

        /// <summary>True, gdy „.bak" jest WYRAŹNIE nowszy niż sam plik (albo plik nie istnieje, a .bak tak).
        /// Normalnie .bak powstaje TUŻ PRZED nadpisaniem, więc plik jest zawsze NIE STARSZY niż .bak;
        /// odwrotna relacja = plik podmieniono na starszy JUŻ PO zrobieniu kopii, czyli cofnięto go z
        /// zewnątrz (np. rollback antywirusa / ochrony folderów). To sam sygnał CZASU — wołający MUSI
        /// jeszcze sprawdzić, że .bak nie jest UBOŻSZY niż plik, zanim przywróci (inaczej cofnąłby dobre
        /// dane do domyślnych — „bujanie" pliku przez AV potrafi zostawić świeży, ubogi .bak).</summary>
        public static bool BackupLooksNewer(string path)
        {
            try
            {
                string bak = path + ".bak";
                if (!File.Exists(bak)) return false;
                if (!File.Exists(path)) return true;
                return File.GetLastWriteTimeUtc(bak) - File.GetLastWriteTimeUtc(path) > TimeSpan.FromSeconds(2);
            }
            catch { return false; }
        }
    }
}
