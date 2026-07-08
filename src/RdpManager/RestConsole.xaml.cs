using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RdpManager.Models;

namespace RdpManager
{
    /// <summary>
    /// Konsola REST dla jednego wpisu-API („wpis = jedno API/kolekcja"). Lewy panel: drzewo kolekcji
    /// (foldery + żądania); prawy: budowniczy żądania (metoda/URL/params/nagłówki/treść/auth) + podgląd
    /// odpowiedzi. Sekret auth trzymany w Credential Manager (w pamięci sesji jako transient), nigdy w JSON.
    /// Środowiska/zmienne i historia — kolejne PR-y (model już to przewiduje).
    /// </summary>
    public partial class RestConsole : UserControl
    {
        // Węzeł drzewa kolekcji: folder albo żądanie.
        private sealed class RestNode : INotifyPropertyChanged
        {
            public bool IsFolder { get; set; }
            public RestFolder Folder { get; set; }
            public RestRequest Request { get; set; }
            public ObservableCollection<RestNode> Children { get; } = new ObservableCollection<RestNode>();

            public Wpf.Ui.Controls.SymbolRegular Icon =>
                IsFolder ? Wpf.Ui.Controls.SymbolRegular.Folder24 : Wpf.Ui.Controls.SymbolRegular.Globe24;
            public Brush IconBrush =>
                (Brush)(Application.Current?.TryFindResource(IsFolder ? "Accent" : "TextSec")) ?? Brushes.Gray;

            public string Name
            {
                get => IsFolder ? Folder.Name : Request.Name;
                set { if (IsFolder) Folder.Name = value; else Request.Name = value; Raise(nameof(Name)); }
            }

            private bool _selected, _expanded;
            public bool IsSelected { get => _selected; set { _selected = value; Raise(nameof(IsSelected)); } }
            public bool IsExpanded { get => _expanded; set { _expanded = value; Raise(nameof(IsExpanded)); } }

            public event PropertyChangedEventHandler PropertyChanged;
            private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }

        private readonly ServerInfo _server;
        private RestCollection _coll;
        private RestRequest _req;
        private ObservableCollection<RestKeyValue> _params;
        private ObservableCollection<RestKeyValue> _headers;
        private ObservableCollection<RestNode> _roots;
        private CancellationTokenSource _cts;
        private string _rawBody;
        private bool _loading;
        private bool _envLoading;

        private static readonly string[] Methods = { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" };

        private static string L(string key) => LocalizationManager.S(key);

        public RestConsole(ServerInfo server)
        {
            InitializeComponent();
            _server = server;
            _coll = RestStore.For(_server.Id);
            if (string.IsNullOrEmpty(_coll.BaseUrl)) _coll.BaseUrl = _server.Host ?? "";
            if (_coll.Requests.Count == 0)
                _coll.Requests.Add(new RestRequest { Name = "Request 1", Url = _coll.BaseUrl });
            LoadSecrets();
            BuildTree();
            _req = _coll.Requests[0];
            LoadIntoUi(_req);
            SelectNodeFor(_req);
            BuildEnvCombo();
            UrlBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) Send_Click(s, e); };
        }

        // ---------- Środowiska ({{zmienne}}) ----------

        private void BuildEnvCombo()
        {
            _envLoading = true;
            EnvCombo.Items.Clear();
            EnvCombo.Items.Add(new ComboBoxItem { Content = L("S.rest.env.none"), Tag = "" });
            foreach (var env in _coll.Environments)
                EnvCombo.Items.Add(new ComboBoxItem { Content = env.Name, Tag = env.Id });
            int sel = 0;
            for (int i = 1; i < EnvCombo.Items.Count; i++)
                if ((EnvCombo.Items[i] as ComboBoxItem)?.Tag as string == _coll.ActiveEnvironmentId) { sel = i; break; }
            EnvCombo.SelectedIndex = sel;
            _envLoading = false;
        }

        private void EnvCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_envLoading) return;
            _coll.ActiveEnvironmentId = (EnvCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        }

        private void ManageEnv_Click(object sender, RoutedEventArgs e)
        {
            new RestEnvWindow(_coll) { Owner = Window.GetWindow(this) }.ShowDialog();
            if (!_coll.Environments.Any(x => x.Id == _coll.ActiveEnvironmentId)) _coll.ActiveEnvironmentId = "";
            BuildEnvCombo();
        }

        // Zmienne aktywnego środowiska do podstawiania {{klucz}} (null = brak środowiska).
        private IReadOnlyDictionary<string, string> Vars()
        {
            var env = _coll.Environments.FirstOrDefault(x => x.Id == _coll.ActiveEnvironmentId);
            if (env == null) return null;
            var d = new Dictionary<string, string>();
            foreach (var v in env.Variables) if (!string.IsNullOrWhiteSpace(v.Key)) d[v.Key] = v.Value ?? "";
            return d;
        }

        // ---------- Drzewo kolekcji ----------

        private void BuildTree()
        {
            _roots = new ObservableCollection<RestNode>();
            foreach (var f in _coll.Folders.Where(f => string.IsNullOrEmpty(f.ParentId)).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                _roots.Add(BuildFolderNode(f));
            foreach (var r in _coll.Requests.Where(r => string.IsNullOrEmpty(r.FolderId)).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                _roots.Add(new RestNode { IsFolder = false, Request = r });
            CollTree.ItemsSource = _roots;
        }

        private RestNode BuildFolderNode(RestFolder f)
        {
            var node = new RestNode { IsFolder = true, Folder = f, IsExpanded = true };
            foreach (var sub in _coll.Folders.Where(x => x.ParentId == f.Id).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                node.Children.Add(BuildFolderNode(sub));
            foreach (var r in _coll.Requests.Where(x => x.FolderId == f.Id).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                node.Children.Add(new RestNode { IsFolder = false, Request = r });
            return node;
        }

        private void Tree_SelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (_loading || !(e.NewValue is RestNode node) || node.IsFolder) return;
            if (node.Request == _req) return;
            CaptureCurrent();          // zachowaj edycję poprzedniego żądania (w pamięci)
            _req = node.Request;
            LoadIntoUi(_req);
        }

        private RestNode SelectedNode => CollTree.SelectedItem as RestNode;

        // Folder-rodzic dla nowego elementu: wybrany folder, folder wybranego żądania, albo korzeń (null).
        private RestFolder CurrentParentFolder()
        {
            var n = SelectedNode;
            if (n == null) return null;
            return n.IsFolder ? n.Folder : _coll.Folders.FirstOrDefault(f => f.Id == n.Request.FolderId);
        }

        private bool SelectNodeFor(RestRequest req) => SelectIn(_roots, req);

        private bool SelectIn(ObservableCollection<RestNode> nodes, RestRequest req)
        {
            foreach (var n in nodes)
            {
                if (!n.IsFolder && n.Request == req) { n.IsSelected = true; return true; }
                if (n.IsFolder && SelectIn(n.Children, req)) { n.IsExpanded = true; return true; }
            }
            return false;
        }

        // ---------- Dodawanie / zmiana nazwy / usuwanie ----------

        private void AddRequest_Click(object sender, RoutedEventArgs e)
        {
            CaptureCurrent();
            var parent = CurrentParentFolder();
            var req = new RestRequest
            {
                Name = UniqueName(L("S.rest.newreq")),
                Url = _coll.BaseUrl,
                FolderId = parent?.Id ?? ""
            };
            _coll.Requests.Add(req);
            BuildTree();
            _req = req;
            LoadIntoUi(req);
            SelectNodeFor(req);
        }

        private void AddFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new InputDialog(L("S.rest.newfolder"), L("S.rest.newfolder.label"), "") { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true || dlg.Value.Trim().Length == 0) return;
            var parent = CurrentParentFolder();
            _coll.Folders.Add(new RestFolder { Name = dlg.Value.Trim(), ParentId = parent?.Id ?? "" });
            BuildTree();
            if (_req != null) SelectNodeFor(_req);
        }

        private void Rename_Click(object sender, RoutedEventArgs e)
        {
            var node = SelectedNode;
            if (node == null) return;
            var dlg = new InputDialog(L("S.rest.rename"), L("S.rest.rename.label"), node.Name) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true || dlg.Value.Trim().Length == 0) return;
            node.Name = dlg.Value.Trim();
            var keep = _req;
            BuildTree();               // przebuduj (kolejność alfabetyczna)
            if (keep != null) SelectNodeFor(keep);
        }

        private void DeleteNode_Click(object sender, RoutedEventArgs e)
        {
            var node = SelectedNode;
            if (node == null) return;
            if (MessageBox.Show(string.Format(L("S.rest.delete.confirm"), node.Name), L("S.rest.delete"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            bool removedActive = node.IsFolder ? FolderContains(node.Folder, _req) : node.Request == _req;
            if (node.IsFolder) DeleteFolder(node.Folder);
            else DeleteRequest(node.Request);

            if (_coll.Requests.Count == 0)
                _coll.Requests.Add(new RestRequest { Name = "Request 1", Url = _coll.BaseUrl });
            BuildTree();
            if (removedActive) { _req = _coll.Requests[0]; LoadIntoUi(_req); }
            SelectNodeFor(_req);
        }

        private bool FolderContains(RestFolder f, RestRequest req)
        {
            if (req == null) return false;
            if (req.FolderId == f.Id) return true;
            return _coll.Folders.Where(x => x.ParentId == f.Id).Any(sub => FolderContains(sub, req));
        }

        private void DeleteFolder(RestFolder f)
        {
            foreach (var sub in _coll.Folders.Where(x => x.ParentId == f.Id).ToList()) DeleteFolder(sub);
            foreach (var r in _coll.Requests.Where(x => x.FolderId == f.Id).ToList()) DeleteRequest(r);
            _coll.Folders.Remove(f);
        }

        private void DeleteRequest(RestRequest r)
        {
            CredentialStore.Delete(r.AuthCredTarget);
            _coll.Requests.Remove(r);
        }

        private string UniqueName(string baseName)
        {
            var names = new System.Collections.Generic.HashSet<string>(_coll.Requests.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
            if (!names.Contains(baseName)) return baseName;
            for (int i = 2; ; i++) { string n = baseName + " " + i; if (!names.Contains(n)) return n; }
        }

        // ---------- Wczytanie / zapis żądania ----------

        private void LoadSecrets()
        {
            foreach (var r in _coll.Requests)
                r.AuthSecret = CredentialStore.TryRead(r.AuthCredTarget, out var s) ? s : "";
        }

        private void LoadIntoUi(RestRequest req)
        {
            _loading = true;
            SelectMethod(req.Method);
            UrlBox.Text = req.Url;
            _params = new ObservableCollection<RestKeyValue>(req.QueryParams);
            _headers = new ObservableCollection<RestKeyValue>(req.Headers);
            ParamsList.ItemsSource = _params;
            HeadersList.ItemsSource = _headers;
            BodyBox.Text = req.Body;
            SelectContentType(req.BodyContentType);
            AuthCombo.SelectedIndex = Math.Clamp(req.AuthType, 0, 2);
            AuthUserBox.Text = req.AuthUsername;
            TokenBox.Password = req.AuthType == 1 ? (req.AuthSecret ?? "") : "";
            AuthPassBox.Password = req.AuthType == 2 ? (req.AuthSecret ?? "") : "";
            UpdateAuthPanels();
            ClearResponse();
            _loading = false;
        }

        // Zbiera stan formularza do aktywnego żądania (przed przełączeniem/wysyłką/zapisem). Sekret → transient.
        private void CaptureCurrent()
        {
            if (_req == null) return;
            _req.Method = SelectedMethod();
            _req.Url = (UrlBox.Text ?? "").Trim();
            _req.QueryParams = _params.ToList();
            _req.Headers = _headers.ToList();
            _req.Body = BodyBox.Text ?? "";
            _req.BodyContentType = SelectedContentType();
            _req.AuthType = AuthCombo.SelectedIndex < 0 ? 0 : AuthCombo.SelectedIndex;
            _req.AuthUsername = AuthUserBox.Text ?? "";
            _req.AuthSecret = SecretFromUi();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            CaptureCurrent();
            foreach (var r in _coll.Requests)   // sekrety całej kolekcji → Credential Manager
            {
                if (r.AuthType == 0 || string.IsNullOrEmpty(r.AuthSecret)) CredentialStore.Delete(r.AuthCredTarget);
                else CredentialStore.TrySave(r.AuthCredTarget, r.AuthUsername, r.AuthSecret);
            }
            RestStore.Put(_server.Id, _coll);
            SetStatus(L("S.rest.saved"));
        }

        private string SecretFromUi()
            => AuthCombo.SelectedIndex == 1 ? TokenBox.Password
             : AuthCombo.SelectedIndex == 2 ? AuthPassBox.Password
             : "";

        // ---------- Wysyłka ----------

        private async void Send_Click(object sender, RoutedEventArgs e)
        {
            CaptureCurrent();
            if (string.IsNullOrWhiteSpace(_req.Url)) { SetStatus(L("S.rest.needurl"), error: true); return; }

            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            SendBtn.IsEnabled = false;
            SetStatus(L("S.rest.sending"));

            var resp = await RestClient.SendAsync(_req, _req.AuthSecret, Vars(), ct);
            if (ct.IsCancellationRequested) return;   // nowsze żądanie przejęło przycisk

            RenderResponse(resp);
            SendBtn.IsEnabled = true;
            SetStatus("");
        }

        private void RenderResponse(RestResponse r)
        {
            StatusPill.Visibility = Visibility.Visible;
            if (!r.Ok)
            {
                StatusPill.Background = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
                StatusPillText.Text = L("S.rest.err");
                MetaText.Text = string.Format(L("S.rest.error"), r.Error);
                _rawBody = r.Error ?? "";
                ResponseBody.Text = _rawBody;
                ResponseHeaders.ItemsSource = null;
                ResponseTabs.SelectedIndex = 0;
                return;
            }

            StatusPill.Background = PillBrush(r.Status);
            StatusPillText.Text = r.Status + (string.IsNullOrEmpty(r.ReasonPhrase) ? "" : " " + r.ReasonPhrase);
            MetaText.Text = r.ElapsedMs + " ms · " + FormatSize(r.Size);
            _rawBody = r.Body ?? "";
            ResponseBody.Text = FormatBody();
            ResponseHeaders.ItemsSource = r.Headers;
        }

        private void ClearResponse()
        {
            _rawBody = null;
            StatusPill.Visibility = Visibility.Collapsed;
            MetaText.Text = "";
            ResponseHeaders.ItemsSource = null;
            ResponseBody.Text = L("S.rest.resp.empty");
        }

        private string FormatBody()
        {
            if (string.IsNullOrEmpty(_rawBody)) return "";
            if (PrettyToggle.IsChecked != true) return _rawBody;
            string t = _rawBody.TrimStart();
            if (t.Length == 0 || (t[0] != '{' && t[0] != '[')) return _rawBody;   // tylko JSON
            try
            {
                using (var doc = JsonDocument.Parse(_rawBody))
                    return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
            }
            catch { return _rawBody; }
        }

        private void Pretty_Changed(object sender, RoutedEventArgs e)
        {
            if (_rawBody != null) ResponseBody.Text = FormatBody();
        }

        private void CopyBody_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(ResponseBody.Text ?? ""); } catch { }
        }

        // ---------- Wiersze klucz/wartość ----------

        private void AddParam_Click(object sender, RoutedEventArgs e) => _params.Add(new RestKeyValue());
        private void AddHeader_Click(object sender, RoutedEventArgs e) => _headers.Add(new RestKeyValue());

        private void DeleteKv_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is RestKeyValue kv && !_params.Remove(kv)) _headers.Remove(kv);
        }

        // ---------- Metoda / typ treści / auth ----------

        private void Method_Changed(object sender, SelectionChangedEventArgs e)
            => MethodCombo.Foreground = MethodBrush(SelectedMethod());

        private void Auth_Changed(object sender, SelectionChangedEventArgs e) => UpdateAuthPanels();

        private void UpdateAuthPanels()
        {
            int i = AuthCombo.SelectedIndex;
            BearerPanel.Visibility = i == 1 ? Visibility.Visible : Visibility.Collapsed;
            BasicPanel.Visibility = i == 2 ? Visibility.Visible : Visibility.Collapsed;
        }

        private string SelectedMethod() => (MethodCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "GET";

        private void SelectMethod(string method)
        {
            int idx = Array.IndexOf(Methods, (method ?? "GET").ToUpperInvariant());
            MethodCombo.SelectedIndex = idx < 0 ? 0 : idx;
        }

        private string SelectedContentType() => (ContentTypeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "application/json";

        private void SelectContentType(string ct)
        {
            foreach (var obj in ContentTypeCombo.Items)
                if (obj is ComboBoxItem it && string.Equals(it.Content?.ToString(), ct, StringComparison.OrdinalIgnoreCase))
                { ContentTypeCombo.SelectedItem = it; return; }
            ContentTypeCombo.SelectedIndex = 0;
        }

        // ---------- Pomocnicze ----------

        private void SetStatus(string text, bool error = false)
        {
            StatusText.Text = text ?? "";
            StatusText.Foreground = (Brush)(error ? TryFindResource("Danger") : TryFindResource("TextTer")) ?? Brushes.Gray;
        }

        private static SolidColorBrush PillBrush(int status)
        {
            if (status >= 200 && status < 300) return new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43));   // zielony
            if (status >= 300 && status < 400) return new SolidColorBrush(Color.FromRgb(0xF0, 0x9A, 0x1A));   // bursztyn
            if (status >= 400) return new SolidColorBrush(Color.FromRgb(0xD1, 0x3B, 0x3B));                    // czerwony
            return new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
        }

        private static Brush MethodBrush(string method)
        {
            switch (method)
            {
                case "GET": return new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43));
                case "POST": return new SolidColorBrush(Color.FromRgb(0xF0, 0x9A, 0x1A));
                case "PUT": return new SolidColorBrush(Color.FromRgb(0x2B, 0x7C, 0xD3));
                case "PATCH": return new SolidColorBrush(Color.FromRgb(0x8A, 0x4F, 0xC7));
                case "DELETE": return new SolidColorBrush(Color.FromRgb(0xD1, 0x3B, 0x3B));
                default: return new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
            }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024L * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
            return (bytes / 1048576.0).ToString("0.#") + " MB";
        }

        /// <summary>Anuluje żądanie w locie przy zamykaniu sesji.</summary>
        public void DisposeConsole()
        {
            try { _cts?.Cancel(); _cts?.Dispose(); } catch { }
            _cts = null;
        }
    }
}
