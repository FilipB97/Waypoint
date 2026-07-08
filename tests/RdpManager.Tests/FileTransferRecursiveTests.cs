using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using RdpManager;
using Xunit;

namespace RdpManager.Tests
{
    // UploadTree/DownloadTree end-to-end na LocalFs↔LocalFs (bez prawdziwego serwera SFTP/FTP) — pokrywa
    // rekurencję, nadpisywanie, anulowanie i path traversal (C3 z przeglądu — dotąd zupełnie nietestowane).
    public class FileTransferRecursiveTests
    {
        private static string TempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "waypoint-xfer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        // Buduje: src/root.txt, src/sub/nested.txt
        private static void MakeNestedTree(string root, string rootContent = "root", string nestedContent = "nested")
        {
            File.WriteAllText(Path.Combine(root, "root.txt"), rootContent);
            string sub = Path.Combine(root, "sub");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(sub, "nested.txt"), nestedContent);
        }

        [Fact]
        public void UploadTree_RecursivelyCopiesNestedTree_ToLocalFsTarget()
        {
            string src = TempDir(), dstParent = TempDir();
            try
            {
                MakeNestedTree(src);
                string srcName = Path.GetFileName(src);

                FileTransferPanel.UploadTree(new LocalFs(), src, LocalFsPath(dstParent), CancellationToken.None);

                string copied = Path.Combine(dstParent, srcName);
                Assert.Equal("root", File.ReadAllText(Path.Combine(copied, "root.txt")));
                Assert.Equal("nested", File.ReadAllText(Path.Combine(copied, "sub", "nested.txt")));
            }
            finally { Directory.Delete(src, true); Directory.Delete(dstParent, true); }
        }

        [Fact]
        public void DownloadTree_RecursivelyCopiesNestedTree_FromLocalFsSource()
        {
            string src = TempDir(), dst = TempDir();
            try
            {
                MakeNestedTree(src);
                string srcName = Path.GetFileName(src);

                FileTransferPanel.DownloadTree(new LocalFs(), LocalFsPath(src), srcName, isDir: true, dst, CancellationToken.None);

                string copied = Path.Combine(dst, srcName);
                Assert.Equal("root", File.ReadAllText(Path.Combine(copied, "root.txt")));
                Assert.Equal("nested", File.ReadAllText(Path.Combine(copied, "sub", "nested.txt")));
            }
            finally { Directory.Delete(src, true); Directory.Delete(dst, true); }
        }

        [Fact]
        public void UploadTree_ReUpload_OverwritesExistingContent()
        {
            string src = TempDir(), dstParent = TempDir();
            try
            {
                File.WriteAllText(Path.Combine(src, "f.txt"), "v1");
                string name = Path.GetFileName(src);
                FileTransferPanel.UploadTree(new LocalFs(), src, LocalFsPath(dstParent), CancellationToken.None);

                File.WriteAllText(Path.Combine(src, "f.txt"), "v2");
                FileTransferPanel.UploadTree(new LocalFs(), src, LocalFsPath(dstParent), CancellationToken.None);

                Assert.Equal("v2", File.ReadAllText(Path.Combine(dstParent, name, "f.txt")));
            }
            finally { Directory.Delete(src, true); Directory.Delete(dstParent, true); }
        }

        [Fact]
        public void DownloadTree_ReDownload_OverwritesExistingContent()
        {
            string src = TempDir(), dst = TempDir();
            try
            {
                File.WriteAllText(Path.Combine(src, "f.txt"), "v1");
                string name = Path.GetFileName(src);
                FileTransferPanel.DownloadTree(new LocalFs(), LocalFsPath(src), name, isDir: true, dst, CancellationToken.None);

                File.WriteAllText(Path.Combine(src, "f.txt"), "v2");
                FileTransferPanel.DownloadTree(new LocalFs(), LocalFsPath(src), name, isDir: true, dst, CancellationToken.None);

                Assert.Equal("v2", File.ReadAllText(Path.Combine(dst, name, "f.txt")));
            }
            finally { Directory.Delete(src, true); Directory.Delete(dst, true); }
        }

        [Fact]
        public void UploadTree_PreCancelledToken_ThrowsWithoutWriting()
        {
            string src = TempDir(), dstParent = TempDir();
            try
            {
                MakeNestedTree(src);
                using var cts = new CancellationTokenSource();
                cts.Cancel();
                Assert.Throws<OperationCanceledException>(() =>
                    FileTransferPanel.UploadTree(new LocalFs(), src, LocalFsPath(dstParent), cts.Token));
                Assert.Empty(Directory.GetFileSystemEntries(dstParent));   // nic nie zdążyło powstać
            }
            finally { Directory.Delete(src, true); Directory.Delete(dstParent, true); }
        }

        [Fact]
        public void DownloadTree_PreCancelledToken_ThrowsWithoutWriting()
        {
            string src = TempDir(), dst = TempDir();
            try
            {
                MakeNestedTree(src);
                string name = Path.GetFileName(src);
                using var cts = new CancellationTokenSource();
                cts.Cancel();
                Assert.Throws<OperationCanceledException>(() =>
                    FileTransferPanel.DownloadTree(new LocalFs(), LocalFsPath(src), name, true, dst, cts.Token));
                Assert.Empty(Directory.GetFileSystemEntries(dst));
            }
            finally { Directory.Delete(src, true); Directory.Delete(dst, true); }
        }

        [Fact]
        public void DownloadTree_MaliciousTopLevelName_ThrowsAndDoesNotEscapeTargetDir()
        {
            string dst = TempDir();
            try
            {
                // isDir:false → List() nigdy wołane; SafeCombine dostaje złośliwą nazwę wprost jako remoteName.
                Assert.Throws<InvalidOperationException>(() =>
                    FileTransferPanel.DownloadTree(new LocalFs(), "/whatever", "..\\..\\evil.txt", false, dst, CancellationToken.None));

                Assert.Empty(Directory.GetFileSystemEntries(dst));   // nic nie zapisano nawet W ŚRODKU celu
                Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(dst) ?? "", "evil.txt")));   // ani PIĘTRO WYŻEJ
            }
            finally { Directory.Delete(dst, true); }
        }

        [Fact]
        public void DownloadTree_MaliciousNestedListingEntry_ThrowsAndDoesNotEscapeTargetDir()
        {
            string dst = TempDir();
            try
            {
                // Symuluje złośliwy/skompromitowany serwer: listing zdalnego katalogu zwraca wpis z niebezpieczną nazwą.
                var fs = new FakeListingFs(new RemoteEntry
                {
                    Name = "..\\..\\evil.txt",
                    FullName = "/remote/root/../../evil.txt",
                    IsDir = false,
                    Length = 3
                });

                Assert.Throws<InvalidOperationException>(() =>
                    FileTransferPanel.DownloadTree(fs, "/remote/root", "root", true, dst, CancellationToken.None));

                string createdRoot = Path.Combine(dst, "root");
                Assert.True(Directory.Exists(createdRoot));   // sam folder "root" powstaje (bezpieczna nazwa)…
                Assert.Empty(Directory.GetFileSystemEntries(createdRoot));   // …ale nic w środku (odrzucone przed zapisem)
            }
            finally { Directory.Delete(dst, true); }
        }

        // Konwertuje ścieżkę Windows na format "/"-owy, jakiego oczekuje LocalFs jako "IRemoteFs" (patrz LocalFs.Denorm).
        private static string LocalFsPath(string windowsPath) => windowsPath.Replace('\\', '/');

        // Minimalny fałszywy IRemoteFs zwracający jeden spreparowany wpis z listingu — do testowania H2
        // na poziomie DownloadTree (nie tylko SafeCombine w izolacji), bez prawdziwego złośliwego serwera.
        private sealed class FakeListingFs : IRemoteFs
        {
            private readonly RemoteEntry[] _entries;
            public FakeListingFs(params RemoteEntry[] entries) => _entries = entries;
            public bool IsConnected => true;
            public void Connect() { }
            public string HomeDirectory => "/";
            public IEnumerable<RemoteEntry> List(string path) => _entries;
            public void Upload(Stream local, string remotePath, bool overwrite) => throw new NotSupportedException();
            public void Download(string remotePath, Stream local) => local.Write(new byte[] { 1, 2, 3 }, 0, 3);
            public void CreateDirectory(string path) { }
            public void Delete(string fullPath, bool isDir) { }
            public void Dispose() { }
        }
    }
}
