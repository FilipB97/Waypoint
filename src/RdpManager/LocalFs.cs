using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RdpManager
{
    /// <summary>
    /// Lokalny system plików jako <see cref="IRemoteFs"/> — dla lewego panelu w widoku dwupanelowym.
    /// Ścieżki normalizowane do „/" (spójnie z panelem/SFTP); korzeń „/" = lista dysków.
    /// </summary>
    public sealed class LocalFs : IRemoteFs
    {
        public bool IsConnected => true;
        public void Connect() { }
        public void Dispose() { }

        public string HomeDirectory => Norm(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        public IEnumerable<RemoteEntry> List(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "/")   // korzeń → dyski
            {
                return DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => new RemoteEntry
                {
                    Name = d.Name.TrimEnd('\\', '/'),           // „C:"
                    FullName = Norm(d.RootDirectory.FullName),  // „C:/"
                    IsDir = true
                }).ToList();
            }

            string dir = Denorm(path);
            var entries = new List<RemoteEntry>();
            foreach (var d in Directory.GetDirectories(dir))
            {
                var di = new DirectoryInfo(d);
                entries.Add(new RemoteEntry { Name = di.Name, FullName = Norm(di.FullName), IsDir = true, Modified = SafeTime(() => di.LastWriteTime) });
            }
            foreach (var f in Directory.GetFiles(dir))
            {
                var fi = new FileInfo(f);
                entries.Add(new RemoteEntry { Name = fi.Name, FullName = Norm(fi.FullName), IsDir = false, Length = SafeLen(fi), Modified = SafeTime(() => fi.LastWriteTime) });
            }
            return entries;
        }

        public void Upload(Stream local, string remotePath, bool overwrite)
        {
            using (var fs = new FileStream(Denorm(remotePath), overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write))
                local.CopyTo(fs);
        }

        public void Download(string remotePath, Stream local)
        {
            using (var fs = File.OpenRead(Denorm(remotePath)))
                fs.CopyTo(local);
        }

        public void CreateDirectory(string path) => Directory.CreateDirectory(Denorm(path));

        public void Rename(string fullPath, string newFullPath)
        {
            string from = Denorm(fullPath), to = Denorm(newFullPath);
            if (Directory.Exists(from)) Directory.Move(from, to);
            else File.Move(from, to);   // rzuca, gdy cel istnieje — panel pyta wcześniej
        }

        public void Delete(string fullPath, bool isDir)
        {
            if (isDir) Directory.Delete(Denorm(fullPath), false);   // tylko pusty (jak reszta implementacji)
            else File.Delete(Denorm(fullPath));
        }

        public Core.RemoteFileInfo Stat(string path)
        {
            var fi = new FileInfo(Denorm(path));
            if (!fi.Exists) throw new FileNotFoundException(path);
            return new Core.RemoteFileInfo { Path = path, Length = fi.Length, ModifiedUtc = fi.LastWriteTimeUtc };
        }

        // Lokalnie: plik tymczasowy obok + File.Replace (podmiana z zachowaniem atrybutów i ACL celu).
        public bool SupportsSudo => false;
        public Core.SudoOutcome WriteFileSudo(byte[] content, string path, string password) => throw new NotSupportedException();

        public SafeWriteResult WriteFileSafe(byte[] content, Core.RemoteFileInfo original)
        {
            string real = Denorm(original.Path);
            string tmp = Path.Combine(Path.GetDirectoryName(real) ?? ".", "." + Path.GetFileName(real) + ".waypoint-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
            try
            {
                File.WriteAllBytes(tmp, content);
                File.Replace(tmp, real, null, ignoreMetadataErrors: true);
                return new SafeWriteResult { Mode = SafeWriteMode.AtomicReplace };
            }
            catch (Exception e) { throw new SafeWriteException(originalMayBeDamaged: false, e); }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }

        private static string Norm(string p) => (p ?? "").Replace('\\', '/');

        // „/"-ścieżka → ścieżka Windows; goła litera dysku „C:" → „C:\" (inaczej odnosi się do CWD dysku).
        private static string Denorm(string p)
        {
            p = (p ?? "").Replace('/', '\\');
            if (p.Length == 2 && p[1] == ':') p += "\\";
            return p;
        }

        private static long SafeLen(FileInfo fi) { try { return fi.Length; } catch { return 0; } }
        private static DateTime SafeTime(Func<DateTime> f) { try { return f(); } catch { return DateTime.MinValue; } }
    }
}
