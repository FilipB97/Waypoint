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
    }
}
