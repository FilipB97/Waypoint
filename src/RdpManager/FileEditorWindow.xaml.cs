using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Renci.SshNet.Common;
using RdpManager.Core;

namespace RdpManager
{
    /// <summary>
    /// Edytor pliku z panelu plików: Monaco (ten sam silnik co VS Code) w WebView2, zapis prosto na serwer.
    ///
    /// Okno NIE jest modalne i ma WŁASNE połączenie (z tej samej fabryki co panel): można edytować config
    /// i jednocześnie patrzeć w terminal albo przeglądać katalogi, a zapis nie czeka na transfer w panelu.
    ///
    /// Całe ryzyko edycji na serwerze siedzi w zapisie, więc tu jest jego polityka:
    ///  * format pliku (kodowanie, końce linii) wraca taki, jaki był — <see cref="TextFileFormat"/>;
    ///  * przed zapisem sprawdzamy, czy ktoś nie zmienił pliku od otwarcia — <see cref="RemoteFileInfo.ChangedSince"/>;
    ///  * sam zapis jest bezpieczny (plik tymczasowy + atomowa podmiana) — <see cref="SftpSafeWriter"/>;
    ///  * po nieudanym zapisie tekst ZOSTAJE w edytorze, a komunikat mówi wprost, czy plik na serwerze jest cały.
    ///
    /// Monaco przychodzi z osadzonego zipa i jest serwowany z wirtualnego źródła (WebResourceRequested) —
    /// bez sieci i bez rozpakowywania na dysk; szczegóły paczki: Assets/monaco/README.md.
    /// </summary>
    public partial class FileEditorWindow
    {
        /// <summary>Większych plików nie otwieramy do edycji — to już nie „dopisanie linii", tylko log albo zrzut.</summary>
        public const long MaxBytes = 10L * 1024 * 1024;

        private const string Origin = "https://editor.waypoint.example/";
        private const string MonacoZip = "pack://application:,,,/Assets/monaco/monaco-0.52.2.zip";
        private const string PageUri = "pack://application:,,,/Assets/editor/index.html";

        private static readonly List<FileEditorWindow> OpenEditors = new List<FileEditorWindow>();
        private static Dictionary<string, byte[]> _assets;
        private static readonly object AssetLock = new object();

        private readonly Func<IRemoteFs> _factory;
        private readonly string _requestedPath;
        private readonly string _name;
        private readonly SemaphoreSlim _io = new SemaphoreSlim(1, 1);
        private IRemoteFs _fs;                 // własne połączenie edytora — tworzone przy pierwszym zapisie
        private RemoteFileInfo _opened;        // stan pliku z chwili otwarcia / ostatniego zapisu
        private TextFileFormat _format;
        private byte[] _savedBytes;            // dokładnie to, co jest teraz na serwerze (wg naszej wiedzy)
        private string _initialText;
        private readonly string _lang;
        private CoreWebView2Environment _env;
        private TaskCompletionSource<string> _textRequest;
        private bool _ready, _dirty, _readOnly, _wrap, _saving, _forceClose, _closed;
        private int _ln = 1, _col = 1, _sel;

        private static string L(string key) => LocalizationManager.S(key);

        private FileEditorWindow(Func<IRemoteFs> factory, string requestedPath, string name, RemoteFileInfo info, byte[] data, int? myUid)
        {
            InitializeComponent();
            _factory = factory;
            _requestedPath = requestedPath;
            _name = name ?? "";
            _opened = info;
            _savedBytes = data ?? Array.Empty<byte>();
            _format = TextFileFormat.Detect(_savedBytes, out _initialText);

            int nl = _initialText.IndexOf('\n');
            _lang = EditorLanguage.For(_name, nl < 0 ? _initialText : _initialText.Substring(0, nl));

            NameText.Text = _name;
            // Pokazujemy ścieżkę PRAWDZIWEGO pliku — przy dowiązaniu to ona zostanie zapisana.
            PathText.Text = info.Path == requestedPath ? info.Path : requestedPath + "  →  " + info.Path;

            if (info.LikelyWritableBy(myUid) == false)
            {
                _readOnly = true;
                string owner = info.UserId == 0 ? "root" : "uid " + info.UserId;
                BannerText.Text = string.Format(L("S.edit.ro.text"), owner, UnixMode.Symbolic(info.Mode ?? 0));
                Banner.Visibility = Visibility.Visible;
            }

            if (_format.EncodingName == TextFileFormat.Latin1) SetStatus(L("S.edit.note.latin1"));
            else if (_format.MixedEol) SetStatus(string.Format(L("S.edit.note.mixed"), _format.EolLabel));

            UpdateTitle();
            UpdateInfo();
            Loaded += async (s, e) => await InitWebAsync();
            Closing += OnClosing;
            Closed += OnClosed;
        }

        /// <summary>
        /// Otwiera plik w edytorze (albo aktywuje już otwarty). Treść i metadane pobrał panel — tym samym
        /// połączeniem, którym właśnie przegląda katalog, więc otwarcie nie czeka na nowe logowanie.
        /// </summary>
        public static void OpenFile(Window owner, Func<IRemoteFs> factory, string requestedPath, string name,
                                    RemoteFileInfo info, byte[] data, int? myUid)
        {
            var w = new FileEditorWindow(factory, requestedPath, name, info, data, myUid);
            // Bez Owner: edytor ma własny przycisk na pasku zadań i nie wisi nad oknem głównym
            // (terminal obok ma być widoczny). Startową pozycję liczymy więc sami — środek właściciela.
            if (owner != null && owner.WindowState != WindowState.Minimized)
            {
                double ow = owner.ActualWidth, oh = owner.ActualHeight;
                w.Width = Math.Min(w.Width, Math.Max(w.MinWidth, ow - 80));
                w.Height = Math.Min(w.Height, Math.Max(w.MinHeight, oh - 80));
                var tl = owner.WindowState == WindowState.Maximized ? new Point(0, 0) : new Point(owner.Left, owner.Top);
                w.Left = tl.X + (ow - w.Width) / 2;
                w.Top = tl.Y + (oh - w.Height) / 2;
            }
            else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            OpenEditors.Add(w);
            w.Show();
        }

        /// <summary>Plik już otwarty z tej samej sesji → przywróć jego okno zamiast otwierać drugie.</summary>
        public static bool TryActivate(Func<IRemoteFs> factory, string requestedPath)
        {
            var w = OpenEditors.FirstOrDefault(x => x._factory == factory && x._requestedPath == requestedPath);
            if (w == null) return false;
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            w.Activate();
            return true;
        }

        /// <summary>Przemalowanie otwartych edytorów po zmianie motywu/presetu/akcentu (jak terminale).</summary>
        public static void ApplyThemeAll()
        {
            foreach (var w in OpenEditors.ToList()) w.ApplyTheme();
        }

        /// <summary>
        /// Zamknięcie aplikacji: jedno pytanie o WSZYSTKIE niezapisane pliki (a nie seria okien), potem
        /// zamyka edytory. false = użytkownik rezygnuje z zamykania aplikacji.
        /// </summary>
        public static bool CloseAllForShutdown(Window owner)
        {
            int dirty = OpenEditors.Count(w => w._dirty);
            if (dirty > 0 && MessageBox.Show(owner, string.Format(L("S.edit.shutdown"), dirty), L("S.edit.title"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return false;
            foreach (var w in OpenEditors.ToList()) { w._forceClose = true; try { w.Close(); } catch { } }
            return true;
        }

        // ---------- WebView2 + Monaco ----------

        private async Task InitWebAsync()
        {
            try
            {
                _env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(SettingsStore.Dir, "webview2"));
                await Web.EnsureCoreWebView2Async(_env);
                if (_closed) return;

                var s = Web.CoreWebView2.Settings;
                s.AreDefaultContextMenusEnabled = false;   // Monaco ma własne menu (Wytnij/Kopiuj/Wklej, paleta)
                s.AreDevToolsEnabled = false;
                s.IsStatusBarEnabled = false;
                s.IsZoomControlEnabled = false;            // Ctrl+kółko powiększa czcionkę edytora, nie stronę
                s.AreBrowserAcceleratorKeysEnabled = false;// F5 nie przeładuje strony (utrata zmian), Ctrl+F trafia do Monaco

                Web.CoreWebView2.AddWebResourceRequestedFilter(Origin + "*", CoreWebView2WebResourceContext.All,
                    CoreWebView2WebResourceRequestSourceKinds.All);
                Web.CoreWebView2.WebResourceRequested += OnResource;
                // Strona nigdzie nie nawiguje; gdyby coś (link w treści, przeciągnięty plik) próbowało, zostajemy.
                Web.CoreWebView2.NavigationStarting += (o, e) =>
                {
                    if (!e.Uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
                };
                Web.CoreWebView2.NewWindowRequested += (o, e) => e.Handled = true;
                Web.CoreWebView2.WebMessageReceived += OnWebMessage;
                SetBackdrop();
                Web.CoreWebView2.Navigate(Origin + "index.html");
            }
            catch (Exception ex)
            {
                LoadingText.Text = L("S.edit.nowebview") + "\n" + ex.Message;
                SetStatus(ex.Message, error: true);
            }
        }

        private static Dictionary<string, byte[]> Assets()
        {
            lock (AssetLock)
            {
                if (_assets != null) return _assets;
                var dict = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                var zipInfo = Application.GetResourceStream(new Uri(MonacoZip));
                using (var zip = new ZipArchive(zipInfo.Stream, ZipArchiveMode.Read))
                {
                    foreach (var entry in zip.Entries)
                    {
                        if (entry.Length == 0 && entry.FullName.EndsWith("/")) continue;
                        using (var es = entry.Open())
                        using (var ms = new MemoryStream((int)entry.Length))
                        {
                            es.CopyTo(ms);
                            dict["/" + entry.FullName] = ms.ToArray();
                        }
                    }
                }
                using (var page = Application.GetResourceStream(new Uri(PageUri)).Stream)
                using (var ms = new MemoryStream())
                {
                    page.CopyTo(ms);
                    dict["/index.html"] = ms.ToArray();
                }
                return _assets = dict;
            }
        }

        private void OnResource(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            try
            {
                string path = new Uri(e.Request.Uri).AbsolutePath;
                if (!Assets().TryGetValue(Uri.UnescapeDataString(path), out var bytes))
                {
                    e.Response = _env.CreateWebResourceResponse(null, 404, "Not Found", "");
                    return;
                }
                e.Response = _env.CreateWebResourceResponse(new MemoryStream(bytes, writable: false), 200, "OK",
                    "Content-Type: " + ContentType(path) + "\r\nCache-Control: no-store");
            }
            catch
            {
                e.Response = _env.CreateWebResourceResponse(null, 500, "Error", "");
            }
        }

        private static string ContentType(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".html": return "text/html; charset=utf-8";
                case ".js": return "application/javascript; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".ttf": return "font/ttf";
                case ".json": return "application/json";
                case ".svg": return "image/svg+xml";
                default: return "application/octet-stream";
            }
        }

        private void Post(object msg)
        {
            if (_closed) return;
            try { Web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(msg)); } catch { }
        }

        private object ThemeMessage()
        {
            var t = EditorTheme.From(PaletteColors.Of, ThemeManager.IsLight);
            return new Dictionary<string, object> { ["base"] = t.Base, ["colors"] = t.Colors };
        }

        private void ApplyTheme()
        {
            SetBackdrop();
            Post(new Dictionary<string, object> { ["t"] = "theme", ["theme"] = ThemeMessage() });
        }

        // Tło WebView2 zanim strona się narysuje — inaczej przy starcie mignęłoby bielą.
        private void SetBackdrop()
        {
            var c = PaletteColors.Of("Canvas");
            if (c != null) Web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(c.Value.R, c.Value.G, c.Value.B);
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
                        _ready = true;
                        int fontSize = Math.Min(24, Math.Max(8, SettingsStore.Load().TerminalFontSize));
                        Post(new Dictionary<string, object>
                        {
                            ["t"] = "open", ["text"] = _initialText, ["lang"] = _lang, ["name"] = _name,
                            ["readOnly"] = _readOnly, ["fontSize"] = fontSize, ["wrap"] = _wrap, ["theme"] = ThemeMessage()
                        });
                        _initialText = null;   // strona ma już treść; nie trzymamy drugiej kopii
                        LoadingText.Visibility = Visibility.Collapsed;
                        SaveBtn.IsEnabled = !_readOnly;
                        break;
                    case "save":
                        _ = SaveTextAsync(root.GetProperty("text").GetString());
                        break;
                    case "text":
                        _textRequest?.TrySetResult(root.GetProperty("text").GetString());
                        break;
                    case "dirty":
                        _dirty = root.GetProperty("v").GetBoolean();
                        UpdateTitle();
                        break;
                    case "cursor":
                        _ln = root.GetProperty("ln").GetInt32();
                        _col = root.GetProperty("col").GetInt32();
                        _sel = root.GetProperty("sel").GetInt32();
                        UpdateInfo();
                        break;
                }
            }
            catch { /* uszkodzona wiadomość ze strony nie może wywrócić okna */ }
        }

        private Task<string> GetTextAsync()
        {
            if (!_ready) return Task.FromResult<string>(null);
            _textRequest?.TrySetResult(null);
            _textRequest = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(new { t = "getText" });
            return _textRequest.Task;
        }

        // ---------- Zapis ----------

        /// <summary>Operacja na WŁASNYM połączeniu edytora (jedna naraz); zerwane połączenie odtwarza się samo.</summary>
        private async Task<T> Io<T>(Func<IRemoteFs, T> work)
        {
            await _io.WaitAsync();
            try
            {
                return await Task.Run(() =>
                {
                    if (_fs == null || !_fs.IsConnected)
                    {
                        try { _fs?.Dispose(); } catch { }
                        _fs = null;
                        var fs = _factory();
                        fs.Connect();
                        _fs = fs;
                    }
                    try { return work(_fs); }
                    catch (Exception e) when (IsConnectionLoss(e))
                    {
                        try { _fs?.Dispose(); } catch { }
                        _fs = null;   // następna próba połączy się od nowa
                        throw;
                    }
                });
            }
            finally { _io.Release(); }
        }

        private static bool IsConnectionLoss(Exception e)
        {
            for (var x = e; x != null; x = x.InnerException)
                if (x is SshConnectionException || x is System.Net.Sockets.SocketException || x is ObjectDisposedException
                    || x is SshOperationTimeoutException)
                    return true;
            return false;
        }

        /// <summary>Zapisuje tekst na serwer. true = zapisane (albo nie było czego zapisywać).</summary>
        private async Task<bool> SaveTextAsync(string text)
        {
            if (text == null || _saving) return false;
            if (_readOnly) { SetStatus(L("S.edit.ro.status"), error: true); return false; }

            var fmt = _format;
            if (!fmt.TryEncode(text, out byte[] bytes, out string bad))
            {
                if (MessageBox.Show(this, string.Format(L("S.edit.latin1.ask"), bad), L("S.edit.title"),
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return false;
                fmt = fmt.AsUtf8();
                fmt.TryEncode(text, out bytes, out _);
            }

            if (TextFileFormat.SameBytes(bytes, _savedBytes))
            {
                Post(new { t = "saved" });
                SetStatus(L("S.edit.nochange"));
                return true;
            }

            _saving = true;
            SaveBtn.IsEnabled = false;
            SetStatus(L("S.edit.saving"));
            try
            {
                string path = _opened.Path;
                var now = await Io(fs => fs.Stat(path));
                if (RemoteFileInfo.ChangedSince(_opened, now)
                    && MessageBox.Show(this, string.Format(L("S.edit.conflict"), _name,
                           now.ModifiedUtc.ToLocalTime().ToString("g"), FormatSize(now.Length)),
                           L("S.edit.title"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    SetStatus(L("S.edit.conflict.kept"), error: true);
                    return false;
                }

                // Metadane ŚWIEŻE (now), nie z otwarcia — jeśli ktoś w międzyczasie zmienił uprawnienia,
                // zapis ma zachować obecne, a nie przywrócić stare.
                var result = await Io(fs => fs.WriteFileSafe(bytes, now));

                try { _opened = await Io(fs => fs.Stat(path)); }
                catch { _opened = now; _opened.Length = bytes.Length; _opened.ModifiedUtc = DateTime.UtcNow; }
                _format = fmt;
                _savedBytes = bytes;
                Post(new { t = "saved" });

                string when = DateTime.Now.ToString("HH:mm:ss");
                SetStatus(result.Mode == SafeWriteMode.AtomicReplace
                    ? string.Format(L("S.edit.saved"), when)
                    : string.Format(L("S.edit.saved.inplace"), when, L(result.FallbackReasonKey ?? "S.edit.fb.rename")));
                UpdateInfo();
                return true;
            }
            catch (SafeWriteException ex)
            {
                bool denied = ex.InnerException is SftpPermissionDeniedException || ex.InnerException is UnauthorizedAccessException;
                string msg = ex.OriginalMayBeDamaged
                    ? string.Format(L("S.edit.fail.damaged"), ex.Message)
                    : denied ? L("S.edit.fail.denied") : string.Format(L("S.edit.fail.safe"), ex.Message);
                SetStatus(ex.OriginalMayBeDamaged ? L("S.edit.fail.damaged.short") : L("S.edit.fail.short"), error: true);
                MessageBox.Show(this, msg, L("S.edit.title"), MessageBoxButton.OK,
                    ex.OriginalMayBeDamaged ? MessageBoxImage.Error : MessageBoxImage.Warning);
                return false;
            }
            catch (Exception ex)
            {
                // Błąd przed zapisem (łączenie, odczyt metadanych) — plik na serwerze nietknięty.
                SetStatus(L("S.edit.fail.short"), error: true);
                MessageBox.Show(this, string.Format(L("S.edit.fail.safe"), ex.Message), L("S.edit.title"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            finally
            {
                _saving = false;
                SaveBtn.IsEnabled = _ready && !_readOnly;
            }
        }

        private async void Save_Click(object sender, RoutedEventArgs e) => await SaveTextAsync(await GetTextAsync());

        // Kopia lokalna — wyjście awaryjne, gdy zapis na serwer nie przechodzi (brak uprawnień, zerwane łącze).
        private async void SaveCopy_Click(object sender, RoutedEventArgs e)
        {
            string text = await GetTextAsync();
            if (text == null) return;
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = _name, Filter = L("S.edit.filter.all") + "|*.*" };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                if (!_format.TryEncode(text, out var bytes, out _)) _format.AsUtf8().TryEncode(text, out bytes, out _);
                File.WriteAllBytes(dlg.FileName, bytes);
                SetStatus(string.Format(L("S.edit.copysaved"), dlg.FileName));
            }
            catch (Exception ex) { SetStatus(ex.Message, error: true); }
        }

        private async void Reload_Click(object sender, RoutedEventArgs e)
        {
            if (!_ready || _saving) return;
            if (_dirty && MessageBox.Show(this, L("S.edit.reload.ask"), L("S.edit.title"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            SetStatus(L("S.edit.reloading"));
            try
            {
                string path = _opened.Path;
                var (info, data) = await Io(fs =>
                {
                    var i = fs.Stat(path);
                    if (i.Length > MaxBytes) throw new IOException(string.Format(L("S.edit.toobig"), FormatSize(MaxBytes)));
                    using (var ms = new MemoryStream())
                    {
                        fs.Download(path, ms);
                        return (i, ms.ToArray());
                    }
                });
                _opened = info;
                _savedBytes = data;
                _format = TextFileFormat.Detect(data, out string text);
                Post(new { t = "reset", text });
                SetStatus(string.Format(L("S.edit.reloaded"), DateTime.Now.ToString("HH:mm:ss")));
                UpdateInfo();
            }
            catch (Exception ex) { SetStatus(ex.Message, error: true); }
        }

        private void Wrap_Click(object sender, RoutedEventArgs e)
        {
            _wrap = !_wrap;
            WrapBtn.Appearance = _wrap ? Wpf.Ui.Controls.ControlAppearance.Secondary : Wpf.Ui.Controls.ControlAppearance.Transparent;
            Post(new { t = "wrap", v = _wrap });
        }

        private void EditAnyway_Click(object sender, RoutedEventArgs e)
        {
            _readOnly = false;
            Banner.Visibility = Visibility.Collapsed;
            SaveBtn.IsEnabled = _ready;
            Post(new { t = "readOnly", v = false });
            Post(new { t = "focus" });
        }

        // ---------- Zamykanie ----------

        private async void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_forceClose || !_dirty) return;
            e.Cancel = true;
            var r = MessageBox.Show(this, string.Format(L("S.edit.close.ask"), _name), L("S.edit.title"),
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return;
            if (r == MessageBoxResult.Yes && !await SaveTextAsync(await GetTextAsync())) return;   // nieudany zapis → zostajemy
            _forceClose = true;
            Close();
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _closed = true;
            OpenEditors.Remove(this);
            _textRequest?.TrySetResult(null);
            try { Web.Dispose(); } catch { }
            var fs = _fs;
            _fs = null;
            if (fs != null) Task.Run(() => { try { fs.Dispose(); } catch { } });
        }

        // ---------- Wygląd ----------

        private void UpdateTitle()
        {
            string t = (_dirty ? "● " : "") + _name + " — " + L("S.edit.title");
            Title = t;
            Bar.Title = t;
        }

        private void UpdateInfo()
        {
            var parts = new List<string> { string.Format(L("S.edit.pos"), _ln, _col) };
            if (_sel > 0) parts[0] += " " + string.Format(L("S.edit.sel"), _sel);
            parts.Add(_format.EncodingName);
            parts.Add(_format.EolLabel);
            parts.Add(_lang);
            if (_readOnly) parts.Add(L("S.edit.ro.short"));
            InfoText.Text = string.Join("  ·  ", parts);
        }

        private void SetStatus(string text, bool error = false)
        {
            StatusText.Text = text ?? "";
            StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, error ? "Danger" : "TextTer");
        }

        private static string FormatSize(long b)
        {
            if (b < 1024) return b + " B";
            if (b < 1024 * 1024) return (b / 1024.0).ToString("0.#") + " KB";
            return (b / 1048576.0).ToString("0.#") + " MB";
        }
    }
}
