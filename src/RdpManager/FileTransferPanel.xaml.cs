using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RdpManager
{
    /// <summary>Ładunek przeciągania wiersza między panelami (dual-pane): źródłowy panel + wpis.</summary>
    public sealed class FileDragData
    {
        public FileTransferPanel Source;
        public System.Collections.Generic.List<FileItemRef> Items = new System.Collections.Generic.List<FileItemRef>();
    }

    /// <summary>Odwołanie do pliku/katalogu w transferze (wielozaznaczenie): pełna ścieżka + nazwa + czy katalog.</summary>
    public sealed class FileItemRef
    {
        public string Full;
        public string Name;
        public bool IsDir;
    }

    /// <summary>
    /// Panel plików zdalnego systemu (<see cref="IRemoteFs"/>): nawigacja (breadcrumb), wyślij/pobierz,
    /// nowy folder, usuwanie oraz upuszczanie plików z Eksploratora (upload). Działa dla SFTP i FTP/FTPS —
    /// źródło dostarcza fabryka. Operacje w tle, UI przez Dispatcher, jedna naraz. Jako samodzielna sesja
    /// zgłasza <see cref="Connected"/>/<see cref="Failed"/>; błąd pojedynczej operacji NIE zrywa sesji.
    /// </summary>
    public partial class FileTransferPanel : UserControl
    {
        private sealed class Row
        {
            public Wpf.Ui.Controls.SymbolRegular Icon { get; set; }
            public Brush IconBrush { get; set; }
            public string Name { get; set; }
            public string FullName { get; set; }
            public bool IsDir { get; set; }
            public string SizeText { get; set; }
            public string Modified { get; set; }
            public long Len { get; set; }        // surowy rozmiar — klucz sortowania (SizeText jest sformatowany)
            public DateTime Mod { get; set; }     // surowa data — klucz sortowania (Modified jest sformatowany)
        }

        // Sortowanie listy po kolumnie (klik nagłówka). Katalogi ZAWSZE na górze, sortowanie w obrębie grupy.
        private enum SortCol { Name, Size, Modified }
        private SortCol _sortCol = SortCol.Name;
        private bool _sortDesc;

        // Stan jednego transferu drzewa (upload/download) — postęp bajtowy do paska, callback per plik do statusu.
        // Klasa (nie struct) celowo: dzielona przez referencję między wątkiem roboczym (ProgressStream) a UI.
        private sealed class TransferState
        {
            public long TotalBytes;
            public long DoneBytes;
            public Action<string> OnFileStarted;
            public Action OnBytesChanged;
        }

        // Dekorator strumienia: zgłasza przesłane bajty i sprawdza anulowanie przy KAŻDYM Read/Write.
        // Owija lokalną stronę transferu (source pliku przy wysyłce, target pliku przy pobieraniu) —
        // działa dla WSZYSTKICH backendów (SFTP/FTP/lokalny) bez zmiany IRemoteFs czy jego implementacji,
        // bo każda z nich ostatecznie czyta/pisze do strumienia PRZEKAZANEGO przez wołającego (ten tutaj).
        private sealed class ProgressStream : System.IO.Stream
        {
            private readonly System.IO.Stream _inner;
            private readonly CancellationToken _ct;
            private readonly Action<int> _onBytes;

            public ProgressStream(System.IO.Stream inner, CancellationToken ct, Action<int> onBytes)
            { _inner = inner; _ct = ct; _onBytes = onBytes; }

            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => _inner.CanSeek;
            public override bool CanWrite => _inner.CanWrite;
            public override long Length => _inner.Length;
            public override long Position { get => _inner.Position; set => _inner.Position = value; }
            public override void Flush() => _inner.Flush();
            public override long Seek(long offset, System.IO.SeekOrigin origin) => _inner.Seek(offset, origin);
            public override void SetLength(long value) => _inner.SetLength(value);

            public override int Read(byte[] buffer, int offset, int count)
            {
                _ct.ThrowIfCancellationRequested();
                int n = _inner.Read(buffer, offset, count);
                if (n > 0) _onBytes?.Invoke(n);
                return n;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                _ct.ThrowIfCancellationRequested();
                _inner.Write(buffer, offset, count);
                _onBytes?.Invoke(count);
            }
        }

        private enum OverwriteChoice { Overwrite, Skip, Cancel }

        private readonly Func<IRemoteFs> _factory;
        private IRemoteFs _fs;
        private string _path = "/";
        private bool _busy;
        private CancellationTokenSource _transferCts;
        private TransferState _xferState;   // aktywny transfer — tylko on może aktualizować pasek (odrzuca spóźnione raporty)
        private readonly Stopwatch _progressThrottle = new Stopwatch();

        /// <summary>Udane (po)łączenie — dla sesji plikowej: karta „online".</summary>
        public event Action Connected;

        /// <summary>Nieudane łączenie (nie: błąd operacji) — dla sesji plikowej: karta „offline".</summary>
        public event Action<string> Failed;

        private static string L(string key) => LocalizationManager.S(key);
        private static Brush Res(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;

        public FileTransferPanel(Func<IRemoteFs> factory, bool localMode = false)
        {
            InitializeComponent();
            _factory = factory;
            if (localMode)   // panel lokalny w dual-pane: transfer robią strzałki/drag, nie dialogi
            {
                UploadBtn.Visibility = Visibility.Collapsed;
                DownloadBtn.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>Bieżący katalog panelu (do transferu w dual-pane).</summary>
        public string CurrentDir => _path;

        /// <summary>Zaznaczony wpis (plik lub katalog) — do transferu; false gdy brak zaznaczenia.
        /// Przy wielozaznaczeniu zwraca pierwszy; pełne zaznaczenie przez <see cref="GetSelection"/>.</summary>
        public bool TryGetSelected(out string full, out string name, out bool isDir)
        {
            full = name = null; isDir = false;
            if (FileList.SelectedItem is Row r) { full = r.FullName; name = r.Name; isDir = r.IsDir; return true; }
            return false;
        }

        /// <summary>Wszystkie zaznaczone wpisy (wielozaznaczenie: Ctrl/Shift/Ctrl+A) — do transferu/usuwania.</summary>
        public System.Collections.Generic.List<FileItemRef> GetSelection()
            => FileList.SelectedItems.Cast<Row>()
                .Select(r => new FileItemRef { Full = r.FullName, Name = r.Name, IsDir = r.IsDir })
                .ToList();

        /// <summary>Upuszczenie pliku z DRUGIEGO panelu na ten (dual-pane) — host wykonuje transfer.</summary>
        public event Action<FileDragData> CrossPaneDrop;

        /// <summary>Wysyła lokalny plik LUB katalog (rekurencyjnie) do bieżącego katalogu TEGO panelu.
        /// Pyta raz o nadpisanie, jeśli nazwa już istnieje w bieżącym listingu (A2 z przeglądu).</summary>
        public async Task<bool> TransferInLocalFileAsync(string localPath)
        {
            bool isDir = System.IO.Directory.Exists(localPath);
            if (!isDir && !System.IO.File.Exists(localPath)) return false;
            string dir = _path.TrimEnd('/');
            string name = System.IO.Path.GetFileName(localPath.TrimEnd('/', '\\'));

            if (!CheckUploadConflict(name, out _)) return false;

            bool ok = await RunTransferAsync(
                _ => LocalTreeSize(localPath),
                n => string.Format(L("S.sftp.uploading"), n),
                (fs, ct, state) => UploadTree(fs, localPath, dir, ct, state));
            if (ok) RefreshAsync();
            return ok;
        }

        /// <summary>Pobiera plik LUB katalog (rekurencyjnie) z TEGO panelu do lokalnego katalogu (wołający odświeża cel).
        /// Pyta raz o nadpisanie, jeśli cel już istnieje lokalnie.</summary>
        public async Task<bool> TransferOutToLocalAsync(string remoteFull, string remoteName, bool isDir, string localDir)
        {
            string dest = SafeCombine(localDir.Replace('/', '\\'), remoteName);
            if (System.IO.Directory.Exists(dest) || System.IO.File.Exists(dest))
            {
                if (AskOverwrite(remoteName) != OverwriteChoice.Overwrite) return false;
            }
            return await RunTransferAsync(
                fs => RemoteTreeSize(fs, remoteFull, isDir, 0),
                n => string.Format(L("S.sftp.downloading"), n),
                (fs, ct, state) => DownloadTree(fs, remoteFull, remoteName, isDir, localDir.Replace('/', '\\'), ct, state));
        }

        // ---------- Rekurencyjny transfer drzew (plik lub katalog); anulowanie + postęp bajtowy ----------

        /// <summary>Wysyła lokalny plik/katalog do remoteDir na zdalnym fs (rekurencyjnie, bez śledzenia postępu).
        /// Publiczne dla testów — TransferState jest prywatny, więc pełny wariant zostaje wewnętrzny.</summary>
        public static void UploadTree(IRemoteFs fs, string localPath, string remoteDir, CancellationToken ct)
            => UploadTree(fs, localPath, remoteDir, ct, null);

        // Wysyła lokalny plik/katalog do remoteDir na zdalnym fs; katalogi tworzy, w pliki wchodzi rekurencyjnie.
        // Statyczna i bezstanowa poza jawnymi parametrami — testowalna bez UI (state/ct mogą być null/None).
        private static void UploadTree(IRemoteFs fs, string localPath, string remoteDir, CancellationToken ct, TransferState state)
        {
            ct.ThrowIfCancellationRequested();
            string name = System.IO.Path.GetFileName(localPath.TrimEnd('/', '\\'));
            string target = remoteDir.TrimEnd('/') + "/" + name;
            if (System.IO.Directory.Exists(localPath))
            {
                EnsureRemoteDir(fs, target);
                foreach (var sub in System.IO.Directory.GetDirectories(localPath)) UploadTree(fs, sub, target, ct, state);
                foreach (var f in System.IO.Directory.GetFiles(localPath)) UploadTree(fs, f, target, ct, state);
            }
            else
            {
                state?.OnFileStarted?.Invoke(name);
                using (var real = System.IO.File.OpenRead(localPath))
                using (var wrapped = new ProgressStream(real, ct, delta => AddBytes(state, delta)))
                    fs.Upload(wrapped, target, true);
            }
        }

        /// <summary>Pobiera zdalny plik/katalog do localParentDir (rekurencyjnie, bez śledzenia postępu).
        /// Publiczne dla testów — TransferState jest prywatny, więc pełny wariant zostaje wewnętrzny.</summary>
        public static void DownloadTree(IRemoteFs fs, string remoteFull, string remoteName, bool isDir, string localParentDir, CancellationToken ct)
            => DownloadTree(fs, remoteFull, remoteName, isDir, localParentDir, ct, null);

        // Pobiera zdalny plik/katalog do localParentDir (ścieżka Windows); katalogi listuje i schodzi rekurencyjnie.
        // remoteName pochodzi z listingu ZDALNEGO serwera — złośliwy/skompromitowany serwer mógłby zwrócić
        // "..\..\", ścieżkę z literą dysku itp., próbując zapisać poza wybranym katalogiem ("zip-slip" /
        // path traversal). SafeCombine odrzuca takie nazwy i wymusza pozostanie wewnątrz localParentDir.
        private static void DownloadTree(IRemoteFs fs, string remoteFull, string remoteName, bool isDir, string localParentDir, CancellationToken ct, TransferState state)
        {
            ct.ThrowIfCancellationRequested();
            string dest = SafeCombine(localParentDir, remoteName);
            if (isDir)
            {
                System.IO.Directory.CreateDirectory(dest);
                foreach (var e in fs.List(remoteFull)) DownloadTree(fs, e.FullName, e.Name, e.IsDir, dest, ct, state);
            }
            else
            {
                state?.OnFileStarted?.Invoke(remoteName);
                using (var real = System.IO.File.Create(dest))
                using (var wrapped = new ProgressStream(real, ct, delta => AddBytes(state, delta)))
                    fs.Download(remoteFull, wrapped);
            }
        }

        private static void AddBytes(TransferState state, int delta)
        {
            if (state == null) return;
            state.DoneBytes += delta;
            state.OnBytesChanged?.Invoke();
        }

        // Sumuje rozmiar CAŁEGO lokalnego poddrzewa (do paska postępu przy wysyłce) — tanie, bez sieci.
        private static long LocalTreeSize(string path)
        {
            if (System.IO.Directory.Exists(path))
            {
                long sum = 0;
                foreach (var d in System.IO.Directory.GetDirectories(path)) sum += LocalTreeSize(d);
                foreach (var f in System.IO.Directory.GetFiles(path)) { try { sum += new System.IO.FileInfo(f).Length; } catch { } }
                return sum;
            }
            try { return System.IO.File.Exists(path) ? new System.IO.FileInfo(path).Length : 0; } catch { return 0; }
        }

        // Sumuje rozmiar CAŁEGO zdalnego poddrzewa (do paska postępu przy pobieraniu) — dodatkowe listowania,
        // ale ograniczone dokładnie do drzewa, które i tak zaraz pobierzemy.
        private static long RemoteTreeSize(IRemoteFs fs, string remoteFull, bool isDir, long knownLength)
        {
            if (!isDir) return knownLength;
            long sum = 0;
            foreach (var e in fs.List(remoteFull)) sum += RemoteTreeSize(fs, e.FullName, e.IsDir, e.Length);
            return sum;
        }

        /// <summary>Łączy <paramref name="localDir"/> z nazwą pochodzącą ZE ZDALNEGO SERWERA, odrzucając próby
        /// wyjścia poza <paramref name="localDir"/> (separatory ścieżki, "."/"..", litera dysku). Dwie warstwy:
        /// odrzucenie niebezpiecznych znaków w samej nazwie ORAZ kontrola, że wynikowa pełna ścieżka faktycznie
        /// leży wewnątrz katalogu — sama pierwsza warstwa nie złapałaby np. "C:evil.txt" (Path.Combine traktuje
        /// "rooted" drugi argument jako zastępujący pierwszy, ignorując localDir). Publiczne dla testów.</summary>
        public static string SafeCombine(string localDir, string remoteName)
        {
            if (string.IsNullOrWhiteSpace(remoteName) || remoteName == "." || remoteName == ".."
                || remoteName.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
                throw new InvalidOperationException(string.Format(L("S.sftp.unsafename"), remoteName));

            string root = System.IO.Path.GetFullPath(localDir);
            string rootWithSep = root.EndsWith(System.IO.Path.DirectorySeparatorChar.ToString())
                ? root : root + System.IO.Path.DirectorySeparatorChar;
            string dest = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, remoteName));
            if (!dest.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(string.Format(L("S.sftp.unsafename"), remoteName));
            return dest;
        }

        // SFTP rzuca gdy katalog już istnieje; FTP/Local są idempotentne. Ignorujemy — realny błąd (np. brak
        // uprawnień) i tak wyjdzie przy Upload/List do środka.
        private static void EnsureRemoteDir(IRemoteFs fs, string path)
        {
            try { fs.CreateDirectory(path); } catch { }
        }

        // Jedna operacja naraz; łączy przy pierwszym użyciu (dispatcher wolny — praca w tle).
        // Rozróżnia błąd POŁĄCZENIA (zgłoszony przez Failed → sesja offline) od błędu operacji (tylko status).
        private async Task<bool> RunAsync(string statusText, Action<IRemoteFs> work, CancellationToken ct = default)
        {
            if (_busy) return false;
            _busy = true;
            bool connecting = false;
            try
            {
                SetStatus(statusText);
                bool didConnect = false;
                await Task.Run(() =>
                {
                    if (_fs == null || !_fs.IsConnected)
                    {
                        try { _fs?.Dispose(); } catch { }
                        _fs = _factory();
                        connecting = true;
                        _fs.Connect();
                        connecting = false;
                        didConnect = true;
                        if (_path == "/") _path = _fs.HomeDirectory ?? "/";   // start w katalogu domowym
                    }
                    work(_fs);
                }, ct);
                if (didConnect) Connected?.Invoke();
                SetStatus(L("S.sftp.done"));
                return true;
            }
            catch (OperationCanceledException)
            {
                SetStatus(L("S.sftp.cancelled"));
                return false;
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message, error: true);
                if (connecting)   // błąd łączenia zrywa sesję; błąd operacji zostawia ją żywą
                {
                    try { _fs?.Dispose(); } catch { }
                    _fs = null;
                    Failed?.Invoke(ex.Message);
                }
                return false;
            }
            finally { _busy = false; }
        }

        // Jak RunAsync, ale dla transferów drzew: liczy rozmiar CAŁOŚCI z góry (pasek postępu), pokazuje
        // przycisk Anuluj i sprząta CancellationTokenSource po sobie (A7-podobny wyciek, tu od początku poprawnie).
        private async Task<bool> RunTransferAsync(Func<IRemoteFs, long> computeTotal, Func<string, string> statusFormat,
            Action<IRemoteFs, CancellationToken, TransferState> transfer)
        {
            var state = new TransferState();
            state.OnFileStarted = name => Dispatcher.BeginInvoke(new Action(() => SetStatus(statusFormat(name))));
            state.OnBytesChanged = () => ReportBytesThrottled(state);

            _transferCts?.Cancel();
            _transferCts?.Dispose();
            var cts = new CancellationTokenSource();
            _transferCts = cts;
            _xferState = state;
            ShowTransferUi(true);
            try
            {
                return await RunAsync(L("S.sftp.connecting"), fs =>
                {
                    state.TotalBytes = computeTotal(fs);
                    transfer(fs, cts.Token, state);
                }, cts.Token);
            }
            finally
            {
                ShowTransferUi(false);
                if (_xferState == state) _xferState = null;
                if (_transferCts == cts) _transferCts = null;
                cts.Dispose();
            }
        }

        private void ShowTransferUi(bool on)
        {
            TransferProgress.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            CancelTransferBtn.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (!on) TransferProgress.Value = 0;
        }

        private void CancelTransfer_Click(object sender, RoutedEventArgs e) => _transferCts?.Cancel();

        // Throttled (max ~10/s) aktualizacja paska — bez tego szybki lokalny kopiuj wołałby Dispatcher.BeginInvoke
        // tysiące razy na sekundę. `state != _xferState` odrzuca spóźnione raporty po anulowaniu/nowym transferze.
        private void ReportBytesThrottled(TransferState state)
        {
            if (_progressThrottle.IsRunning && _progressThrottle.ElapsedMilliseconds < 100) return;
            _progressThrottle.Restart();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (state != _xferState) return;
                double pct = state.TotalBytes > 0 ? Math.Min(100.0, state.DoneBytes * 100.0 / state.TotalBytes) : 0;
                TransferProgress.Value = pct;
            }));
        }

        // Pyta RAZ (dla korzenia transferu — "ask-once", nie per-plik w głębi drzewa) czy nadpisać istniejący
        // cel. Uproszczenie świadome: pełny per-plik dialog dla rekurencyjnego drzewa byłby bardzo inwazyjny;
        // to i tak koniec z CAŁKOWICIE cichym nadpisywaniem sprzed poprawki (zero ostrzeżenia).
        private OverwriteChoice AskOverwrite(string name)
        {
            var r = MessageBox.Show(Window.GetWindow(this),
                string.Format(L("S.sftp.exists.confirm"), name), L("S.sftp.exists.title"),
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            return r == MessageBoxResult.Yes ? OverwriteChoice.Overwrite
                 : r == MessageBoxResult.No ? OverwriteChoice.Skip
                 : OverwriteChoice.Cancel;
        }

        // Sprawdza istnienie celu w BIEŻĄCO wczytanym listingu zdalnym (bez dodatkowego round-tripu) i pyta
        // raz o nadpisanie/pominięcie. true = kontynuuj wysyłkę. cancelWhole = true → wołający powinien
        // przerwać CAŁĄ serię (przy wielu plikach naraz), nie tylko ten jeden element.
        private bool CheckUploadConflict(string name, out bool cancelWhole)
        {
            cancelWhole = false;
            bool exists = (FileList.ItemsSource as IEnumerable<Row>)?.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)) == true;
            if (!exists) return true;
            var choice = AskOverwrite(name);
            if (choice == OverwriteChoice.Cancel) { cancelWhole = true; return false; }
            return choice == OverwriteChoice.Overwrite;
        }

        private void SetStatus(string text, bool error = false)
        {
            StatusText.Text = text ?? "";
            StatusText.Foreground = error ? Res("Danger") : Res("TextTer");
            StatusText.ToolTip = string.IsNullOrEmpty(text) ? null : text;
        }

        /// <summary>Odświeża listing bieżącego katalogu (łączy przy pierwszym wywołaniu).</summary>
        public async void RefreshAsync()
        {
            var folder = Res("Accent");
            var fileBr = Res("TextSec");
            List<Row> rows = null;
            bool ok = await RunAsync(L("S.sftp.connecting"), fs =>
            {
                rows = fs.List(_path)
                    .Select(f => new Row
                    {
                        Icon = f.IsDir ? Wpf.Ui.Controls.SymbolRegular.Folder24 : Wpf.Ui.Controls.SymbolRegular.Document24,
                        IconBrush = f.IsDir ? folder : fileBr,
                        Name = f.Name,
                        FullName = f.FullName,
                        IsDir = f.IsDir,
                        SizeText = f.IsDir ? "" : FormatSize(f.Length),
                        Modified = f.Modified.ToString("yyyy-MM-dd HH:mm"),
                        Len = f.Length,
                        Mod = f.Modified
                    })
                    .ToList();
                rows = SortRows(rows);
            });
            if (!ok) return;
            FileList.ItemsSource = rows;
            UpdateSortIndicators();
            BuildBreadcrumb();
            SetStatus("");
        }

        // Katalogi na górze, potem sortowanie wg wybranej kolumny i kierunku. Nazwa bez uwzględniania wielkości liter.
        private List<Row> SortRows(List<Row> rows)
        {
            var byDir = rows.OrderByDescending(r => r.IsDir);
            IOrderedEnumerable<Row> sorted;
            switch (_sortCol)
            {
                case SortCol.Size:
                    sorted = _sortDesc ? byDir.ThenByDescending(r => r.Len) : byDir.ThenBy(r => r.Len);
                    break;
                case SortCol.Modified:
                    sorted = _sortDesc ? byDir.ThenByDescending(r => r.Mod) : byDir.ThenBy(r => r.Mod);
                    break;
                default:
                    sorted = _sortDesc
                        ? byDir.ThenByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase)
                        : byDir.ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase);
                    break;
            }
            return sorted.ToList();
        }

        // Klik nagłówka kolumny: ta sama kolumna → odwróć kierunek; inna → ustaw ją (rosnąco). Re-sortuje bez pobierania.
        private void HeaderSort_Click(object sender, RoutedEventArgs e)
        {
            var col = (sender as FrameworkElement)?.Tag as string;
            var target = col == "size" ? SortCol.Size : col == "modified" ? SortCol.Modified : SortCol.Name;
            if (_sortCol == target) _sortDesc = !_sortDesc;
            else { _sortCol = target; _sortDesc = false; }

            if (FileList.ItemsSource is IEnumerable<Row> current)
            {
                FileList.ItemsSource = SortRows(current.ToList());
                UpdateSortIndicators();
            }
        }

        private void UpdateSortIndicators()
        {
            string arrow = _sortDesc ? " ▼" : " ▲";
            SortArrowName.Text = _sortCol == SortCol.Name ? arrow : "";
            SortArrowSize.Text = _sortCol == SortCol.Size ? arrow : "";
            SortArrowMod.Text = _sortCol == SortCol.Modified ? arrow : "";
        }

        // Ścieżka jako klikalne okruszki: „/" › katalog › podkatalog.
        private void BuildBreadcrumb()
        {
            Breadcrumb.Children.Clear();
            Breadcrumb.Children.Add(MakeCrumb("/", "/"));
            string acc = "";
            foreach (var seg in _path.Split('/'))
            {
                if (seg.Length == 0) continue;
                acc += "/" + seg;
                Breadcrumb.Children.Add(new TextBlock
                {
                    Text = "›",
                    Foreground = Res("TextTer"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(1, 0, 1, 0)
                });
                Breadcrumb.Children.Add(MakeCrumb(seg, acc));
            }
        }

        private System.Windows.Controls.Button MakeCrumb(string label, string target)
        {
            var b = new System.Windows.Controls.Button { Content = label, Style = (Style)FindResource("FtCrumbBtn") };
            b.Click += (s, e) => { if (_path != target) { _path = target; RefreshAsync(); } };
            return b;
        }

        private void Up_Click(object sender, RoutedEventArgs e)
        {
            string p = _path.TrimEnd('/');
            int i = p.LastIndexOf('/');
            _path = i <= 0 ? "/" : p.Substring(0, i);
            RefreshAsync();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshAsync();

        private async void Upload_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true };
            if (dlg.ShowDialog() != true) return;
            await UploadPaths(dlg.FileNames);
        }

        // Wysyła podane pliki/katalogi do bieżącego katalogu (wspólne dla przycisku i upuszczenia z Eksploratora).
        // Konflikt nazwy pyta RAZ na element (ask-once) — "Anuluj" przerywa całą serię, "Pomiń" tylko ten element.
        private async Task UploadPaths(IEnumerable<string> paths)
        {
            string dir = _path.TrimEnd('/');
            bool any = false;
            foreach (var p in paths)
            {
                bool isDir = System.IO.Directory.Exists(p);
                if (!isDir && !System.IO.File.Exists(p)) continue;
                string name = System.IO.Path.GetFileName(p.TrimEnd('/', '\\'));

                if (!CheckUploadConflict(name, out bool cancelWhole))
                {
                    if (cancelWhole) break;
                    continue;
                }

                any = true;
                string localPath = p;
                bool ok = await RunTransferAsync(
                    _ => LocalTreeSize(localPath),
                    n => string.Format(L("S.sftp.uploading"), n),
                    (fs, ct, state) => UploadTree(fs, localPath, dir, ct, state));
                if (!ok) break;   // błąd/anulowanie przerywa serię (status pokazuje powód)
            }
            if (any) RefreshAsync();
        }

        private async void Download_Click(object sender, RoutedEventArgs e)
        {
            var sel = GetSelection();
            if (sel.Count > 1)   // wielozaznaczenie → jeden folder docelowy, po kolei każdy plik/katalog
            {
                var mdlg = new Microsoft.Win32.OpenFolderDialog();
                if (mdlg.ShowDialog() != true) return;
                foreach (var it in sel)
                    await TransferOutToLocalAsync(it.Full, it.Name, it.IsDir, mdlg.FolderName);
                return;
            }
            if (!(FileList.SelectedItem is Row r)) return;
            if (r.IsDir)   // katalog → wybór folderu docelowego, pobranie rekurencyjne
            {
                var fdlg = new Microsoft.Win32.OpenFolderDialog();
                if (fdlg.ShowDialog() != true) return;

                string dest = SafeCombine(fdlg.FolderName, r.Name);
                if (System.IO.Directory.Exists(dest) || System.IO.File.Exists(dest))
                {
                    var choice = AskOverwrite(r.Name);
                    if (choice != OverwriteChoice.Overwrite) return;
                }

                await RunTransferAsync(
                    fs => RemoteTreeSize(fs, r.FullName, true, 0),
                    n => string.Format(L("S.sftp.downloading"), n),
                    (fs, ct, state) => DownloadTree(fs, r.FullName, r.Name, true, fdlg.FolderName, ct, state));
                return;
            }
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = r.Name };   // SaveFileDialog pyta o nadpisanie natywnie
            if (dlg.ShowDialog() != true) return;

            await RunAsync(string.Format(L("S.sftp.downloading"), r.Name), fs =>
            {
                using (var s = System.IO.File.Create(dlg.FileName))
                    fs.Download(r.FullName, s);
            });
        }

        private async void NewFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new InputDialog(L("S.sftp.newfolder"), L("S.sftp.newfolder.label"), "")
            { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true || dlg.Value.Length == 0) return;

            string name = dlg.Value;
            bool ok = await RunAsync(L("S.sftp.connecting"), fs => fs.CreateDirectory(_path.TrimEnd('/') + "/" + name));
            if (ok) RefreshAsync();
        }

        private async void Delete_Click(object sender, RoutedEventArgs e)
        {
            var sel = GetSelection();
            if (sel.Count == 0) return;
            string prompt = sel.Count == 1
                ? string.Format(L("S.sftp.delete.confirm"), sel[0].Name)
                : string.Format(L("S.sftp.delete.confirmmany"), sel.Count);
            if (MessageBox.Show(prompt, L("S.sftp.delete"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            bool ok = await RunAsync(L("S.sftp.connecting"), fs =>
            {
                foreach (var it in sel) fs.Delete(it.Full, it.IsDir);   // katalog: tylko pusty
            });
            if (ok) RefreshAsync();
        }

        private void List_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (FileList.SelectedItem is Row r)
            {
                if (r.IsDir) { _path = r.FullName; RefreshAsync(); }
                else Download_Click(sender, null);
            }
        }

        // ---------- Przeciąganie wiersza (dual-pane: między panelami) ----------

        private System.Windows.Point _dragStart;
        private void List_PreviewDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => _dragStart = e.GetPosition(null);
        private void List_PreviewMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
            var sel = GetSelection();
            if (sel.Count == 0) return;
            var pos = e.GetPosition(null);
            if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            try { DragDrop.DoDragDrop(FileList, new FileDragData { Source = this, Items = sel }, DragDropEffects.Copy); }
            catch { }
        }

        // ---------- Drag&drop z Eksploratora (upload) + między panelami ----------

        private void Panel_DragEnter(object sender, DragEventArgs e) => UpdateDropEffect(e);
        private void Panel_DragOver(object sender, DragEventArgs e) => UpdateDropEffect(e);
        private void Panel_DragLeave(object sender, DragEventArgs e) => DropOverlay.Visibility = Visibility.Collapsed;

        private void UpdateDropEffect(DragEventArgs e)
        {
            bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
            bool cross = e.Data.GetDataPresent(typeof(FileDragData))
                         && (e.Data.GetData(typeof(FileDragData)) as FileDragData)?.Source != this;
            bool ok = files || cross;
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            DropOverlay.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
            e.Handled = true;
        }

        private async void Panel_Drop(object sender, DragEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            if (e.Data.GetDataPresent(typeof(FileDragData)))   // z drugiego panelu → host robi transfer
            {
                if (e.Data.GetData(typeof(FileDragData)) is FileDragData d && d.Source != this) CrossPaneDrop?.Invoke(d);
                return;
            }
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths != null) await UploadPaths(paths);
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024L * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1048576.0).ToString("0.#") + " MB";
            return (bytes / 1073741824.0).ToString("0.##") + " GB";
        }

        public void DisposePanel()
        {
            // Anuluj transfer W TOKU PRZED zerwaniem połączenia pod nim — bez tego zamknięcie karty w trakcie
            // transferu ucinało go w dowolnym miejscu (pół-skopiowany plik, A2 z przeglądu). Anulowanie jest
            // kooperacyjne (ProgressStream sprawdza je przy każdym kawałku) — znacznie skraca okno wyścigu
            // z wątkiem roboczym, choć bez pełnego async-dispose (poza zakresem tej poprawki) nie eliminuje go całkowicie.
            _transferCts?.Cancel();
            try { _fs?.Dispose(); } catch { }
            _fs = null;
        }
    }
}
