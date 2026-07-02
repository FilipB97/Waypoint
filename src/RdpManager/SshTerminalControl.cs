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

        public SshTerminalControl()
        {
            Child = _web;
            // Ciemne tło od pierwszej klatki (bez białego błysku WebView2).
            _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(16, 18, 22);
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

                var methods = new List<AuthenticationMethod>();
                if (!string.IsNullOrWhiteSpace(server.PrivateKeyPath))
                    methods.Add(new PrivateKeyAuthenticationMethod(server.Username,
                        new PrivateKeyFile(server.PrivateKeyPath)));
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

                var ci = new ConnectionInfo(server.Host, server.Port > 0 ? server.Port : 22,
                                            server.Username, methods.ToArray())
                {
                    Timeout = TimeSpan.FromSeconds(15),
                    Encoding = Encoding.UTF8
                };

                var client = new SshClient(ci);
                // MVP: akceptujemy klucz hosta bez weryfikacji (do zrobienia: odcisk + known_hosts).
                client.HostKeyReceived += (o, e) => e.CanTrust = true;
                client.ErrorOccurred += (o, e) => RaiseDisconnected(e.Exception?.Message);
                client.Connect();

                var shell = client.CreateShellStream("xterm-256color",
                    (uint)Math.Max(cols, 10), (uint)Math.Max(rows, 5), 0, 0, 8192);
                shell.DataReceived += OnShellData;
                shell.Closed += (o, e) => RaiseDisconnected(null);

                _client = client;
                _shell = shell;
                Connected?.Invoke();
            });
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

        // window-change: SSH.NET nie wystawia stabilnego publicznego API — użyj przez refleksję, jeśli jest.
        private void TryResizePty(int cols, int rows)
        {
            var sh = _shell;
            if (sh == null) return;
            try
            {
                var m = sh.GetType().GetMethod("SendWindowChangeRequest",
                    new[] { typeof(uint), typeof(uint), typeof(uint), typeof(uint) });
                m?.Invoke(sh, new object[] { (uint)Math.Max(cols, 10), (uint)Math.Max(rows, 5), 0u, 0u });
            }
            catch { /* brak API w tej wersji — terminal działa dalej ze starym rozmiarem PTY */ }
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
            try { if (sh != null) { sh.DataReceived -= OnShellData; sh.Dispose(); } } catch { }
            try { c?.Dispose(); } catch { }
        }

        /// <summary>Pełne sprzątanie przy zamknięciu karty/aplikacji (SSH + WebView2).</summary>
        public void DisposeTerminal()
        {
            _disposed = true;
            DisposeClient();
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
window.chrome.webview.addEventListener('message', e => term.write(e.data));
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
