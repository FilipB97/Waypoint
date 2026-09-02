using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using RdpManager;
using RdpManager.Models;
using Xunit;

namespace RdpManager.Tests
{
    // Rekurencyjne usuwanie i przelicznik zawartości. Wszystkie trzy implementacje IRemoteFs kasują
    // wyłącznie PUSTE katalogi, więc rekurencja żyje w panelu — raz dla SFTP, FTP i dysku lokalnego.
    // Stąd testy idą przez LocalFs (zachowanie end-to-end) i przez atrapę (kolejność wywołań).
    public class FileTransferDeleteTests
    {
        private static string TempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "waypoint-del-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        // LocalFs oczekuje ścieżek z "/" (patrz LocalFs.Denorm).
        private static string P(string windowsPath) => windowsPath.Replace('\\', '/');

        // src/root.txt (4 B) + src/sub/nested.txt (6 B) + src/sub/deep/x.bin (3 B)
        private static void MakeTree(string root)
        {
            File.WriteAllText(Path.Combine(root, "root.txt"), "abcd");
            string sub = Path.Combine(root, "sub");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(sub, "nested.txt"), "abcdef");
            string deep = Path.Combine(sub, "deep");
            Directory.CreateDirectory(deep);
            File.WriteAllBytes(Path.Combine(deep, "x.bin"), new byte[] { 1, 2, 3 });
        }

        [Fact]
        public void DeleteTree_UsuwaNiepustyKatalogWCalosci()
        {
            string root = TempDir();
            try
            {
                MakeTree(root);
                FileTransferPanel.DeleteTree(new LocalFs(), P(root), isDir: true, CancellationToken.None);
                Assert.False(Directory.Exists(root));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [Fact]
        public void DeleteTree_UsuwaPojedynczyPlik()
        {
            string root = TempDir();
            try
            {
                string file = Path.Combine(root, "a.txt");
                File.WriteAllText(file, "x");
                FileTransferPanel.DeleteTree(new LocalFs(), P(file), isDir: false, CancellationToken.None);
                Assert.False(File.Exists(file));
                Assert.True(Directory.Exists(root));   // katalog nadrzędny nietknięty
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void DeleteTree_DzieciPrzedRodzicem()
        {
            // Kolejność nie jest kosmetyką: backendy kasują tylko puste katalogi, więc rodzic usunięty
            // przed dziećmi zakończyłby się błędem serwera w połowie operacji.
            var fs = new RecordingFs(new Dictionary<string, RemoteEntry[]>
            {
                ["/a"] = new[] { Dir("/a/b"), File_("/a/f1.txt") },
                ["/a/b"] = new[] { File_("/a/b/f2.txt") },
            });

            FileTransferPanel.DeleteTree(fs, "/a", isDir: true, CancellationToken.None);

            Assert.Equal(new[] { "/a/b/f2.txt", "/a/b", "/a/f1.txt", "/a" }, fs.Deleted);
        }

        [Fact]
        public void DeleteTree_AnulowanieZatrzymujeOperacje()
        {
            var cts = new CancellationTokenSource();
            cts.Cancel();
            var fs = new RecordingFs(new Dictionary<string, RemoteEntry[]> { ["/a"] = Array.Empty<RemoteEntry>() });

            Assert.Throws<OperationCanceledException>(
                () => FileTransferPanel.DeleteTree(fs, "/a", isDir: true, cts.Token));
            Assert.Empty(fs.Deleted);
        }

        [Fact]
        public void RemoteTreeStats_LiczyPlikiISumeBajtow()
        {
            string root = TempDir();
            try
            {
                MakeTree(root);
                var stats = FileTransferPanel.RemoteTreeStats(new LocalFs(), P(root), isDir: true, knownLength: 0);
                Assert.Equal(3, stats.Files);
                Assert.Equal(4 + 6 + 3, stats.Bytes);
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void RemoteTreeStats_PlikToJedenWpisOZnanymRozmiarze()
        {
            var stats = FileTransferPanel.RemoteTreeStats(new RecordingFs(null), "/x", isDir: false, knownLength: 42);
            Assert.Equal(1, stats.Files);
            Assert.Equal(42, stats.Bytes);
        }

        [Fact]
        public void LocalFs_Rename_ZmieniaNazwePlikuIKatalogu()
        {
            string root = TempDir();
            try
            {
                var fs = new LocalFs();
                string file = Path.Combine(root, "stary.txt");
                File.WriteAllText(file, "tresc");
                fs.Rename(P(file), P(Path.Combine(root, "nowy.txt")));
                Assert.False(File.Exists(file));
                Assert.Equal("tresc", File.ReadAllText(Path.Combine(root, "nowy.txt")));

                string dir = Path.Combine(root, "katalog");
                Directory.CreateDirectory(dir);
                fs.Rename(P(dir), P(Path.Combine(root, "inny")));
                Assert.False(Directory.Exists(dir));
                Assert.True(Directory.Exists(Path.Combine(root, "inny")));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void LocalFs_Rename_NaIstniejacaNazweRzuca()
        {
            // Panel sprawdza kolizję sam (żeby dać czytelny komunikat), ale backend nie może po cichu
            // nadpisać cudzego pliku, gdyby kontrola panelu kiedyś zniknęła.
            string root = TempDir();
            try
            {
                File.WriteAllText(Path.Combine(root, "a.txt"), "a");
                File.WriteAllText(Path.Combine(root, "b.txt"), "b");
                Assert.ThrowsAny<IOException>(
                    () => new LocalFs().Rename(P(Path.Combine(root, "a.txt")), P(Path.Combine(root, "b.txt"))));
            }
            finally { Directory.Delete(root, true); }
        }

        private static RemoteEntry Dir(string full)
            => new RemoteEntry { Name = full.Substring(full.LastIndexOf('/') + 1), FullName = full, IsDir = true };

        private static RemoteEntry File_(string full)
            => new RemoteEntry { Name = full.Substring(full.LastIndexOf('/') + 1), FullName = full, IsDir = false, Length = 1 };

        // Atrapa systemu plików ze stałym drzewem: zapisuje KOLEJNOŚĆ usunięć, bo to ona decyduje,
        // czy operacja przejdzie na prawdziwym serwerze.
        private sealed class RecordingFs : IRemoteFs
        {
            private readonly Dictionary<string, RemoteEntry[]> _tree;
            public readonly List<string> Deleted = new List<string>();

            public RecordingFs(Dictionary<string, RemoteEntry[]> tree) => _tree = tree;

            public bool IsConnected => true;
            public void Connect() { }
            public string HomeDirectory => "/";
            public IEnumerable<RemoteEntry> List(string path)
                => _tree != null && _tree.TryGetValue(path, out var e) ? e : Array.Empty<RemoteEntry>();
            public void Upload(Stream local, string remotePath, bool overwrite) => throw new NotSupportedException();
            public void Download(string remotePath, Stream local) => throw new NotSupportedException();
            public void CreateDirectory(string path) => throw new NotSupportedException();
            public void Delete(string fullPath, bool isDir) => Deleted.Add(fullPath);
            public void Rename(string fullPath, string newFullPath) => throw new NotSupportedException();
            public void Dispose() { }
        }
    }
}
