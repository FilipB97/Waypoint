using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace RdpManager
{
    /// <summary>
    /// Baza terminali tekstowych: WebView2 + xterm.js (assety osadzone w exe, wstrzykiwane inline —
    /// offline, bez CDN) i mostek JS↔C#. Pochodne dostarczają transport (SSH / Telnet / Serial):
    /// klawisze przychodzą w <see cref="OnTerminalInput"/>, dane z transportu wypycha
    /// <see cref="PostToTerminal"/>, stan zgłaszają <see cref="RaiseConnected"/> /
    /// <see cref="RaiseDisconnected"/>. Kopiowanie zaznaczeniem, Ctrl+Shift+C/V
    /// i Ctrl+kółko (rozmiar czcionki) są wspólne dla wszystkich terminali.
    /// </summary>
    public abstract class XtermControl : Border
    {
        /// <summary>Kontrolka WebView2 — pochodna może ją przełożyć do własnego layoutu (np. SFTP obok).</summary>
        protected readonly WebView2 Web = new WebView2();

        private TaskCompletionSource<(int Cols, int Rows)> _ready;
        private int _down = 1;             // 1 = brak aktywnego połączenia — Disconnected nie poleci
        private volatile bool _disposed;

        /// <summary>Połączono — transport gotowy (wątek roboczy).</summary>
        public event Action Connected;
        /// <summary>Rozłączono; parametr = powód (null = zwykłe zamknięcie). Wątek roboczy.</summary>
        public event Action<string> Disconnected;

        protected bool IsTerminalDisposed => _disposed;

        protected XtermControl()
        {
            Child = Web;
            // Tło od pierwszej klatki dopasowane do motywu apki (bez błysku złego koloru przy starcie WebView2).
            Web.DefaultBackgroundColor = ThemeManager.IsLight
                ? System.Drawing.Color.FromArgb(255, 255, 255)
                : System.Drawing.Color.FromArgb(16, 18, 22);
        }

        /// <summary>Klawisze z xterm (wątek UI). Pochodna pisze do swojego transportu.</summary>
        protected abstract void OnTerminalInput(string data);

        /// <summary>Zmiana rozmiaru terminala (kolumny/wiersze) — np. window-change do PTY.</summary>
        protected virtual void OnTerminalResize(int cols, int rows) { }

        /// <summary>Rozłącza transport (Disconnected przyjdzie z warstwy transportu).</summary>
        public abstract void Disconnect();

        // ---------- Inicjalizacja WebView2 + xterm ----------

        /// <summary>
        /// Inicjalizuje WebView2 i xterm; zwraca wynegocjowany rozmiar (kolumny/wiersze).
        /// Wielokrotne wywołanie (rekonekt) zwraca zapamiętany wynik.
        /// </summary>
        public async Task<(int Cols, int Rows)> InitAsync()
        {
            if (_ready != null) return await _ready.Task;
            _ready = new TaskCompletionSource<(int, int)>(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                await WaitLoadedAsync();

                // Folder danych WebView2 w %APPDATA%\RdpManager — obok exe może być tylko-do-odczytu.
                var env = await CoreWebView2Environment.CreateAsync(null,
                    Path.Combine(SettingsStore.Dir, "webview2"));
                await Web.EnsureCoreWebView2Async(env);

                var s = Web.CoreWebView2.Settings;
                s.AreDefaultContextMenusEnabled = false;
                s.AreDevToolsEnabled = false;
                s.IsStatusBarEnabled = false;
                s.IsZoomControlEnabled = false;

                Web.CoreWebView2.WebMessageReceived += OnWebMessage;
                Web.CoreWebView2.NavigateToString(BuildHtml());

                return await _ready.Task;
            }
            catch
            {
                // Inicjalizacja padła (np. brak runtime WebView2 / zablokowany folder danych). Wyzeruj _ready,
                // żeby ponowna próba (przycisk „Połącz ponownie") re-inicjalizowała, zamiast czekać w
                // nieskończoność na TaskCompletionSource, który nigdy się nie ukończy.
                _ready = null;
                throw;
            }
        }

        // WebView2 tworzy HWND dopiero po wejściu do drzewa — poczekaj na Loaded.
        private async Task WaitLoadedAsync()
        {
            if (Web.IsLoaded) return;
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            RoutedEventHandler h = null;
            h = (o, e) => { Web.Loaded -= h; tcs.TrySetResult(true); };
            Web.Loaded += h;
            if (Web.IsLoaded) { Web.Loaded -= h; return; }   // wyścig: załadowało się między sprawdzeniem a subskrypcją
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
                    case "in":   // klawisze z xterm → transport pochodnej
                        var data = root.GetProperty("d").GetString();
                        if (!string.IsNullOrEmpty(data)) OnTerminalInput(data);
                        break;
                    case "size":
                        OnTerminalResize(root.GetProperty("c").GetInt32(), root.GetProperty("r").GetInt32());
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
                                    Web.CoreWebView2?.PostWebMessageAsJson(
                                        JsonSerializer.Serialize(new { t = "paste", d = txt }));
                            }
                            catch { }
                        }));
                        break;
                }
            }
            catch { /* uszkodzona wiadomość — ignoruj */ }
        }

        // ---------- Zdarzenia stanu ----------

        /// <summary>Zgłasza Connected i uzbraja pojedyncze Disconnected dla tego połączenia.</summary>
        protected void RaiseConnected()
        {
            if (_disposed) return;   // połączenie dobiło po zamknięciu karty — nie ożywiaj martwej sesji
            Interlocked.Exchange(ref _down, 0);
            Connected?.Invoke();
        }

        /// <summary>Zgłasza Disconnected raz na połączenie (transporty potrafią zgłosić koniec dwiema drogami).</summary>
        protected void RaiseDisconnected(string reason)
        {
            if (_disposed) return;
            if (Interlocked.Exchange(ref _down, 1) == 1) return;
            Disconnected?.Invoke(reason);
        }

        // ---------- Wyjście do terminala ----------

        /// <summary>Wypycha tekst (może zawierać ANSI) do xterm — z dowolnego wątku.</summary>
        protected void PostToTerminal(string text)
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!_disposed) Web.CoreWebView2?.PostWebMessageAsString(text);
                }));
            }
            catch { /* dispatcher w trakcie zamykania */ }
        }

        /// <summary>Lokalny komunikat do terminala (status łączenia itp.) — NIE idzie do transportu.</summary>
        public void WriteLocal(string text) => PostToTerminal(text);

        public void FocusTerminal()
        {
            try { Web.Focus(); } catch { }
        }

        // ---------- Sprzątanie ----------

        /// <summary>Ustawia flagę końca życia — zatrzymuje wypychanie do WebView2 (wołaj PRZED sprzątaniem transportu).</summary>
        protected void MarkDisposed() => _disposed = true;

        /// <summary>Sprzątanie przy zamknięciu karty/aplikacji. Pochodne dokładają swój transport i wołają bazę.</summary>
        public virtual void DisposeTerminal()
        {
            _disposed = true;
            try { Web.Dispose(); } catch { }
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

            // Terminal (WebView2/xterm.js) żyje poza drzewem zasobów WPF — nie widzi DynamicResource,
            // więc motyw i rozmiar czcionki trzeba wstrzyknąć raz, przy budowie strony (D5 z przeglądu).
            bool light = ThemeManager.IsLight;
            string pageBg = light ? "#FFFFFF" : "#101216";
            string xtermTheme = light
                ? "{ background:'#FFFFFF', foreground:'#1B1D22', cursor:'#2657D6', selectionBackground:'#B8CBFA' }"
                : "{ background:'#101216', foreground:'#D6D8DC', cursor:'#2657D6', selectionBackground:'#2A3A66' }";
            int fontSize = Math.Min(24, Math.Max(8, SettingsStore.Load().TerminalFontSize));

            var sb = new StringBuilder(400_000);
            sb.Append("<!doctype html><html><head><meta charset='utf-8'><style>")
              .Append(css)
              .Append("html,body{margin:0;padding:0;height:100%;background:").Append(pageBg).Append(";overflow:hidden}#t{height:100%}")
              .Append("</style><script>").Append(js)
              .Append("</script><script>").Append(fit)
              .Append("</script></head><body><div id='t'></div><script>\n")
              .Append(@"
// Buildy UMD raz eksponują klasę, raz moduł { Terminal } — obsłuż obie postaci.
const TermCtor = (typeof Terminal === 'function') ? Terminal : Terminal.Terminal;
const FitCtor  = (typeof FitAddon === 'function') ? FitAddon : FitAddon.FitAddon;
const term = new TermCtor({
  fontFamily: 'Cascadia Code, Cascadia Mono, Consolas, monospace',
  fontSize: ").Append(fontSize).Append(@", cursorBlink: true, scrollback: 5000,
  theme: ").Append(xtermTheme).Append(@"
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
