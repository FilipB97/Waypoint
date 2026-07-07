using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentFTP;
using RdpManager.Models;

namespace RdpManager
{
    /// <summary>
    /// FTP/FTPS przez FluentFTP (implementacja <see cref="IRemoteFs"/> — ten sam panel plików co SFTP).
    /// Tryb szyfrowania i login anonimowy bierze z <see cref="ServerInfo"/>. Certyfikaty TLS akceptowane
    /// bez pinowania (typowe dla wewnętrznych FTPS) — TOFU/pin certyfikatu poza zakresem v1.
    /// </summary>
    public sealed class FtpFs : IRemoteFs
    {
        private readonly ServerInfo _server;
        private readonly string _password;
        private FtpClient _c;

        public FtpFs(ServerInfo server, string password)
        {
            _server = server;
            _password = password;
        }

        public bool IsConnected => _c != null && _c.IsConnected;

        public void Connect()
        {
            string user = _server.FtpAnonymous ? "anonymous" : _server.Username;
            string pass = _server.FtpAnonymous ? "" : _password;
            int port = _server.Port > 0 ? _server.Port : 21;

            _c = new FtpClient(_server.Host, user, pass, port);
            _c.Config.EncryptionMode = _server.FtpEncryption == 1 ? FtpEncryptionMode.Implicit
                                     : _server.FtpEncryption == 2 ? FtpEncryptionMode.None
                                     : _server.FtpEncryption == 3 ? FtpEncryptionMode.Auto
                                     : FtpEncryptionMode.Explicit;   // domyślnie jawne FTPS
            _c.Config.ValidateAnyCertificate = true;   // akceptuj self-signed (wewn. FTPS); pinowanie poza zakresem v1
            _c.Connect();
        }

        public string HomeDirectory => _c.GetWorkingDirectory();

        public IEnumerable<RemoteEntry> List(string path)
            => _c.GetListing(path)
                 .Where(f => f.Name != "." && f.Name != "..")
                 .Select(f => new RemoteEntry
                 {
                     Name = f.Name,
                     FullName = f.FullName,
                     IsDir = f.Type == FtpObjectType.Directory,
                     Length = f.Size,
                     Modified = f.Modified
                 });

        public void Upload(Stream local, string remotePath, bool overwrite)
            => _c.UploadStream(local, remotePath, overwrite ? FtpRemoteExists.Overwrite : FtpRemoteExists.Skip);

        public void Download(string remotePath, Stream local) => _c.DownloadStream(local, remotePath);

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

    /// <summary>Konektor sesji plikowej FTP/FTPS: zapamiętuje serwer+hasło i tworzy <see cref="FtpFs"/>.</summary>
    public sealed class FtpConnector : IFileConnector
    {
        private ServerInfo _server;
        private string _password;

        public void SetIdentity(ServerInfo server, string password)
        {
            _server = server;
            _password = password;
        }

        public IRemoteFs NewFs() => new FtpFs(_server, _password);
    }
}
