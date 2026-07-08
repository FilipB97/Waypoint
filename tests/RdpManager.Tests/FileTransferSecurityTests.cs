using System;
using System.IO;
using RdpManager;
using Xunit;

namespace RdpManager.Tests
{
    // SafeCombine: nazwa pliku pochodzi z listingu ZDALNEGO serwera (SFTP/FTP) — złośliwy/skompromitowany
    // serwer mógłby próbować "zip-slip"/path traversal. Te testy pokrywają dokładnie ten wektor ataku.
    public class FileTransferSecurityTests
    {
        private static string TempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "waypoint-transfer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void SafeCombine_NormalName_StaysInsideDir()
        {
            string dir = TempDir();
            try
            {
                string dest = FileTransferPanel.SafeCombine(dir, "report.pdf");
                Assert.Equal(Path.GetFullPath(Path.Combine(dir, "report.pdf")), dest);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void SafeCombine_NameLooksDottedButIsLiteral_NotRejected()
        {
            string dir = TempDir();
            try
            {
                // "..foo" nie jest odwołaniem do rodzica (to nie jest CAŁY segment ".."), tylko zwykłą nazwą pliku.
                string dest = FileTransferPanel.SafeCombine(dir, "..foo");
                Assert.StartsWith(Path.GetFullPath(dir), dest, StringComparison.OrdinalIgnoreCase);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Theory]
        [InlineData("..")]
        [InlineData(".")]
        [InlineData("../../evil.txt")]
        [InlineData("..\\..\\evil.txt")]
        [InlineData("subdir/../../evil.txt")]
        [InlineData("C:evil.txt")]
        [InlineData("C:\\Windows\\System32\\evil.dll")]
        [InlineData("\\\\server\\share\\evil.txt")]
        [InlineData("")]
        [InlineData("   ")]
        public void SafeCombine_TraversalOrRootedName_Throws(string maliciousName)
        {
            string dir = TempDir();
            try
            {
                Assert.Throws<InvalidOperationException>(() => FileTransferPanel.SafeCombine(dir, maliciousName));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void SafeCombine_Null_Throws()
        {
            string dir = TempDir();
            try { Assert.Throws<InvalidOperationException>(() => FileTransferPanel.SafeCombine(dir, null)); }
            finally { Directory.Delete(dir, true); }
        }
    }
}
