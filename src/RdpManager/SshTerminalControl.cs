using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Renci.SshNet;
using Renci.SshNet.Common;
using RdpManager.Core;
using RdpManager.Models;

namespace RdpManager
{
    /// <summary>
    /// Terminal SSH na bazie <see cref="XtermControl"/> + SSH.NET (ShellStream): hasło/klucz
    /// (z passphrasem), TOFU klucza hosta, tunele lokalne (ssh -L), resize PTY oraz panel
    /// plików SFTP (osobne łącze na tych samych poświadczeniach).
    /// </summary>
    public class SshTerminalControl : XtermControl
    {
        private SshClient _client;
        private ShellStream _shell;
        private readonly Decoder _utf8 = Encoding.UTF8.GetDecoder();   // stanowy — skleja rozcięte znaki wielobajtowe

        /// <summary>
        /// Pytanie o zaufanie kluczowi hosta: (host:port, odcisk, czyZmianaKlucza) → true = ufaj.
        /// Wołane z wątku SSH — obsługa musi zmarshalować się na UI (Dispatcher.Invoke).
        /// Bez subskrybenta: nowy klucz = ufaj (TOFU), zmieniony = odrzuć.
        /// </summary>
        public Func<string, string, bool, bool> TrustHostKey;

        /// <summary>
        /// Klucz prywatny jest zaszyfrowany — poproś o passphrase (parametr: ścieżka klucza).
        /// null = anuluj. Wątek SSH; obsługa marshaluje na UI.
        /// </summary>
        public Func<string, string> RequestKeyPassphrase;

        /// <summary>Status tunelu: (reguła, ok, błąd). Wątek SSH.</summary>
        public event Action<string, bool, string> TunnelStatus;

        private readonly List<ForwardedPortLocal> _forwards = new List<ForwardedPortLocal>();
        private string _hostKeyHost;
        private int _hostKeyPort;
        private ServerInfo _server;          // parametry ostatniego połączenia — dla SFTP
        private string _password;
        private PrivateKeyFile _loadedKey;   // cache: passphrase pytamy raz, nie przy każdym łączu
        private string _loadedKeyPath;

        private readonly Grid _grid = new Grid();
        private readonly ColumnDefinition _splitCol = new ColumnDefinition { Width = new GridLength(0) };
        private readonly ColumnDefinition _filesCol = new ColumnDefinition { Width = new GridLength(0) };
        private readonly GridSplitter _split = new GridSplitter
        {
            Width = 5,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = System.Windows.Media.Brushes.Transparent,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            Visibility = Visibility.Collapsed
        };
        private SftpPanel _files;

        public SshTerminalControl()
        {
            // terminal | splitter | panel plików SFTP (domyślnie zwinięty) — przekładamy Web z bazy do siatki
            Child = null;
            _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _grid.ColumnDefinitions.Add(_splitCol);
            _grid.ColumnDefinitions.Add(_filesCol);
            Grid.SetColumn(Web, 0);
            Grid.SetColumn(_split, 1);
            _grid.Children.Add(Web);
            _grid.Children.Add(_split);
            Child = _grid;
        }

        /// <summary>Pokazuje/chowa panel plików SFTP (tworzony leniwie przy pierwszym użyciu).</summary>
        public void ToggleFiles()
        {
            if (_files == null)
            {
                _files = new SftpPanel(CreateSftpClient);
                Grid.SetColumn(_files, 2);
                _grid.Children.Add(_files);
            }
            bool show = _files.Visibility != Visibility.Visible;
            _files.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            _split.Visibility = _files.Visibility;
            _splitCol.Width = show ? GridLength.Auto : new GridLength(0);
            _filesCol.Width = show ? new GridLength(360) : new GridLength(0);
            if (show) _files.RefreshAsync();
        }

        // ---------- Transport (XtermControl) ----------

        protected override void OnTerminalInput(string data)
        {
            var sh = _shell;
            if (sh == null) return;
            try { sh.Write(data); sh.Flush(); } catch { /* shell w trakcie zamykania */ }
        }

        /// <summary>Wysyła tekst do powłoki tak, jakby wpisał go użytkownik (do broadcastu). Serwer odbije echo.</summary>
        public void SendText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            OnTerminalInput(text);
        }

        protected override void OnTerminalResize(int cols, int rows) => TryResizePty(cols, rows);

        // ---------- Połączenie SSH ----------

        /// <summary>
        /// Łączy w tle (hasło i/lub klucz prywatny). Zdarzenia Connected/Disconnected
        /// przychodzą z wątków roboczych — wołający marshaluje na UI.
        /// </summary>
        public Task ConnectAsync(ServerInfo server, string password, int cols, int rows)
        {
            return Task.Run(() =>
            {
                DisposeClient();   // rekonekt: sprzątnij poprzednie połączenie

                _server = server;
                _password = password;
                var ci = BuildConnectionInfo(server, password);

                var client = new SshClient(ci);
                client.KeepAliveInterval = TimeSpan.FromSeconds(30);   // NAT/zapory ubijają bezczynne sesje
                client.HostKeyReceived += OnHostKey;
                client.ErrorOccurred += (o, e) => RaiseDisconnected(e.Exception?.Message);
                client.Connect();

                StartTunnels(client, server);

                var shell = client.CreateShellStream("xterm-256color",
                    (uint)Math.Max(cols, 10), (uint)Math.Max(rows, 5), 0, 0, 8192);
                shell.DataReceived += OnShellData;
                shell.Closed += (o, e) => RaiseDisconnected(null);

                _client = client;
                _shell = shell;
                RaiseConnected();
            });
        }

        // Wspólne dla terminala i SFTP (osobne łącza TCP na tych samych poświadczeniach).
        private ConnectionInfo BuildConnectionInfo(ServerInfo server, string password)
        {
            var methods = new List<AuthenticationMethod>();
            if (!string.IsNullOrWhiteSpace(server.PrivateKeyPath))
                methods.Add(new PrivateKeyAuthenticationMethod(server.Username,
                    LoadPrivateKey(server.PrivateKeyPath)));
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

        /// <summary>
        /// Nowe połączenie SFTP na poświadczeniach tej sesji (wątek roboczy; osobne łącze TCP,
        /// ten sam mechanizm weryfikacji klucza hosta — po TOFU terminala przechodzi cicho).
        /// </summary>
        public SftpClient CreateSftpClient()
        {
            var server = _server;
            if (server == null) throw new InvalidOperationException("Sesja SSH nie była jeszcze łączona.");
            var c = new SftpClient(BuildConnectionInfo(server, _password));
            c.HostKeyReceived += OnHostKey;
            c.Connect();
            return c;
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

        // Tunele lokalne (ssh -L): 127.0.0.1:portLokalny → host:portZdalny przez serwer SSH.
        // Błąd jednej reguły nie ubija sesji — leci zdarzeniem do UI.
        private void StartTunnels(SshClient client, ServerInfo server)
        {
            if (server.Tunnels == null) return;
            foreach (var spec in server.Tunnels)
            {
                if (!TunnelSpec.TryParse(spec, out int lp, out string host, out int rp)) continue;   // walidacja w edytorze
                try
                {
                    var fp = new ForwardedPortLocal("127.0.0.1", (uint)lp, host, (uint)rp);
                    client.AddForwardedPort(fp);
                    fp.Exception += (o, e) => TunnelStatus?.Invoke(spec, false, e.Exception?.Message);
                    fp.Start();
                    lock (_forwards) _forwards.Add(fp);
                    TunnelStatus?.Invoke(spec, true, null);
                }
                catch (Exception ex) { TunnelStatus?.Invoke(spec, false, ex.Message); }
            }
        }

        // TOFU: znany klucz → OK; nowy → pytanie (domyślnie ufaj); ZMIENIONY → pytanie z ostrzeżeniem (domyślnie odrzuć).
        private void OnHostKey(object sender, HostKeyEventArgs e)
        {
            try
            {
                string fp = KnownHosts.Fingerprint(e.HostKey);
                var store = KnownHosts.Load(SettingsStore.Dir);
                var status = KnownHosts.Check(store, _hostKeyHost, _hostKeyPort, fp);
                if (status == KnownHosts.Status.Match) { e.CanTrust = true; return; }

                bool changed = status == KnownHosts.Status.Mismatch;
                var ask = TrustHostKey;
                bool trust = ask != null ? ask(_hostKeyHost + ":" + _hostKeyPort, fp, changed) : !changed;
                if (trust)
                {
                    store[KnownHosts.EntryKey(_hostKeyHost, _hostKeyPort)] = fp;
                    KnownHosts.Save(SettingsStore.Dir, store);
                }
                e.CanTrust = trust;
            }
            catch { e.CanTrust = false; }   // wątpliwość = odmowa (bezpieczny domyślny)
        }

        private void OnShellData(object sender, ShellDataEventArgs e)
        {
            if (IsTerminalDisposed || e.Data == null || e.Data.Length == 0) return;
            string text;
            lock (_utf8)   // Decoder jest stanowy — dostęp z jednego wątku naraz
            {
                var chars = new char[Encoding.UTF8.GetMaxCharCount(e.Data.Length)];
                int n = _utf8.GetChars(e.Data, 0, e.Data.Length, chars, 0);
                if (n == 0) return;
                text = new string(chars, 0, n);
            }
            PostToTerminal(text);
        }

        /// <summary>Rozłącza w tle; zdarzenie Disconnected przyjdzie z warstwy SSH.</summary>
        public override void Disconnect()
        {
            var c = _client;
            Task.Run(() => { try { c?.Disconnect(); } catch { } });
        }

        // window-change do PTY — pełnoekranowe aplikacje (htop, vim) dostają nowy rozmiar.
        // SSH.NET nie wystawia tego publicznie (metoda żyje na wewnętrznym kanale ShellStream),
        // więc wołamy przez refleksję; brak/zmiana API = cichy brak resize'u, nic się nie psuje.
        private void TryResizePty(int cols, int rows)
        {
            var sh = _shell;
            if (sh == null) return;
            try
            {
                var ch = sh.GetType()
                    .GetField("_channel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.GetValue(sh);
                var m = ch?.GetType().GetMethod("SendWindowChangeRequest");
                m?.Invoke(ch, new object[] { (uint)Math.Max(cols, 10), (uint)Math.Max(rows, 5), 0u, 0u });
            }
            catch { /* shell w trakcie zamykania albo inna wersja SSH.NET */ }
        }

        // ---------- Sprzątanie ----------

        private void DisposeClient()
        {
            var sh = _shell; var c = _client;
            _shell = null; _client = null;
            ForwardedPortLocal[] fws;
            lock (_forwards) { fws = _forwards.ToArray(); _forwards.Clear(); }
            foreach (var f in fws) { try { f.Stop(); f.Dispose(); } catch { } }
            try { if (sh != null) { sh.DataReceived -= OnShellData; sh.Dispose(); } } catch { }
            try { c?.Dispose(); } catch { }
        }

        /// <summary>Pełne sprzątanie przy zamknięciu karty/aplikacji (SSH + SFTP + WebView2).</summary>
        public override void DisposeTerminal()
        {
            MarkDisposed();
            DisposeClient();
            try { _files?.DisposePanel(); } catch { }
            base.DisposeTerminal();
        }
    }
}
