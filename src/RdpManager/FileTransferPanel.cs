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
    /// Panel plików zdalnego systemu (<see cref="IRemoteFs"/>): nawigacja, wyślij/pobierz, nowy folder,
    /// usuwanie. Działa dla SFTP (i docelowo FTP/FTPS) — źródło dostarcza fabryka. Operacje w tle,
    /// UI przez Dispatcher, jedna naraz. Jako samodzielna sesja zgłasza zdarzenia
    /// <see cref="Connected"/>/<see cref="Failed"/> (stan karty/sesji); błąd pojedynczej operacji
    /// NIE zrywa sesji — tylko błąd łączenia.
    /// </summary>
    public class FileTransferPanel : Border
    {
        private sealed class Row
        {
            public string Display { get; set; }    // „📁 nazwa" / „📄 nazwa"
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

        private readonly TextBlock _pathText;
        private readonly TextBlock _status;
        private readonly ListView _list;

        private static string L(string key) => LocalizationManager.S(key);
        private static Brush Res(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;

        public FileTransferPanel(Func<IRemoteFs> factory)
        {
            _factory = factory;
            Background = Res("Panel");
            BorderBrush = Res("Border");
            BorderThickness = new Thickness(1, 0, 0, 0);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // ścieżka
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // przyciski
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // status

            _pathText = new TextBlock
            {
                Foreground = Res("TextSec"),
                FontFamily = Application.Current.TryFindResource("Mono") as FontFamily,
                FontSize = 11.5,
                Margin = new Thickness(10, 8, 10, 4),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetRow(_pathText, 0);
            root.Children.Add(_pathText);

            var bar = new WrapPanel { Margin = new Thickness(8, 0, 8, 6) };
            bar.Children.Add(MakeBtn("↑", L("S.sftp.up"), (s, e) => GoUp()));
            bar.Children.Add(MakeBtn("⟳", L("S.sftp.refresh"), (s, e) => RefreshAsync()));
            bar.Children.Add(MakeBtn(L("S.sftp.upload"), null, (s, e) => UploadAsync()));
            bar.Children.Add(MakeBtn(L("S.sftp.download"), null, (s, e) => DownloadSelectedAsync()));
            bar.Children.Add(MakeBtn(L("S.sftp.newfolder"), null, (s, e) => NewFolderAsync()));
            bar.Children.Add(MakeBtn(L("S.sftp.delete"), null, (s, e) => DeleteSelectedAsync()));
            Grid.SetRow(bar, 1);
            root.Children.Add(bar);

            _list = new ListView
            {
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = Res("TextPrim"),
                FontSize = 12
            };
            var gv = new GridView();
            gv.Columns.Add(new GridViewColumn
            { Header = L("S.sftp.name"), Width = 170, DisplayMemberBinding = new System.Windows.Data.Binding("Display") });
            gv.Columns.Add(new GridViewColumn
            { Header = L("S.sftp.size"), Width = 70, DisplayMemberBinding = new System.Windows.Data.Binding("SizeText") });
            gv.Columns.Add(new GridViewColumn
            { Header = L("S.sftp.modified"), Width = 105, DisplayMemberBinding = new System.Windows.Data.Binding("Modified") });
            _list.View = gv;
            _list.MouseDoubleClick += (s, e) =>
            {
                if (_list.SelectedItem is Row r)
                {
                    if (r.IsDir) { _path = r.FullName; RefreshAsync(); }
                    else DownloadSelectedAsync();
                }
            };
            Grid.SetRow(_list, 2);
            root.Children.Add(_list);

            _status = new TextBlock
            {
                Foreground = Res("TextTer"),
                FontSize = 11,
                Margin = new Thickness(10, 4, 10, 8),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetRow(_status, 3);
            root.Children.Add(_status);

            Child = root;
        }

        private static Wpf.Ui.Controls.Button MakeBtn(string content, string tip, RoutedEventHandler click)
        {
            var b = new Wpf.Ui.Controls.Button
            {
                Content = content,
                Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
                FontSize = 11.5,
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 0, 4, 4),
                ToolTip = tip
            };
            b.Click += click;
            return b;
        }

        private void SetStatus(string text, bool error = false)
        {
            _status.Text = text ?? "";
            _status.Foreground = error ? Res("Danger") : Res("TextTer");
            _status.ToolTip = string.IsNullOrEmpty(text) ? null : text;
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

        /// <summary>Odświeża listing bieżącego katalogu (łączy przy pierwszym wywołaniu).</summary>
        public async void RefreshAsync()
        {
            List<Row> rows = null;
            bool ok = await RunAsync(L("S.sftp.connecting"), fs =>
            {
                rows = fs.List(_path)
                    .OrderByDescending(f => f.IsDir)
                    .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(f => new Row
                    {
                        Display = (f.IsDir ? "\U0001F4C1 " : "\U0001F4C4 ") + f.Name,
                        Name = f.Name,
                        FullName = f.FullName,
                        IsDir = f.IsDir,
                        SizeText = f.IsDir ? "" : FormatSize(f.Length),
                        Modified = f.Modified.ToString("yyyy-MM-dd HH:mm")
                    })
                    .ToList();
            });
            if (!ok) return;
            _list.ItemsSource = rows;
            _pathText.Text = _path;
            _pathText.ToolTip = _path;
            SetStatus("");
        }

        private void GoUp()
        {
            string p = _path.TrimEnd('/');
            int i = p.LastIndexOf('/');
            _path = i <= 0 ? "/" : p.Substring(0, i);
            RefreshAsync();
        }

        private async void UploadAsync()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true };
            if (dlg.ShowDialog() != true) return;

            string dir = _path.TrimEnd('/');
            foreach (var file in dlg.FileNames)
            {
                string name = System.IO.Path.GetFileName(file);
                bool ok = await RunAsync(string.Format(L("S.sftp.uploading"), name), fs =>
                {
                    using (var s = System.IO.File.OpenRead(file))
                        fs.Upload(s, dir + "/" + name, true);
                });
                if (!ok) return;   // błąd przerywa serię (status już pokazuje powód)
            }
            RefreshAsync();
        }

        private async void DownloadSelectedAsync()
        {
            if (!(_list.SelectedItem is Row r) || r.IsDir) return;
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = r.Name };
            if (dlg.ShowDialog() != true) return;

            await RunAsync(string.Format(L("S.sftp.downloading"), r.Name), fs =>
            {
                using (var s = System.IO.File.Create(dlg.FileName))
                    fs.Download(r.FullName, s);
            });
        }

        private async void NewFolderAsync()
        {
            var dlg = new InputDialog(L("S.sftp.newfolder"), L("S.sftp.newfolder.label"), "")
            { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true || dlg.Value.Length == 0) return;

            string name = dlg.Value;
            bool ok = await RunAsync(L("S.sftp.connecting"), fs => fs.CreateDirectory(_path.TrimEnd('/') + "/" + name));
            if (ok) RefreshAsync();
        }

        private async void DeleteSelectedAsync()
        {
            if (!(_list.SelectedItem is Row r)) return;
            if (MessageBox.Show(string.Format(L("S.sftp.delete.confirm"), r.Name), L("S.sftp.delete"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            bool ok = await RunAsync(L("S.sftp.connecting"), fs => fs.Delete(r.FullName, r.IsDir));   // katalog: tylko pusty
            if (ok) RefreshAsync();
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
