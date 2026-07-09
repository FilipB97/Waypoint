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

            // Folder → ikona folderu; żądanie → kolorowy tag metody (GET/POST…) zamiast kuli.
            public Visibility FolderIconVis => IsFolder ? Visibility.Visible : Visibility.Collapsed;
            public Visibility MethodVis => IsFolder ? Visibility.Collapsed : Visibility.Visible;
            public string MethodText => IsFolder ? "" : (Request.Method ?? "GET").ToUpperInvariant();
            public Brush MethodBrush => IsFolder ? Brushes.Transparent : RestConsole.MethodBrush(Request.Method);
            public Brush MethodBadgeBg => IsFolder ? Brushes.Transparent : RestConsole.MethodBadgeBg(Request.Method);

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
        private ObservableCollection<RestKeyValue> _formFields;
        private ObservableCollection<RestNode> _roots;
        private ObservableCollection<RestHistoryEntry> _history;
        private CancellationTokenSource _cts;
        private string _rawBody;
        // Zmienne ustawione przez skrypt gdy BRAK aktywnego środowiska (sesyjne; z env-em skrypt pisze do env).
        private readonly Dictionary<string, string> _scriptVars = new Dictionary<string, string>();
        // Środowiska są GLOBALNE (EnvironmentStore) — wspólne dla wszystkich kolekcji. Kolekcja tylko
        // wskazuje wybrane przez _coll.ActiveEnvironmentId. Trzymane w pamięci na czas życia konsoli.
        private List<RestEnvironment> _envs;
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
            _envs = EnvironmentStore.Load();   // środowiska globalne (wspólne dla wszystkich kolekcji)
            BuildEnvCombo();
            _history = new ObservableCollection<RestHistoryEntry>(_coll.History);
            HistoryList.ItemsSource = _history;
            UrlBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) Send_Click(s, e); };
        }

        // ---------- Historia ----------

        private void History_Toggled(object sender, RoutedEventArgs e) => ShowHistory(HistoryToggle.IsChecked == true);

        private void ShowHistory(bool on)
        {
            HistoryToggle.IsChecked = on;
            CollTree.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            HistoryList.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }

        // Dwuklik wpisu historii → nowe żądanie w kolekcji z tą metodą i URL (bez nadpisywania bieżącego).
        private void History_Activate(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!(HistoryList.SelectedItem is RestHistoryEntry h)) return;
            CaptureCurrent();
            var req = new RestRequest { Name = UniqueName(HistName(h)), Method = h.Method, Url = h.Url };
            _coll.Requests.Add(req);
            BuildTree();
            _req = req;
            LoadIntoUi(req);
            ShowHistory(false);
            SelectNodeFor(req);
        }

        private void RecordHistory(RestResponse r, RestRequest used)
        {
            _history.Insert(0, new RestHistoryEntry
            {
                Method = used.Method,
                Url = used.Url,
                Status = r.Ok ? r.Status : 0,
                ElapsedMs = r.ElapsedMs,
                WhenIso = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            });
            while (_history.Count > 50) _history.RemoveAt(_history.Count - 1);   // ostatnie 50
            _coll.History = _history.ToList();
            RestStore.Put(_server.Id, _coll);   // historia utrwalana od razu (wraz z zapamiętanym żądaniem)
        }

        private static string HistName(RestHistoryEntry h)
        {
            try
            {
                var u = new Uri(h.Url.Contains("://") ? h.Url : "https://" + h.Url);
                var seg = u.Segments.LastOrDefault()?.Trim('/');
                return string.IsNullOrEmpty(seg) ? u.Host : seg;
            }
            catch { return string.IsNullOrWhiteSpace(h.Method) ? "Request" : h.Method; }
        }

        // ---------- Środowiska ({{zmienne}}) ----------

        private void BuildEnvCombo()
        {
            _envLoading = true;
            EnvCombo.Items.Clear();
            EnvCombo.Items.Add(new ComboBoxItem { Content = L("S.rest.env.none"), Tag = "" });
            foreach (var env in _envs)
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
            RecolorAllVars();   // inne środowisko = inne znane zmienne → przelicz kolory pól
        }

        private void ManageEnv_Click(object sender, RoutedEventArgs e)
        {
            // Edytor operuje na GLOBALNYM store — po zamknięciu przeładuj listę i napraw wybór, gdy aktywne
            // środowisko zostało usunięte. Wybór (ActiveEnvironmentId) jest per-kolekcja, więc utrwalamy kolekcję.
            new RestEnvWindow { Owner = Window.GetWindow(this) }.ShowDialog();
            _envs = EnvironmentStore.Load();
            if (!_envs.Any(x => x.Id == _coll.ActiveEnvironmentId)) _coll.ActiveEnvironmentId = "";
            BuildEnvCombo();
            CaptureCurrent();
            RestStore.Put(_server.Id, _coll);
            RecolorAllVars();   // zmienne mogły dojść/zniknąć w edytorze
        }

        private RestEnvironment ActiveEnv() => _envs?.FirstOrDefault(x => x.Id == _coll.ActiveEnvironmentId);

        // Zmienne do podstawiania {{klucz}}: aktywne środowisko + nakładka zmiennych ustawionych przez skrypt.
        private IReadOnlyDictionary<string, string> Vars()
        {
            var d = new Dictionary<string, string>();
            var env = ActiveEnv();
            if (env != null)
                foreach (var v in env.Variables) if (!string.IsNullOrWhiteSpace(v.Key)) d[v.Key] = v.Value ?? "";
            foreach (var kv in _scriptVars) d[kv.Key] = kv.Value;   // skrypt (bez env) nadpisuje
            return d.Count > 0 ? d : null;
        }

        // Odczyt/zapis zmiennej ze skryptu: gdy jest aktywne środowisko → pisz do niego (utrwali się z kolekcją),
        // w przeciwnym razie do sesyjnego _scriptVars.
        private string GetScriptVar(string key)
        {
            var env = ActiveEnv();
            var v = env?.Variables.FirstOrDefault(x => x.Key == key);
            if (v != null) return v.Value ?? "";
            return _scriptVars.TryGetValue(key, out var s) ? s : "";
        }

        private void SetScriptVar(string key, string val)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            var env = ActiveEnv();
            if (env == null) { _scriptVars[key] = val ?? ""; return; }
            var v = env.Variables.FirstOrDefault(x => x.Key == key);
            if (v == null) env.Variables.Add(new RestVariable { Key = key, Value = val ?? "" });
            else v.Value = val ?? "";
            EnvironmentStore.Save(_envs);   // środowiska są globalne — utrwal od razu (nie zależy od RestStore.Put)
        }

        private void UnsetScriptVar(string key)
        {
            _scriptVars.Remove(key);
            var env = ActiveEnv();
            if (env != null && env.Variables.RemoveAll(x => x.Key == key) > 0) EnvironmentStore.Save(_envs);
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
            if (CollCount != null) CollCount.Text = _coll.Requests.Count.ToString();
        }

        /// <summary>Zaznacza żądanie po Id (moduł REST w railu otwiera konkretne żądanie w tej konsoli).</summary>
        public void SelectRequestById(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            var req = _coll.Requests.FirstOrDefault(r => r.Id == id);
            if (req == null) return;
            _req = req;
            LoadIntoUi(req);
            SelectNodeFor(req);
        }

        private RestNode BuildFolderNode(RestFolder f) => BuildFolderNode(f, new HashSet<string>());

        // visited: broni przed zapętleniem po cyklicznym/zduplikowanym ParentId (np. ręcznie edytowany
        // JSON albo relikt starego buga klonowania folderów) — A9 z przeglądu.
        private RestNode BuildFolderNode(RestFolder f, HashSet<string> visited)
        {
            var node = new RestNode { IsFolder = true, Folder = f, IsExpanded = true };
            if (!visited.Add(f.Id)) return node;
            foreach (var sub in _coll.Folders.Where(x => x.ParentId == f.Id).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                node.Children.Add(BuildFolderNode(sub, visited));
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

        private bool FolderContains(RestFolder f, RestRequest req) => FolderContains(f, req, new HashSet<string>());

        private bool FolderContains(RestFolder f, RestRequest req, HashSet<string> visited)
        {
            if (req == null || !visited.Add(f.Id)) return false;
            if (req.FolderId == f.Id) return true;
            return _coll.Folders.Where(x => x.ParentId == f.Id).Any(sub => FolderContains(sub, req, visited));
        }

        private void DeleteFolder(RestFolder f) => DeleteFolder(f, new HashSet<string>());

        // visited: jak w BuildFolderNode — cykliczny ParentId nie może zapętlić kasowania (A9 z przeglądu).
        private void DeleteFolder(RestFolder f, HashSet<string> visited)
        {
            if (!visited.Add(f.Id)) return;
            foreach (var sub in _coll.Folders.Where(x => x.ParentId == f.Id).ToList()) DeleteFolder(sub, visited);
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

        // Cel w Credential Managerze dla auth CAŁEJ kolekcji (korzeń dziedziczenia) — kolekcja nie zna
        // własnego Id (kluczowana zewnętrznie po Id wpisu), więc liczymy to tutaj z _server.Id.
        private string CollAuthCredTarget => "RdpManager:restcoll:" + _server.Id;

        private void LoadSecrets()
        {
            foreach (var r in _coll.Requests)
                r.AuthSecret = CredentialStore.TryRead(r.AuthCredTarget, out var s) ? s : "";
            foreach (var f in _coll.Folders)
                f.AuthSecret = CredentialStore.TryRead(f.AuthCredTarget, out var s) ? s : "";
            _coll.AuthSecret = CredentialStore.TryRead(CollAuthCredTarget, out var cs) ? cs : "";
        }

        private void CollectionAuth_Click(object sender, RoutedEventArgs e)
        {
            bool committed = new RestAuthWindow(_coll, CollAuthCredTarget) { Owner = Window.GetWindow(this) }.ShowDialog() == true;
            if (committed) RestStore.Put(_server.Id, _coll);   // od razu, nie czekamy na „Zapisz" (lekcja PR #100)
            UpdateAuthPanels();   // opis dziedziczenia mógł się zmienić, jeśli aktywne żądanie ma "Dziedzicz"
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
            bool isForm = string.Equals(req.BodyContentType, "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
            _formFields = new ObservableCollection<RestKeyValue>(
                req.FormFields.Count > 0 ? req.FormFields : isForm ? ParseLegacyFormBody(req.Body) : new List<RestKeyValue>());
            FormFieldsList.ItemsSource = _formFields;
            UpdateBodyMode();
            AuthCombo.SelectedIndex = Math.Clamp(req.AuthType, 0, 3);
            AuthUserBox.Text = req.AuthUsername;
            TokenBox.Text = req.AuthType == 1 ? (req.AuthSecret ?? "") : "";
            AuthPassBox.Password = req.AuthType == 2 ? (req.AuthSecret ?? "") : "";
            PreScriptBox.Text = req.PreScript ?? "";
            TestScriptBox.Text = req.TestScript ?? "";
            UpdateAuthPanels();
            ClearResponse();
            _loading = false;
            // Kolory zmiennych w komórkach tabel — kontenery wierszy powstają asynchronicznie, więc po layoucie.
            Dispatcher.BeginInvoke(new Action(RecolorAllVars), System.Windows.Threading.DispatcherPriority.Loaded);
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
            _req.FormFields = _formFields.ToList();
            _req.AuthType = AuthCombo.SelectedIndex < 0 ? 0 : AuthCombo.SelectedIndex;
            _req.AuthUsername = AuthUserBox.Text ?? "";
            _req.AuthSecret = SecretFromUi();
            _req.PreScript = PreScriptBox.Text ?? "";
            _req.TestScript = TestScriptBox.Text ?? "";
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
            => AuthCombo.SelectedIndex == 1 ? TokenBox.Text
             : AuthCombo.SelectedIndex == 2 ? AuthPassBox.Password
             : "";

        // ---------- Kolor zmiennych {{...}} (niebieski = wszystkie znane w środowisku, czerwony = brak) ----------

        private void VarCell_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox tb) ColorizeVars(tb);
        }

        // Pole bez zmiennych wraca do koloru ze stylu (ClearValue); ze zmiennymi — całe pole sygnalizuje
        // stan: wszystkie znane → AccentBright, jakakolwiek nieznana → Danger. Wartość pokazuje tooltip.
        private void ColorizeVars(TextBox tb)
        {
            if (tb == null) return;
            string t = tb.Text ?? "";
            if (!RestClient.HasVars(t)) { tb.ClearValue(Control.ForegroundProperty); return; }
            bool missing = RestClient.MissingVars(t, Vars()).Count > 0;
            tb.Foreground = (Brush)TryFindResource(missing ? "Danger" : "AccentBright") ?? tb.Foreground;
        }

        // Po zmianie środowiska / załadowaniu żądania — przelicz kolory we wszystkich polach ze zmiennymi.
        private void RecolorAllVars()
        {
            ColorizeVars(UrlBox);
            ColorizeVars(TokenBox);
            ColorizeVars(AuthUserBox);
            foreach (var host in new ItemsControl[] { ParamsList, HeadersList, FormFieldsList })
                foreach (var tb in Descendants<TextBox>(host)) ColorizeVars(tb);
        }

        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) yield break;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var c = VisualTreeHelper.GetChild(root, i);
                if (c is T t) yield return t;
                foreach (var d in Descendants<T>(c)) yield return d;
            }
        }

        // ---------- Wysyłka ----------

        private const int ScriptTabIndex = 2;   // Body, Nagłówki, Skrypt

        private async void Send_Click(object sender, RoutedEventArgs e)
        {
            CaptureCurrent();

            // Skrypty i wysyłka na KOPII żądania — mutacje z pre-skryptu nie brudzą zapisanego żądania.
            var eff = CloneForSend(_req);
            // AuthType=Dziedzicz na klonie zastępujemy już ROZWIĄZANYM (folder → nadfolder → kolekcja) —
            // RestClient nie zna dziedziczenia, dostaje zawsze gotowy, jawny typ.
            (eff.AuthType, eff.AuthUsername, eff.AuthSecret) = ResolveEffectiveAuth(_req);
            var pre = RestScript.Run(_req.PreScript, eff, null, GetScriptVar, SetScriptVar, UnsetScriptVar);
            if (!pre.Ok)
            {
                ShowScriptOutput(pre, null);
                ResponseTabs.SelectedIndex = ScriptTabIndex;
                SetStatus(string.Format(L("S.rest.script.err"), pre.Error), error: true);
                return;
            }
            if (string.IsNullOrWhiteSpace(eff.Url)) { SetStatus(L("S.rest.needurl"), error: true); return; }

            _cts?.Cancel();
            _cts?.Dispose();   // stary token nikomu już niepotrzebny — bez tego wyciekał przy każdym Send (A7)
            var cts = new CancellationTokenSource();
            _cts = cts;
            SendBtn.IsEnabled = false;
            SetStatus(L("S.rest.sending"));

            try
            {
                var resp = await RestClient.SendAsync(eff, eff.AuthSecret, Vars(), cts.Token);
                if (cts.Token.IsCancellationRequested) return;   // nowsze żądanie przejęło przycisk (finally i tak nie odblokuje)

                RenderResponse(resp);
                var post = RestScript.Run(_req.TestScript, eff, resp, GetScriptVar, SetScriptVar, UnsetScriptVar);
                ShowScriptOutput(pre, post);
                if (!post.Ok || post.Tests.Any(t => !t.Passed)) ResponseTabs.SelectedIndex = ScriptTabIndex;
                RecordHistory(resp, eff);
                SetStatus(ScriptSummary(post));
            }
            catch (Exception ex)
            {
                // Nieoczekiwany błąd PO wysyłce (np. RecordHistory→RestStore.Put przy pełnym/zablokowanym
                // dysku) — bez tego catch+finally SendBtn zostawałby zablokowany na stałe (A6/A7).
                SetStatus(string.Format(L("S.rest.error"), ex.Message), error: true);
            }
            finally
            {
                if (!cts.Token.IsCancellationRequested) SendBtn.IsEnabled = true;
            }
        }

        // Kopia żądania do skryptów+wysyłki (nie brudzi zapisanego modelu).
        private static RestRequest CloneForSend(RestRequest r) => new RestRequest
        {
            Id = r.Id, Name = r.Name, Method = r.Method, Url = r.Url,
            Body = r.Body, BodyContentType = r.BodyContentType,
            AuthType = r.AuthType, AuthUsername = r.AuthUsername, AuthSecret = r.AuthSecret,
            QueryParams = r.QueryParams.Select(p => new RestKeyValue { Enabled = p.Enabled, Key = p.Key, Value = p.Value }).ToList(),
            Headers = r.Headers.Select(p => new RestKeyValue { Enabled = p.Enabled, Key = p.Key, Value = p.Value }).ToList(),
            FormFields = r.FormFields.Select(p => new RestKeyValue { Enabled = p.Enabled, Key = p.Key, Value = p.Value }).ToList()
        };

        private void ShowScriptOutput(ScriptOutcome pre, ScriptOutcome post)
        {
            var lines = new List<string>();
            AppendOutcome(lines, L("S.rest.script.pre"), pre);
            AppendOutcome(lines, L("S.rest.script.post"), post);
            ScriptOutput.Text = string.Join("\n", lines);
        }

        private static void AppendOutcome(List<string> lines, string label, ScriptOutcome o)
        {
            if (o == null || (o.Ok && o.IsEmpty)) return;
            if (lines.Count > 0) lines.Add("");
            lines.Add("— " + label + " —");
            if (!o.Ok) lines.Add("⚠ " + o.Error);
            foreach (var t in o.Tests) lines.Add((t.Passed ? "✓ " : "✗ ") + t.Name + (t.Passed ? "" : "  — " + t.Error));
            foreach (var log in o.Logs) lines.Add(log);
        }

        private static string ScriptSummary(ScriptOutcome post)
        {
            if (post == null || post.Tests.Count == 0) return "";
            return string.Format(L("S.rest.script.testsummary"), post.Tests.Count(t => t.Passed), post.Tests.Count);
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
                SetResponseBody(_rawBody, allowColor: false);
                ResponseHeaders.ItemsSource = null;
                ResponseTabs.SelectedIndex = 0;
                return;
            }

            StatusPill.Background = PillBrush(r.Status);
            StatusPillText.Text = r.Status + (string.IsNullOrEmpty(r.ReasonPhrase) ? "" : " " + r.ReasonPhrase);
            MetaText.Text = r.ElapsedMs + " ms · " + FormatSize(r.Size);
            _rawBody = r.Body ?? "";
            SetResponseBody(FormatBody(), allowColor: true);
            ResponseHeaders.ItemsSource = r.Headers;
        }

        private void ClearResponse()
        {
            _rawBody = null;
            StatusPill.Visibility = Visibility.Collapsed;
            MetaText.Text = "";
            ResponseHeaders.ItemsSource = null;
            SetResponseBody(L("S.rest.resp.empty"), allowColor: false);
            ScriptOutput.Text = "";
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
            if (_rawBody != null) SetResponseBody(FormatBody(), allowColor: true);
        }

        private void CopyBody_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var doc = ResponseBody.Document;
                var text = new System.Windows.Documents.TextRange(doc.ContentStart, doc.ContentEnd).Text;
                Clipboard.SetText(text ?? "");
            }
            catch { }
        }

        // ---------- Wiersze klucz/wartość ----------

        private void AddParam_Click(object sender, RoutedEventArgs e) => _params.Add(new RestKeyValue());
        private void AddHeader_Click(object sender, RoutedEventArgs e) => _headers.Add(new RestKeyValue());
        private void AddFormField_Click(object sender, RoutedEventArgs e) => _formFields.Add(new RestKeyValue());

        private void DeleteKv_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is RestKeyValue kv)) return;
            if (_params.Remove(kv)) return;
            if (_headers.Remove(kv)) return;
            _formFields.Remove(kv);
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
            bool inherit = i == RestAuthResolve.Inherit;
            InheritHint.Visibility = inherit ? Visibility.Visible : Visibility.Collapsed;
            if (inherit && _req != null) InheritHint.Text = DescribeInheritedAuth(_req);
        }

        // Rozwiązuje efektywne uwierzytelnianie żądania: jawny typ używany wprost; "Dziedzicz" idzie w górę
        // łańcucha folderów (ParentId) aż do pierwszego jawnego poziomu, na końcu do korzenia kolekcji.
        private (int type, string username, string secret) ResolveEffectiveAuth(RestRequest req)
        {
            if (req.AuthType != RestAuthResolve.Inherit) return (req.AuthType, req.AuthUsername, req.AuthSecret);
            var (t, u, s, _) = RestAuthResolve.Resolve(_coll, req.FolderId);
            return (t, u, s);
        }

        // Opis "skąd" dla podpowiedzi w UI (bez sekretu) — dokładnie ta klasa błędu, która wywołała tę funkcję:
        // użytkownik widzi "Dziedziczy", ale nie wie skąd i czy tam w ogóle coś jest ustawione.
        private string DescribeInheritedAuth(RestRequest req)
        {
            var (type, _, _, source) = RestAuthResolve.Resolve(_coll, req.FolderId);
            string place = source != null ? source.Name : L("S.rest.collection");
            string typeLabel = type == 1 ? L("S.rest.auth.bearer") : type == 2 ? L("S.rest.auth.basic") : L("S.rest.auth.none");
            return string.Format(L("S.rest.auth.inherit.desc"), place, typeLabel);
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

        private void ContentType_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            UpdateBodyMode();
        }

        // Treść: tabela pól dla x-www-form-urlencoded (jak w Postmanie), surowy edytor dla reszty typów.
        private void UpdateBodyMode()
        {
            bool form = string.Equals(SelectedContentType(), "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
            BodyRawHost.Visibility = form ? Visibility.Collapsed : Visibility.Visible;
            FormFieldsHost.Visibility = form ? Visibility.Visible : Visibility.Collapsed;
        }

        // Migracja przy wczytaniu: starsze żądania (sprzed edytora tabelarycznego) mają tylko surowy Body "k=v&k=v".
        private static List<RestKeyValue> ParseLegacyFormBody(string body)
        {
            var list = new List<RestKeyValue>();
            if (string.IsNullOrEmpty(body)) return list;
            foreach (var seg in body.Split('&'))
            {
                if (seg.Length == 0) continue;
                int eq = seg.IndexOf('=');
                list.Add(eq < 0
                    ? new RestKeyValue { Key = seg, Value = "" }
                    : new RestKeyValue { Key = seg.Substring(0, eq), Value = seg.Substring(eq + 1) });
            }
            return list;
        }

        // Podgląd wartości po podstawieniu {{zmiennych}} z aktywnego środowiska — pokazywany na hover jak w Postmanie.
        private void ValuePreview_ToolTipOpening(object sender, ToolTipEventArgs e)
        {
            if (!(sender is TextBox tb)) { e.Handled = true; return; }
            string preview = VarPreview(tb.Text);
            if (preview == null) { e.Handled = true; return; }
            tb.ToolTip = preview;
        }

        // null = nic do pokazania (brak {{...}} albo nieznana zmienna — Subst zwróci tekst bez zmian).
        private string VarPreview(string raw)
        {
            if (string.IsNullOrEmpty(raw) || raw.IndexOf("{{", StringComparison.Ordinal) < 0) return null;
            string resolved = RestClient.Subst(raw, Vars());
            if (resolved == raw) return null;
            return resolved.Length == 0 ? L("S.rest.var.empty") : resolved;
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

        // Kolory metod wg mockupu Compass (m-get/m-post/m-put/m-del): GET zielony, POST niebieski,
        // PUT bursztyn, DELETE czerwony, PATCH fiolet, reszta szary. Publiczne — używa też moduł REST w railu.
        public static Brush MethodBrush(string method)
        {
            switch (method)
            {
                case "GET": return new SolidColorBrush(Color.FromRgb(0x4B, 0xD6, 0xA0));
                case "POST": return new SolidColorBrush(Color.FromRgb(0x7B, 0xA6, 0xFF));
                case "PUT": return new SolidColorBrush(Color.FromRgb(0xF0, 0xB4, 0x5F));
                case "PATCH": return new SolidColorBrush(Color.FromRgb(0xB0, 0x8C, 0xE8));
                case "DELETE": return new SolidColorBrush(Color.FromRgb(0xF0, 0x73, 0x6C));
                default: return new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
            }
        }

        // Tinta tła badge'a metody (ten sam kolor, niska alfa) — jak .mb w mockupie. Publiczne — moduł REST.
        public static Brush MethodBadgeBg(string method)
        {
            var c = ((MethodBrush(method) as SolidColorBrush)?.Color) ?? Colors.Gray;
            return new SolidColorBrush(Color.FromArgb(0x26, c.R, c.G, c.B));
        }

        private Brush JsonBrush(RestJsonTok k)
        {
            switch (k)
            {
                case RestJsonTok.Key: return new SolidColorBrush(Color.FromRgb(0x7B, 0xA6, 0xFF));
                case RestJsonTok.Str: return new SolidColorBrush(Color.FromRgb(0x4B, 0xD6, 0xA0));
                case RestJsonTok.Num: return new SolidColorBrush(Color.FromRgb(0xF0, 0xB4, 0x5F));
                case RestJsonTok.Keyword: return new SolidColorBrush(Color.FromRgb(0xF0, 0xB4, 0x5F));
                case RestJsonTok.Punct: return (Brush)TryFindResource("TextTer") ?? Brushes.Gray;
                default: return (Brush)TryFindResource("TextPrim") ?? Brushes.White;
            }
        }

        // Wstawia treść odpowiedzi do RichTextBox: JSON kolorowany tokenami, reszta jednym kolorem.
        private void SetResponseBody(string text, bool allowColor)
        {
            var doc = new System.Windows.Documents.FlowDocument
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = (double)TryFindResource("FontBody"),
                PageWidth = 2400   // szeroka strona = brak zawijania + poziomy pasek (jak kod w mockupie)
            };
            var p = new System.Windows.Documents.Paragraph { Margin = new Thickness(0), LineHeight = 20 };
            string t = (text ?? "").TrimStart();
            bool json = allowColor && t.Length > 0 && (t[0] == '{' || t[0] == '[');
            if (json)
                foreach (var (seg, kind) in RestJsonColorizer.Tokenize(text))
                    p.Inlines.Add(new System.Windows.Documents.Run(seg) { Foreground = JsonBrush(kind) });
            else
                p.Inlines.Add(new System.Windows.Documents.Run(text ?? "") { Foreground = (Brush)TryFindResource("TextPrim") });
            doc.Blocks.Add(p);
            ResponseBody.Document = doc;
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
