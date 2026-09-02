using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RdpManager.Core;

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
            SetBackdrop(CurrentTheme());
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
                    case "link": // kliknięty adres w wyjściu terminala
                        var url = root.GetProperty("d").GetString();
                        Dispatcher.BeginInvoke(new Action(() => OpenLinkChecked(url)));
                        break;
                    case "transcript": // zrzut bufora przewijania → plik
                        var text = root.GetProperty("d").GetString();
                        Dispatcher.BeginInvoke(new Action(() => WriteTranscriptFile(text)));
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

        /// <summary>
        /// Pasek szukania rysowany W DOKUMENCIE terminala, nie w WPF. Powód jest ten sam co przy arkuszu
        /// skrótów: WebView2 to osobne okno (HWND), więc cokolwiek WPF narysuje nad terminalem, zostanie
        /// przez niego zasłonięte. Wewnątrz strony problemu nie ma.
        /// Teksty idą z tego samego słownika co reszta interfejsu — strona powstaje w C#, więc nie ma
        /// powodu, żeby terminal był jedynym miejscem w aplikacji mówiącym wyłącznie po polsku.
        /// </summary>
        /// <summary>
        /// Zamienia tekst na literał JS wraz z cudzysłowami. Osobno od HtmlEncode, bo to inny kontekst:
        /// tu chodzi o to, żeby apostrof w tłumaczeniu (albo cokolwiek innego) nie rozwalił skryptu.
        /// JsonSerializer robi dokładnie to, czego trzeba, i ucieka też znaki spoza ASCII.
        /// </summary>
        /// <summary>Kolory podświetleń wyszukiwania — akcent na trafieniu aktywnym, przygaszony wariant
        /// na pozostałych. Z palety, więc idą za presetem i własnym akcentem.</summary>
        private static Dictionary<string, string> SearchDecorationMap(TerminalTheme t)
        {
            // Trafienie aktywne pełnym akcentem, pozostałe tym samym akcentem przygaszonym — jeden
            // kolor w dwóch natężeniach, więc podąża za presetem i własnym akcentem użytkownika.
            return new Dictionary<string, string>
            {
                ["activeMatchBackground"] = t.Accent,
                ["activeMatchColorOverviewRuler"] = t.Accent,
                ["matchBackground"] = t.Selection,
                ["matchOverviewRuler"] = t.Selection
            };
        }

        // Ta sama mapa raz wstrzykiwana do strony, raz wysyłana przy przemalowaniu — żeby podświetlenia
        // wyszukiwania nie zostały na akcencie sprzed zmiany motywu.
        private static string SearchDecorations(TerminalTheme t)
            => JsonSerializer.Serialize(SearchDecorationMap(t));

        /// <summary>Motyw z ŻYWEJ palety — patrz Core/TerminalTheme (terminal nie widzi DynamicResource).</summary>
        private static TerminalTheme CurrentTheme()
            => TerminalTheme.From(Core.PaletteColors.Of, ThemeManager.IsLight);

        /// <summary>
        /// Przemalowuje OTWARTY terminal po zmianie motywu, presetu albo akcentu. Bez tego zmiana
        /// dotyczyła tylko nowo otwieranych sesji, bo strona wstrzykuje motyw przy budowie.
        /// </summary>
        public void ApplyTheme()
        {
            if (_disposed) return;
            var t = CurrentTheme();
            var msg = new Dictionary<string, object>
            {
                ["t"] = "theme",
                ["xterm"] = new Dictionary<string, string>
                {
                    ["background"] = t.Background,
                    ["foreground"] = t.Foreground,
                    ["cursor"] = t.Cursor,
                    ["selectionBackground"] = t.Selection
                },
                ["vars"] = t.CssVars(),
                ["deco"] = SearchDecorationMap(t)
            };
            try
            {
                SetBackdrop(t);
                Web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(msg));
            }
            catch { /* strona jeszcze nie żyje albo już zamknięta */ }
        }

        /// <summary>
        /// Tło samego okna WebView2 — widać je przez ułamek sekundy przed wczytaniem strony i podczas
        /// zmiany rozmiaru, zanim strona się przerysuje. Bierzemy je z tego samego motywu co tło terminala,
        /// więc przy starcie i przy przełączeniu motywu nie mruga kolorem poprzedniej palety.
        /// </summary>
        private void SetBackdrop(TerminalTheme t)
        {
            var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(t.Background);
            Web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(c.R, c.G, c.B);
        }

        private static string JsStr(string value) => JsonSerializer.Serialize(value ?? "");

        private static string SearchBarHtml()
        {
            string E(string k) => System.Net.WebUtility.HtmlEncode(LocalizationManager.S(k));
            return
                "<div id='sb' hidden>" +
                "<input id='sq' type='text' spellcheck='false' placeholder='" + E("S.term.find.ph") + "'>" +
                "<span id='sn'></span>" +
                "<button id='sp' title='" + E("S.term.find.prev") + "'>&#x2191;</button>" +
                "<button id='snx' title='" + E("S.term.find.next") + "'>&#x2193;</button>" +
                "<button id='sx' title='" + E("S.term.find.close") + "'>&#x2715;</button>" +
                "</div>";
        }

        /// <summary>Otwiera pasek szukania w buforze (menu karty / skrót).</summary>
        public void OpenFind() => PostControl("find");

        /// <summary>Prosi stronę o zrzut bufora; wynik wraca komunikatem „transcript" i trafia do pliku.</summary>
        public void SaveTranscript() => PostControl("serialize");

        private void PostControl(string t)
        {
            if (_disposed) return;
            try { Web.CoreWebView2?.PostWebMessageAsJson("{\"t\":\"" + t + "\"}"); } catch { }
        }

        /// <summary>
        /// Otwiera adres kliknięty W WYJŚCIU TERMINALA. Ta treść pochodzi ze zdalnego serwera, więc
        /// przechodzi przez UrlValidation: bez tego wystarczyłoby, żeby serwer wypisał coś, co xterm
        /// uzna za adres w schemacie obsługiwanym przez zarejestrowany handler, i ShellExecute
        /// uruchomiłby na tej maszynie program — bez pytania. Przepuszczamy wyłącznie http/https.
        /// </summary>
        private static void OpenLinkChecked(string raw)
        {
            if (!Core.UrlValidation.TryNormalizeWebUrl(raw, out var uri)) return;
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch { }
        }

        /// <summary>Zapisuje transkrypt sesji do pliku wskazanego przez użytkownika.</summary>
        private static void WriteTranscriptFile(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = "waypoint-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt",
                DefaultExt = ".txt",
                Filter = "Tekst (*.txt)|*.txt|Wszystkie pliki (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true) return;
            // Zrzut z serialize niesie sekwencje sterujące (kolory); do pliku tekstowego idą surowo —
            // tak samo zachowuje się `script`, a każdy pager i tak je rozumie.
            try { File.WriteAllText(dlg.FileName, text, Encoding.UTF8); } catch { }
        }

        private static string ReadAsset(string name)
        {
            var uri = new Uri("pack://application:,,,/Assets/xterm/" + name);
            using (var s = Application.GetResourceStream(uri).Stream)
            using (var r = new StreamReader(s, Encoding.UTF8))
                return r.ReadToEnd();
        }

        /// <summary>Style paska szukania — dopasowane do palety Waypointa (promienie z drabiny 8/12,
        /// akcent marki, tekst na progu czytelności).</summary>
        // Wyłącznie ZMIENNE CSS — żadnych kolorów wprost. Wartości ustawia skrypt strony przy starcie
        // i przy każdym przemalowaniu (ApplyTheme), więc zmiana motywu nie wymaga przeładowania.
        private static string SearchBarCss() =>
                "#sb{position:fixed;top:8px;right:14px;display:flex;align-items:center;gap:6px;padding:6px 8px;" +
                "border-radius:12px;background:var(--wp-panel);border:1px solid var(--wp-border);" +
                "box-shadow:0 6px 24px rgba(0,0,0,.35);font-family:'Segoe UI',system-ui,sans-serif;z-index:9}" +
                "#sb[hidden]{display:none}" +
                "#sq{width:190px;height:26px;border-radius:8px;border:2px solid transparent;" +
                "background:var(--wp-input);color:var(--wp-tx);padding:0 8px;font-size:12px;outline:none}" +
                "#sq:focus{border-color:var(--wp-accent)}" +
                "#sq::placeholder{color:var(--wp-tx3)}" +
                "#sn{font-size:11px;color:var(--wp-tx3);min-width:52px;text-align:center;font-variant-numeric:tabular-nums}" +
                "#sb button{width:24px;height:24px;border:0;border-radius:8px;background:transparent;color:var(--wp-tx3);" +
                "cursor:pointer;font-size:12px;line-height:1}" +
                "#sb button:hover{background:var(--wp-hover);color:var(--wp-tx)}";

        private static string BuildHtml()
        {
            // "</script>" wewnątrz inline-skryptu urwałby dokument — wymagane escapowanie.
            string css = ReadAsset("xterm.css");
            string js = ReadAsset("xterm.js").Replace("</script>", "<\\/script>");
            string fit = ReadAsset("addon-fit.js").Replace("</script>", "<\\/script>");
            // Dodatki xterm.js 5.3.0 (zgodne: peerDependency xterm ^5.0.0), osadzone jak reszta — offline:
            //   search    — szukanie w buforze przewijania (Ctrl+Shift+F),
            //   web-links — adresy w wyjściu terminala stają się klikalne,
            //   serialize — zrzut bufora do tekstu (zapis transkryptu sesji).
            string search = ReadAsset("addon-search.js").Replace("</script>", "<\\/script>");
            string links = ReadAsset("addon-web-links.js").Replace("</script>", "<\\/script>");
            string serial = ReadAsset("addon-serialize.js").Replace("</script>", "<\\/script>");

            // Terminal (WebView2/xterm.js) żyje poza drzewem zasobów WPF — nie widzi DynamicResource,
            // więc motyw wstrzykujemy przy budowie strony, a późniejsze zmiany dosyłamy wiadomością
            // (ApplyTheme). Rozmiar czcionki zostaje przy budowie: xterm musi wtedy przeliczyć siatkę.
            var theme = CurrentTheme();
            string xtermTheme = theme.ToXtermObject();
            int fontSize = Math.Min(24, Math.Max(8, SettingsStore.Load().TerminalFontSize));

            var sb = new StringBuilder(400_000);
            sb.Append("<!doctype html><html><head><meta charset='utf-8'><style>")
              .Append(css)
              .Append(":root{").Append(string.Join(";", theme.CssVars().Select(kv => kv.Key + ":" + kv.Value))).Append("}")
              .Append("html,body{margin:0;padding:0;height:100%;background:var(--wp-bg);overflow:hidden}#t{height:100%}")
              .Append(SearchBarCss())
              .Append("</style><script>").Append(js)
              .Append("</script><script>").Append(fit)
              .Append("</script><script>").Append(search)
              .Append("</script><script>").Append(links)
              .Append("</script><script>").Append(serial)
              .Append("</script></head><body><div id='t'></div>")
              .Append(SearchBarHtml())
              .Append("<script>\n")
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

// Dodatki: szukanie, klikalne adresy, zrzut bufora. Każdy build UMD eksponuje się dwojako — jak wyżej.
const SearchCtor = (typeof SearchAddon === 'function') ? SearchAddon : SearchAddon.SearchAddon;
const LinksCtor  = (typeof WebLinksAddon === 'function') ? WebLinksAddon : WebLinksAddon.WebLinksAddon;
const SerCtor    = (typeof SerializeAddon === 'function') ? SerializeAddon : SerializeAddon.SerializeAddon;
const searchAddon = new SearchCtor();
const serializeAddon = new SerCtor();
term.loadAddon(searchAddon);
term.loadAddon(serializeAddon);
// Kliknięcie w adres NIE otwiera niczego tutaj — przeglądarkę uruchamia C# i dopiero PO sprawdzeniu
// schematu (UrlValidation: tylko http/https). Wyjście terminala pochodzi ze zdalnego serwera, więc
// „link” wypisany przez serwer to potencjalnie polecenie uruchomienia czegokolwiek na tej maszynie.
term.loadAddon(new LinksCtor((ev, uri) => window.chrome.webview.postMessage({ t:'link', d:uri })));

term.open(document.getElementById('t'));
fit.fit();
term.onData(d => window.chrome.webview.postMessage({ t:'in', d:d }));
// Kanały z C#: PostWebMessageAsString = wyjście terminala; PostWebMessageAsJson = sterowanie (paste).
window.chrome.webview.addEventListener('message', e => {
  if (typeof e.data === 'string') term.write(e.data);
  else if (e.data && e.data.t === 'paste') term.paste(e.data.d || '');
  else if (e.data && e.data.t === 'serialize') window.dispatchEvent(new Event('wp-serialize'));
  else if (e.data && e.data.t === 'find') openSearch();
  else if (e.data && e.data.t === 'theme') applyTheme(e.data);
});
// Przemalowanie bez przeładowania: xterm przyjmuje motyw w locie, a pasek szukania stoi na
// zmiennych CSS, więc wystarczy je podmienić na elemencie głównym.
function applyTheme(m){
  try{
    if(m.xterm) term.options.theme = m.xterm;
    if(m.vars){ var r=document.documentElement.style;
      Object.keys(m.vars).forEach(function(k){ r.setProperty(k, m.vars[k]) }); }
    if(m.deco) opts.decorations = m.deco;
  }catch(err){}
}
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
  if (ev.ctrlKey && ev.shiftKey && ev.code === 'KeyF') { openSearch(); return false; }
  if (ev.key === 'Escape' && !sb.hidden) { closeSearch(); return false; }
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

// ---------- pasek szukania ----------
const NO_MATCH = ").Append(JsStr(LocalizationManager.S("S.term.find.none"))).Append(@";
const OF_SEP = ").Append(JsStr(" " + LocalizationManager.S("S.term.find.of") + " ")).Append(@";
const sb = document.getElementById('sb'), sq = document.getElementById('sq'), sn = document.getElementById('sn');
// Podświetlenia trafień muszą iść za motywem — na jasnym tle ciemnoszare tło trafienia byłoby plamą.
const opts = { decorations: ").Append(SearchDecorations(theme)).Append(@" };
let lastCount = null;
// Licznik trafień jest zdarzeniem, nie wartością zwracaną — addon zgłasza go po każdym wyszukaniu.
if (searchAddon.onDidChangeResults) searchAddon.onDidChangeResults(r => {
  lastCount = r;
  sn.textContent = (!r || r.resultCount === 0) ? NO_MATCH : ((r.resultIndex + 1) + OF_SEP + r.resultCount);
});
function openSearch() {
  sb.hidden = false; sq.focus(); sq.select();
  if (sq.value) searchAddon.findNext(sq.value, opts);
}
function closeSearch() {
  sb.hidden = true; sn.textContent = '';
  if (searchAddon.clearDecorations) searchAddon.clearDecorations();
  term.focus();
}
function find(back) {
  const q = sq.value;
  if (!q) { sn.textContent = ''; return; }
  back ? searchAddon.findPrevious(q, opts) : searchAddon.findNext(q, opts);
}
sq.addEventListener('input', () => find(false));
sq.addEventListener('keydown', ev => {
  if (ev.key === 'Enter') { ev.preventDefault(); find(ev.shiftKey); }
  else if (ev.key === 'Escape') { ev.preventDefault(); closeSearch(); }
});
document.getElementById('snx').onclick = () => find(false);
document.getElementById('sp').onclick = () => find(true);
document.getElementById('sx').onclick = closeSearch;

// Zrzut bufora na żądanie z C# (menu karty). Serializacja idzie z powrotem tym samym kanałem.
window.addEventListener('wp-serialize', () => {
  window.chrome.webview.postMessage({ t:'transcript', d: serializeAddon.serialize() });
});

window.chrome.webview.postMessage({ t:'ready', c:term.cols, r:term.rows });
term.focus();
")
              .Append("</script></body></html>");
            return sb.ToString();
        }
    }
}
