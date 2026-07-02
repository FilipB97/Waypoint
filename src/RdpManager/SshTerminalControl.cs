using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Renci.SshNet;
using Renci.SshNet.Common;
using RdpManager.Core;
using RdpManager.Models;

namespace RdpManager
{
    /// <summary>
    /// Wbudowany terminal SSH: WebView2 + xterm.js (assety osadzone w exe, wstrzykiwane inline —
    /// offline, bez CDN) + SSH.NET (ShellStream). Kontrolka żyje w kontenerze sesji obok hostów RDP;
    /// przełączanie kart to zmiana Visibility, jak przy RDP.
    /// Klawisze: xterm.onData → postMessage → ShellStream.Write; dane z serwera → term.write.
    /// </summary>
    public class SshTerminalControl : Border
    {
        private readonly WebView2 _web = new WebView2();
        private SshClient _client;
        private ShellStream _shell;
        private readonly Decoder _utf8 = Encoding.UTF8.GetDecoder();   // stanowy — skleja rozcięte znaki wielobajtowe
        private TaskCompletionSource<(int Cols, int Rows)> _ready;
        private int _down;                 // 1 = Disconnected już zgłoszone (ErrorOccurred i Closed potrafią przyjść oba)
        private volatile bool _disposed;

        /// <summary>Połączono i shell gotowy (zdarzenie przychodzi z wątku roboczego).</summary>
        public event Action Connected;
        /// <summary>Rozłączono; parametr = opis powodu (może być null). Wątek roboczy.</summary>
        public event Action<string> Disconnected;

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
            // terminal | splitter | panel plików SFTP (domyślnie zwinięty)
            _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _grid.ColumnDefinitions.Add(_splitCol);
            _grid.ColumnDefinitions.Add(_filesCol);
            Grid.SetColumn(_web, 0);
            Grid.SetColumn(_split, 1);
            _grid.Children.Add(_web);
            _grid.Children.Add(_split);
            Child = _grid;

            // Ciemne tło od pierwszej klatki (bez białego błysku WebView2).
            _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(16, 18, 22);
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

        // ---------- Inicjalizacja WebView2 + xterm ----------

        /// <summary>
        /// Inicjalizuje WebView2 i xterm; zwraca wynegocjowany rozmiar terminala (kolumny/wiersze).
        /// Wielokrotne wywołanie (rekonekt) zwraca zapamiętany wynik.
        /// </summary>
        public async Task<(int Cols, int Rows)> InitAsync()
        {
            if (_ready != null) return await _ready.Task;
            _ready = new TaskCompletionSource<(int, int)>(TaskCreationOptions.RunContinuationsAsynchronously);

            await WaitLoadedAsync();

            // Folder danych WebView2 w %APPDATA%\RdpManager — obok exe może być tylko-do-odczytu.
            var env = await CoreWebView2Environment.CreateAsync(null,
                Path.Combine(SettingsStore.Dir, "webview2"));
            await _web.EnsureCoreWebView2Async(env);

            var s = _web.CoreWebView2.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.AreDevToolsEnabled = false;
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = false;

            _web.CoreWebView2.WebMessageReceived += OnWebMessage;
            _web.CoreWebView2.NavigateToString(BuildHtml());

            return await _ready.Task;
        }

        // WebView2 tworzy HWND dopiero po wejściu do drzewa — poczekaj na Loaded.
        private async Task WaitLoadedAsync()
        {
            if (_web.IsLoaded) return;
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            RoutedEventHandler h = null;
            h = (o, e) => { _web.Loaded -= h; tcs.TrySetResult(true); };
            _web.Loaded += h;
            if (_web.IsLoaded) { _web.Loaded -= h; return; }   // wyścig: załadowało się między sprawdzeniem a subskrypcją
            await tcs.Task;
        }

        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                var root = doc.RootElement;
                switch (root.GetProperty("t").GetString())
                {
                    case "ready":
                        _ready?.TrySetResult((root.GetProperty("c").GetInt32(), root.GetProperty("r").GetInt32()));
                        break;
                    case "in":   // klawisze z xterm → do shella
                        var data = root.GetProperty("d").GetString();
                        var sh = _shell;
                        if (sh != null && !string.IsNullOrEmpty(data))
                        {
                            try { sh.Write(data); sh.Flush(); } catch { /* shell w trakcie zamykania */ }
                        }
                        break;
                    case "size": // zmiana rozmiaru okna → window-change do PTY (jeśli ta wersja SSH.NET je wystawia)
                        TryResizePty(root.GetProperty("c").GetInt32(), root.GetProperty("r").GetInt32());
                        break;
                    case "copy": // zaznaczenie / Ctrl+Shift+C → schowek Windows
                        var sel = root.GetProperty("d").GetString();
                        if (!string.IsNullOrEmpty(sel))
                            Dispatcher.BeginInvoke(new Action(() => { try { Clipboard.SetText(sel); } catch { } }));
                        break;
                    case "paste": // Ctrl+Shift+V → tekst ze schowka do terminala (JSON = kanał sterujący)
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                string txt = Clipboard.GetText();
                                if (!string.IsNullOrEmpty(txt) && !_disposed)
                                    _web.CoreWebView2?.PostWebMessageAsJson(
                                        JsonSerializer.Serialize(new { t = "paste", d = txt }));
                            }
                            catch { }
                        }));
                        break;
                }
            }
            catch { /* uszkodzona wiadomość — ignoruj */ }
        }

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
                Interlocked.Exchange(ref _down, 0);

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
                Connected?.Invoke();
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
            if (_disposed || e.Data == null || e.Data.Length == 0) return;
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

        private void RaiseDisconnected(string reason)
        {
            if (_disposed) return;
            if (Interlocked.Exchange(ref _down, 1) == 1) return;   // tylko raz na połączenie
            Disconnected?.Invoke(reason);
        }

        /// <summary>Rozłącza w tle; zdarzenie Disconnected przyjdzie z warstwy SSH.</summary>
        public void Disconnect()
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

        // ---------- Wyjście do terminala ----------

        private void PostToTerminal(string text)
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!_disposed) _web.CoreWebView2?.PostWebMessageAsString(text);
                }));
            }
            catch { /* dispatcher w trakcie zamykania */ }
        }

        /// <summary>Lokalny komunikat do terminala (status łączenia itp.) — NIE idzie do serwera.</summary>
        public void WriteLocal(string text) => PostToTerminal(text);

        public void FocusTerminal()
        {
            try { _web.Focus(); } catch { }
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
        public void DisposeTerminal()
        {
            _disposed = true;
            DisposeClient();
            try { _files?.DisposePanel(); } catch { }
            try { _web.Dispose(); } catch { }
        }

        // ---------- HTML (xterm inline) ----------

        private static string ReadAsset(string name)
        {
            var uri = new Uri("pack://application:,,,/Assets/xterm/" + name);
            using (var s = Application.GetResourceStream(uri).Stream)
            using (var r = new StreamReader(s, Encoding.UTF8))
                return r.ReadToEnd();
        }

        private static string BuildHtml()
        {
            // "</script>" wewnątrz inline-skryptu urwałby dokument — wymagane escapowanie.
            string css = ReadAsset("xterm.css");
            string js = ReadAsset("xterm.js").Replace("</script>", "<\\/script>");
            string fit = ReadAsset("addon-fit.js").Replace("</script>", "<\\/script>");

            var sb = new StringBuilder(400_000);
            sb.Append("<!doctype html><html><head><meta charset='utf-8'><style>")
              .Append(css)
              .Append("html,body{margin:0;padding:0;height:100%;background:#101216;overflow:hidden}#t{height:100%}")
              .Append("</style><script>").Append(js)
              .Append("</script><script>").Append(fit)
              .Append("</script></head><body><div id='t'></div><script>\n")
              .Append(@"
// Buildy UMD raz eksponują klasę, raz moduł { Terminal } — obsłuż obie postaci.
const TermCtor = (typeof Terminal === 'function') ? Terminal : Terminal.Terminal;
const FitCtor  = (typeof FitAddon === 'function') ? FitAddon : FitAddon.FitAddon;
const term = new TermCtor({
  fontFamily: 'Cascadia Code, Cascadia Mono, Consolas, monospace',
  fontSize: 14, cursorBlink: true, scrollback: 5000,
  theme: { background:'#101216', foreground:'#D6D8DC', cursor:'#29C5D6',
           selectionBackground:'#2A4A50' }
});
const fit = new FitCtor();
term.loadAddon(fit);
term.open(document.getElementById('t'));
fit.fit();
term.onData(d => window.chrome.webview.postMessage({ t:'in', d:d }));
// Kanały z C#: PostWebMessageAsString = wyjście terminala; PostWebMessageAsJson = sterowanie (paste).
window.chrome.webview.addEventListener('message', e => {
  if (typeof e.data === 'string') term.write(e.data);
  else if (e.data && e.data.t === 'paste') term.paste(e.data.d || '');
});
// Ctrl+Shift+C/V = kopiuj/wklej (zwykłe Ctrl+C musi zostać SIGINT-em).
term.attachCustomKeyEventHandler(ev => {
  if (ev.type !== 'keydown') return true;
  if (ev.ctrlKey && ev.shiftKey && ev.code === 'KeyC') {
    const s = term.getSelection();
    if (s) window.chrome.webview.postMessage({ t:'copy', d:s });
    return false;
  }
  if (ev.ctrlKey && ev.shiftKey && ev.code === 'KeyV') {
    window.chrome.webview.postMessage({ t:'paste' });
    return false;
  }
  return true;
});
// Kopiowanie samym zaznaczeniem (styl PuTTY), z małym opóźnieniem.
let selT = null;
term.onSelectionChange(() => {
  clearTimeout(selT);
  selT = setTimeout(() => {
    const s = term.getSelection();
    if (s) window.chrome.webview.postMessage({ t:'copy', d:s });
  }, 250);
});
// Ctrl+kółko = rozmiar czcionki terminala (8-24).
document.addEventListener('wheel', ev => {
  if (!ev.ctrlKey) return;
  ev.preventDefault();
  const fs = Math.min(24, Math.max(8, term.options.fontSize + (ev.deltaY < 0 ? 1 : -1)));
  if (fs !== term.options.fontSize) {
    term.options.fontSize = fs;
    fit.fit();
    window.chrome.webview.postMessage({ t:'size', c:term.cols, r:term.rows });
  }
}, { passive: false });
let rt = null;
window.addEventListener('resize', () => {
  clearTimeout(rt);
  rt = setTimeout(() => { fit.fit(); window.chrome.webview.postMessage({ t:'size', c:term.cols, r:term.rows }); }, 150);
});
window.chrome.webview.postMessage({ t:'ready', c:term.cols, r:term.rows });
term.focus();
")
              .Append("</script></body></html>");
            return sb.ToString();
        }
    }
}
