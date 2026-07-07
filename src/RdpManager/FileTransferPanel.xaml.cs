using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RdpManager
{
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

        public FileTransferPanel(Func<IRemoteFs> factory)
        {
            InitializeComponent();
            _factory = factory;
        }

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

        // Wysyła podane pliki do bieżącego katalogu (wspólne dla przycisku i upuszczenia z Eksploratora).
        private async Task UploadPaths(IEnumerable<string> files)
        {
            string dir = _path.TrimEnd('/');
            bool any = false;
            foreach (var file in files)
            {
                if (!System.IO.File.Exists(file)) continue;   // foldery w tej fazie pomijamy
                any = true;
                string name = System.IO.Path.GetFileName(file);
                bool ok = await RunAsync(string.Format(L("S.sftp.uploading"), name), fs =>
                {
                    using (var s = System.IO.File.OpenRead(file))
                        fs.Upload(s, dir + "/" + name, true);
                });
                if (!ok) return;   // błąd przerywa serię (status pokazuje powód)
            }
            if (any) RefreshAsync();
        }

        private async void Download_Click(object sender, RoutedEventArgs e)
        {
            if (!(FileList.SelectedItem is Row r) || r.IsDir) return;
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

        // ---------- Drag&drop z Eksploratora (upload) ----------

        private void Panel_DragEnter(object sender, DragEventArgs e) => UpdateDropEffect(e);
        private void Panel_DragOver(object sender, DragEventArgs e) => UpdateDropEffect(e);
        private void Panel_DragLeave(object sender, DragEventArgs e) => DropOverlay.Visibility = Visibility.Collapsed;

        private void UpdateDropEffect(DragEventArgs e)
        {
            bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
            DropOverlay.Visibility = files ? Visibility.Visible : Visibility.Collapsed;
            e.Handled = true;
        }

        private async void Panel_Drop(object sender, DragEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
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
