using System;
using System.Collections.Generic;
using System.Linq;
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
        }

        private readonly Func<IRemoteFs> _factory;
        private IRemoteFs _fs;
        private string _path = "/";
        private bool _busy;

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

        /// <summary>Zaznaczony wpis (plik lub katalog) — do transferu; false gdy brak zaznaczenia.</summary>
        public bool TryGetSelected(out string full, out string name, out bool isDir)
        {
            full = name = null; isDir = false;
            if (FileList.SelectedItem is Row r) { full = r.FullName; name = r.Name; isDir = r.IsDir; return true; }
            return false;
        }

        /// <summary>Upuszczenie pliku z DRUGIEGO panelu na ten (dual-pane) — host wykonuje transfer.</summary>
        public event Action<FileDragData> CrossPaneDrop;

        /// <summary>Wysyła lokalny plik LUB katalog (rekurencyjnie) do bieżącego katalogu TEGO panelu.</summary>
        public async Task<bool> TransferInLocalFileAsync(string localPath)
        {
            bool isDir = System.IO.Directory.Exists(localPath);
            if (!isDir && !System.IO.File.Exists(localPath)) return false;
            string dir = _path.TrimEnd('/');
            string name = System.IO.Path.GetFileName(localPath.TrimEnd('/', '\\'));
            bool ok = await RunAsync(string.Format(L("S.sftp.uploading"), name),
                fs => UploadTree(fs, localPath, dir, ProgressUp));
            if (ok) RefreshAsync();
            return ok;
        }

        /// <summary>Pobiera plik LUB katalog (rekurencyjnie) z TEGO panelu do lokalnego katalogu (wołający odświeża cel).</summary>
        public Task<bool> TransferOutToLocalAsync(string remoteFull, string remoteName, bool isDir, string localDir)
            => RunAsync(string.Format(L("S.sftp.downloading"), remoteName),
                fs => DownloadTree(fs, remoteFull, remoteName, isDir, localDir.Replace('/', '\\'), ProgressDown));

        // ---------- Rekurencyjny transfer drzew (plik lub katalog); progress per plik ----------

        // Wysyła lokalny plik/katalog do remoteDir na zdalnym fs; katalogi tworzy, w pliki wchodzi rekurencyjnie.
        private static void UploadTree(IRemoteFs fs, string localPath, string remoteDir, Action<string> progress)
        {
            string name = System.IO.Path.GetFileName(localPath.TrimEnd('/', '\\'));
            string target = remoteDir.TrimEnd('/') + "/" + name;
            if (System.IO.Directory.Exists(localPath))
            {
                EnsureRemoteDir(fs, target);
                foreach (var sub in System.IO.Directory.GetDirectories(localPath)) UploadTree(fs, sub, target, progress);
                foreach (var f in System.IO.Directory.GetFiles(localPath)) UploadTree(fs, f, target, progress);
            }
            else
            {
                progress?.Invoke(name);
                using (var s = System.IO.File.OpenRead(localPath)) fs.Upload(s, target, true);
            }
        }

        // Pobiera zdalny plik/katalog do localParentDir (ścieżka Windows); katalogi listuje i schodzi rekurencyjnie.
        private static void DownloadTree(IRemoteFs fs, string remoteFull, string remoteName, bool isDir, string localParentDir, Action<string> progress)
        {
            string dest = System.IO.Path.Combine(localParentDir, remoteName);
            if (isDir)
            {
                System.IO.Directory.CreateDirectory(dest);
                foreach (var e in fs.List(remoteFull)) DownloadTree(fs, e.FullName, e.Name, e.IsDir, dest, progress);
            }
            else
            {
                progress?.Invoke(remoteName);
                using (var s = System.IO.File.Create(dest)) fs.Download(remoteFull, s);
            }
        }

        // SFTP rzuca gdy katalog już istnieje; FTP/Local są idempotentne. Ignorujemy — realny błąd (np. brak
        // uprawnień) i tak wyjdzie przy Upload/List do środka.
        private static void EnsureRemoteDir(IRemoteFs fs, string path)
        {
            try { fs.CreateDirectory(path); } catch { }
        }

        // Status per plik z wątku roboczego → wątek UI (nieblokująco).
        private void ProgressUp(string name) => Dispatcher.BeginInvoke(new Action(() => SetStatus(string.Format(L("S.sftp.uploading"), name))));
        private void ProgressDown(string name) => Dispatcher.BeginInvoke(new Action(() => SetStatus(string.Format(L("S.sftp.downloading"), name))));

        // Jedna operacja naraz; łączy przy pierwszym użyciu (dispatcher wolny — praca w tle).
        // Rozróżnia błąd POŁĄCZENIA (zgłoszony przez Failed → sesja offline) od błędu operacji (tylko status).
        private async Task<bool> RunAsync(string statusText, Action<IRemoteFs> work)
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
                });
                if (didConnect) Connected?.Invoke();
                SetStatus(L("S.sftp.done"));
                return true;
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
                    .OrderByDescending(f => f.IsDir)
                    .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(f => new Row
                    {
                        Icon = f.IsDir ? Wpf.Ui.Controls.SymbolRegular.Folder24 : Wpf.Ui.Controls.SymbolRegular.Document24,
                        IconBrush = f.IsDir ? folder : fileBr,
                        Name = f.Name,
                        FullName = f.FullName,
                        IsDir = f.IsDir,
                        SizeText = f.IsDir ? "" : FormatSize(f.Length),
                        Modified = f.Modified.ToString("yyyy-MM-dd HH:mm")
                    })
                    .ToList();
            });
            if (!ok) return;
            FileList.ItemsSource = rows;
            BuildBreadcrumb();
            SetStatus("");
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
        private async Task UploadPaths(IEnumerable<string> paths)
        {
            string dir = _path.TrimEnd('/');
            bool any = false;
            foreach (var p in paths)
            {
                bool isDir = System.IO.Directory.Exists(p);
                if (!isDir && !System.IO.File.Exists(p)) continue;
                any = true;
                string name = System.IO.Path.GetFileName(p.TrimEnd('/', '\\'));
                bool ok = await RunAsync(string.Format(L("S.sftp.uploading"), name),
                    fs => UploadTree(fs, p, dir, ProgressUp));
                if (!ok) return;   // błąd przerywa serię (status pokazuje powód)
            }
            if (any) RefreshAsync();
        }

        private async void Download_Click(object sender, RoutedEventArgs e)
        {
            if (!(FileList.SelectedItem is Row r)) return;
            if (r.IsDir)   // katalog → wybór folderu docelowego, pobranie rekurencyjne
            {
                var fdlg = new Microsoft.Win32.OpenFolderDialog();
                if (fdlg.ShowDialog() != true) return;
                await RunAsync(string.Format(L("S.sftp.downloading"), r.Name),
                    fs => DownloadTree(fs, r.FullName, r.Name, true, fdlg.FolderName, ProgressDown));
                return;
            }
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = r.Name };
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
            if (!(FileList.SelectedItem is Row r)) return;
            if (MessageBox.Show(string.Format(L("S.sftp.delete.confirm"), r.Name), L("S.sftp.delete"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            bool ok = await RunAsync(L("S.sftp.connecting"), fs => fs.Delete(r.FullName, r.IsDir));   // katalog: tylko pusty
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
            if (!(FileList.SelectedItem is Row r)) return;
            var pos = e.GetPosition(null);
            if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            try { DragDrop.DoDragDrop(FileList, new FileDragData { Source = this, Full = r.FullName, Name = r.Name, IsDir = r.IsDir }, DragDropEffects.Copy); }
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
            try { _fs?.Dispose(); } catch { }
            _fs = null;
        }
    }
}
