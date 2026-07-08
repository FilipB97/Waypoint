using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentFTP;
using FluentFTP.Client.BaseClient;
using RdpManager.Models;

namespace RdpManager
{
    /// <summary>
    /// FTP/FTPS przez FluentFTP (implementacja <see cref="IRemoteFs"/> — ten sam panel plików co SFTP).
    /// Tryb szyfrowania i login anonimowy bierze z <see cref="ServerInfo"/>. Certyfikat TLS weryfikowany
    /// przez TOFU/pinning (<see cref="Core.FtpsCertPinning"/>) — ten sam wzorzec co znane klucze hosta SSH.
    /// </summary>
    public sealed class FtpFs : IRemoteFs
    {
        private readonly ServerInfo _server;
        private readonly string _password;
        private FtpClient _c;

        /// <summary>(host:port, odcisk, czyZmianaCertyfikatu) → true = ufaj. Wołane z wątku FTP; obsługa marshaluje na UI.</summary>
        public Func<string, string, bool, bool> TrustCertificate;

        public FtpFs(ServerInfo server, string password, Func<string, string, bool, bool> trustCertificate = null)
        {
            _server = server;
            _password = password;
            TrustCertificate = trustCertificate;
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
            _c.ValidateCertificate += OnValidateCertificate;   // TOFU/pin zamiast ValidateAnyCertificate (MITM)
            _c.Connect();
        }

        // TOFU: znany certyfikat → OK; nowy → pytanie (domyślnie ufaj); ZMIENIONY → pytanie z ostrzeżeniem
        // (domyślnie odrzuć). FtpSslValidationEventArgs.Accept domyślnie false — wątpliwość = odmowa.
        private void OnValidateCertificate(BaseFtpClient control, FtpSslValidationEventArgs e)
        {
            try
            {
                string fp = Core.FtpsCertPinning.Fingerprint(e.Certificate);
                int port = _server.Port > 0 ? _server.Port : 21;
                string hostPort = _server.Host + ":" + port;

                // Odczyt→sprawdzenie pod lockiem, zwolniony PRZED pytaniem o zaufanie (ask marshaluje na UI,
                // blokująco) — jak w SshConnectionFactory.OnHostKey, z tego samego powodu (uniknąć zakleszczenia).
                bool changed;
                lock (Core.FtpsCertPinning.Sync)
                {
                    var store = Core.FtpsCertPinning.Load(SettingsStore.Dir);
                    var status = Core.FtpsCertPinning.Check(store, _server.Host, port, fp);
                    if (status == Core.FtpsCertPinning.Status.Match) { e.Accept = true; return; }
                    changed = status == Core.FtpsCertPinning.Status.Mismatch;
                }

                var ask = TrustCertificate;
                bool trust = ask != null ? ask(hostPort, fp, changed) : !changed;
                if (trust)
                {
                    lock (Core.FtpsCertPinning.Sync)
                    {
                        var store = Core.FtpsCertPinning.Load(SettingsStore.Dir);
                        store[Core.FtpsCertPinning.EntryKey(_server.Host, port)] = fp;
                        Core.FtpsCertPinning.Save(SettingsStore.Dir, store);
                    }
                }
                e.Accept = trust;
            }
            catch { e.Accept = false; }   // wątpliwość = odmowa (bezpieczny domyślny)
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

        /// <summary>(host:port, odcisk, czyZmianaCertyfikatu) → true = ufaj. Przekazywane do każdego <see cref="FtpFs"/>.</summary>
        public Func<string, string, bool, bool> TrustCertificate;

        public void SetIdentity(ServerInfo server, string password)
        {
            _server = server;
            _password = password;
        }

        public IRemoteFs NewFs() => new FtpFs(_server, _password, TrustCertificate);
    }
}
