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
        private readonly SshConnectionFactory _conn = new SshConnectionFactory();   // budowa łącza + TOFU (wspólne z SFTP)

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
        private FileTransferPanel _files;

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
                _files = new FileTransferPanel(() => _conn.NewFs());
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
                lock (_utf8) _utf8.Reset();   // porzuć niedokończony znak wielobajtowy z poprzedniej sesji

                _conn.TrustHostKey = TrustHostKey;
                _conn.RequestKeyPassphrase = RequestKeyPassphrase;
                _conn.SetIdentity(server, password);
                var ci = _conn.BuildConnectionInfo();

                var client = new SshClient(ci);
                client.KeepAliveInterval = TimeSpan.FromSeconds(30);   // NAT/zapory ubijają bezczynne sesje
                _conn.Attach(client);
                client.ErrorOccurred += (o, e) => RaiseDisconnected(e.Exception?.Message);
                client.Connect();

                StartTunnels(client, server);

                var shell = client.CreateShellStream("xterm-256color",
                    (uint)Math.Max(cols, 10), (uint)Math.Max(rows, 5), 0, 0, 8192);
                shell.DataReceived += OnShellData;
                shell.Closed += (o, e) => RaiseDisconnected(null);

                // Karta mogła zostać zamknięta w trakcie łączenia — wtedy DisposeClient już przebiegł
                // i nikt nie zwolniłby tych zasobów. Sprzątnij i nie ożywiaj sesji.
                if (IsTerminalDisposed)
                {
                    try { shell.Dispose(); } catch { }
                    try { client.Dispose(); } catch { }
                    return;
                }

                _client = client;
                _shell = shell;
                RaiseConnected();
            });
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
