using System;
using System.IO;
using RdpManager.Core;
using Xunit;

namespace RdpManager.Tests
{
    public class AtomicFileTests
    {
        // Ustala deterministyczne czasy zapisu, żeby test nie zależał od zegara ani od sleepów.
        private static readonly DateTime Base = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static string TempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "waypoint-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void BackupLooksNewer_NoBackup_False()
        {
            string dir = TempDir();
            try
            {
                string path = Path.Combine(dir, "data.json");
                File.WriteAllText(path, "{}");
                Assert.False(AtomicFile.BackupLooksNewer(path));   // brak .bak
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void BackupLooksNewer_BackupExists_FileMissing_True()
        {
            string dir = TempDir();
            try
            {
                string path = Path.Combine(dir, "data.json");
                File.WriteAllText(path + ".bak", "{}");
                Assert.True(AtomicFile.BackupLooksNewer(path));   // .bak jest, pliku brak
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void BackupLooksNewer_BackupNewerBeyondThreshold_True()
        {
            string dir = TempDir();
            try
            {
                string path = Path.Combine(dir, "data.json");
                File.WriteAllText(path, "{}");
                File.WriteAllText(path + ".bak", "{}");
                File.SetLastWriteTimeUtc(path, Base);
                File.SetLastWriteTimeUtc(path + ".bak", Base.AddSeconds(5));   // .bak nowszy o 5 s (> 2 s)
                Assert.True(AtomicFile.BackupLooksNewer(path));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void BackupLooksNewer_BackupNewerWithinThreshold_False()
        {
            string dir = TempDir();
            try
            {
                string path = Path.Combine(dir, "data.json");
                File.WriteAllText(path, "{}");
                File.WriteAllText(path + ".bak", "{}");
                File.SetLastWriteTimeUtc(path, Base);
                File.SetLastWriteTimeUtc(path + ".bak", Base.AddSeconds(1));   // tylko 1 s — próg to > 2 s
                Assert.False(AtomicFile.BackupLooksNewer(path));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void BackupLooksNewer_FileNewerThanBackup_False()
        {
            string dir = TempDir();
            try
            {
                string path = Path.Combine(dir, "data.json");
                File.WriteAllText(path, "{}");
                File.WriteAllText(path + ".bak", "{}");
                File.SetLastWriteTimeUtc(path, Base.AddSeconds(10));   // plik nowszy = normalny stan
                File.SetLastWriteTimeUtc(path + ".bak", Base);
                Assert.False(AtomicFile.BackupLooksNewer(path));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
