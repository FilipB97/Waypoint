using System;
using System.Collections.Generic;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using RdpManager.Core;
using RdpManager.Models;

namespace RdpManager
{
    /// <summary>
    /// Buduje połączenia SSH.NET (SshClient/SftpClient) na wspólnych poświadczeniach: hasło/klucz
    /// (z passphrasem) + weryfikacja klucza hosta (TOFU, współdzielony magazyn <see cref="KnownHosts"/>).
    /// Używany przez terminal SSH (powłoka) oraz przez sesje plikowe SFTP (osobny protokół) — dzięki
    /// czemu zaufanie kluczy hosta i logika uwierzytelniania są w jednym miejscu.
    /// </summary>
    public class SshConnectionFactory : IFileConnector
    {
        /// <summary>(host:port, odcisk, czyZmianaKlucza) → true = ufaj. Wołane z wątku SSH; obsługa marshaluje na UI.</summary>
        public Func<string, string, bool, bool> TrustHostKey;

        /// <summary>Klucz zaszyfrowany — poproś o passphrase (parametr: ścieżka). null = anuluj. Wątek SSH.</summary>
        public Func<string, string> RequestKeyPassphrase;

        private ServerInfo _server;          // parametry ostatniego połączenia
        private string _password;
        private string _hostKeyHost;
        private int _hostKeyPort;
        private PrivateKeyFile _loadedKey;   // cache: passphrase pytamy raz, nie przy każdym łączu
        private string _loadedKeyPath;

        /// <summary>Zapamiętuje poświadczenia (login/host/klucz z serwera + hasło w pamięci) do kolejnych łączeń.</summary>
        public void SetIdentity(ServerInfo server, string password)
        {
            _server = server;
            _password = password;
        }

        // Wspólne dla terminala i SFTP (osobne łącza TCP na tych samych poświadczeniach).
        public ConnectionInfo BuildConnectionInfo()
        {
            var server = _server;
            var password = _password;
            if (server == null) throw new InvalidOperationException("Brak poświadczeń połączenia (SetIdentity nie wywołane).");

            var methods = new List<AuthenticationMethod>();
            if (!string.IsNullOrWhiteSpace(server.PrivateKeyPath))
                methods.Add(new PrivateKeyAuthenticationMethod(server.Username, LoadPrivateKey(server.PrivateKeyPath)));
            if (!string.IsNullOrEmpty(password))
            {
                methods.Add(new PasswordAuthenticationMethod(server.Username, password));
                // Wiele serwerów używa keyboard-interactive zamiast czystego "password".
                var kbi = new KeyboardInteractiveAuthenticationMethod(server.Username);
                kbi.AuthenticationPrompt += (o, e) => { foreach (var p in e.Prompts) p.Response = password; };
                methods.Add(kbi);
            }
            if (methods.Count == 0)
                methods.Add(new NoneAuthenticationMethod(server.Username));

            _hostKeyHost = server.Host;
            _hostKeyPort = server.Port > 0 ? server.Port : 22;
            return new ConnectionInfo(server.Host, _hostKeyPort, server.Username, methods.ToArray())
            {
                Timeout = TimeSpan.FromSeconds(15),
                Encoding = Encoding.UTF8
            };
        }

        /// <summary>Podpina weryfikację klucza hosta do klienta (SshClient lub SftpClient).</summary>
        public void Attach(BaseClient client) => client.HostKeyReceived += OnHostKey;

        /// <summary>Nowy, ROZŁĄCZONY klient SFTP na bieżących poświadczeniach (wołający łączy).</summary>
        public SftpClient NewSftpClient()
        {
            var c = new SftpClient(BuildConnectionInfo());
            Attach(c);
            return c;
        }

        /// <summary>Zdalny system plików SFTP (dla panelu plików) — rozłączony; Connect łączy.</summary>
        public IRemoteFs NewFs() => new SftpFs(NewSftpClient);

        // TOFU: znany klucz → OK; nowy → pytanie (domyślnie ufaj); ZMIENIONY → pytanie z ostrzeżeniem (domyślnie odrzuć).
        private void OnHostKey(object sender, HostKeyEventArgs e)
        {
            try
            {
                string fp = KnownHosts.Fingerprint(e.HostKey);

                // Odczyt→sprawdzenie pod lockiem, ale lock ZWALNIAMY przed pytaniem o zaufanie: ask(...)
                // marshaluje na wątek UI (Dispatcher.Invoke, blokująco), a trzymanie KnownHosts.Sync przez
                // ten czas potrafiło zakleszczyć równoległe sesje/SFTP dobijające się o ten sam lock.
                bool changed;
                lock (KnownHosts.Sync)
                {
                    var store = KnownHosts.Load(SettingsStore.Dir);
                    var status = KnownHosts.Check(store, _hostKeyHost, _hostKeyPort, fp);
                    if (status == KnownHosts.Status.Match) { e.CanTrust = true; return; }
                    changed = status == KnownHosts.Status.Mismatch;
                }

                var ask = TrustHostKey;
                bool trust = ask != null ? ask(_hostKeyHost + ":" + _hostKeyPort, fp, changed) : !changed;
                if (trust)
                {
                    // Ponowny odczyt świeżego stanu i zapis pod lockiem. TOCTOU nieszkodliwe: inna sesja mogła
                    // w międzyczasie dopisać ten sam host — zapiszemy ten sam odcisk (idempotentnie).
                    lock (KnownHosts.Sync)
                    {
                        var store = KnownHosts.Load(SettingsStore.Dir);
                        store[KnownHosts.EntryKey(_hostKeyHost, _hostKeyPort)] = fp;
                        KnownHosts.Save(SettingsStore.Dir, store);
                    }
                }
                e.CanTrust = trust;
            }
            catch { e.CanTrust = false; }   // wątpliwość = odmowa (bezpieczny domyślny)
        }

        // Klucz zaszyfrowany passphrasem → dopytaj przez RequestKeyPassphrase (do 3 prób).
        private PrivateKeyFile LoadPrivateKey(string path)
        {
            if (_loadedKey != null && _loadedKeyPath == path) return _loadedKey;   // passphrase pytamy raz

            try { return Cache(new PrivateKeyFile(path), path); }
            catch (SshPassPhraseNullOrEmptyException)
            {
                var ask = RequestKeyPassphrase;
                if (ask == null) throw;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    string phrase = ask(path);
                    if (phrase == null) throw new SshAuthenticationException("Anulowano podawanie passphrase klucza.");
                    try { return Cache(new PrivateKeyFile(path, phrase), path); }
                    catch (SshException) { /* złe hasło klucza — spróbuj ponownie */ }
                    catch (InvalidOperationException) { /* jw. — starsze wersje rzucają inaczej */ }
                }
                throw new SshAuthenticationException("Niepoprawna passphrase klucza (3 próby).");
            }
        }

        private PrivateKeyFile Cache(PrivateKeyFile key, string path)
        {
            _loadedKey = key;
            _loadedKeyPath = path;
            return key;
        }
    }
}
