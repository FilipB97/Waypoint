using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Renci.SshNet;
using RdpManager.Models;

namespace RdpManager
{
    /// <summary>Wpis w zdalnym katalogu — wspólny dla SFTP i (docelowo) FTP/FTPS.</summary>
    public sealed class RemoteEntry
    {
        public string Name { get; set; }
        public string FullName { get; set; }
        public bool IsDir { get; set; }
        public long Length { get; set; }
        public DateTime Modified { get; set; }
    }

    /// <summary>
    /// Abstrakcja operacji na zdalnym systemie plików. Implementacje: <see cref="SftpFs"/> (SSH.NET),
    /// docelowo FTP/FTPS. Operacje wołane z wątku roboczego (panel serializuje je: jedna naraz).
    /// </summary>
    public interface IRemoteFs : IDisposable
    {
        bool IsConnected { get; }
        void Connect();
        string HomeDirectory { get; }
        IEnumerable<RemoteEntry> List(string path);
        void Upload(Stream local, string remotePath, bool overwrite);
        void Download(string remotePath, Stream local);
        void CreateDirectory(string path);
        void Delete(string fullPath, bool isDir);
    }

    /// <summary>
    /// Fabryka zdalnego systemu plików dla sesji plikowej: ustawia poświadczenia (przy łączeniu)
    /// i tworzy <see cref="IRemoteFs"/>. Implementacje: <see cref="SshConnectionFactory"/> (SFTP),
    /// docelowo FTP/FTPS.
    /// </summary>
    public interface IFileConnector
    {
        void SetIdentity(ServerInfo server, string password);
        IRemoteFs NewFs();
    }

    /// <summary>SFTP przez SSH.NET. Klienta dostarcza fabryka (rozłączony) — <see cref="Connect"/> łączy.</summary>
    public sealed class SftpFs : IRemoteFs
    {
        private readonly Func<SftpClient> _make;
        private SftpClient _c;

        public SftpFs(Func<SftpClient> make) { _make = make; }

        public bool IsConnected => _c != null && _c.IsConnected;

        public void Connect()
        {
            _c = _make();
            _c.Connect();
        }

        public string HomeDirectory => _c.WorkingDirectory;

        public IEnumerable<RemoteEntry> List(string path)
            => _c.ListDirectory(path)
                 .Where(f => f.Name != "." && f.Name != "..")
                 .Select(f => new RemoteEntry
                 {
                     Name = f.Name,
                     FullName = f.FullName,
                     IsDir = f.IsDirectory,
                     Length = f.Length,
                     Modified = f.LastWriteTime
                 });

        public void Upload(Stream local, string remotePath, bool overwrite) => _c.UploadFile(local, remotePath, overwrite);
        public void Download(string remotePath, Stream local) => _c.DownloadFile(remotePath, local);
        public void CreateDirectory(string path) => _c.CreateDirectory(path);

        public void Delete(string fullPath, bool isDir)
        {
            if (isDir) _c.DeleteDirectory(fullPath);
            else _c.DeleteFile(fullPath);
        }

        public void Dispose()
        {
            try { _c?.Dispose(); } catch { }
            _c = null;
        }
    }
}
