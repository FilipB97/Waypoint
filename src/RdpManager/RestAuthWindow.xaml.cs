using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RdpManager.Models;

namespace RdpManager
{
    /// <summary>
    /// Zarządzanie uwierzytelnianiem na poziomie kolekcji (korzeń) i folderów — dziedziczenie jak w Postmanie
    /// „Inherit auth from parent" (zob. RestAuthResolve). Edytuje kolekcję w pamięci; sekrety zapisuje do
    /// Credential Managera od razu po potwierdzeniu (ta sama dyscyplina co środowiska — patrz RestEnvWindow).
    /// </summary>
    public partial class RestAuthWindow
    {
        private sealed class AuthEntry
        {
            // WŁAŚCIWOŚĆ, nie pole: DisplayMemberPath („Label") binduje tylko do właściwości — jako pole
            // każdy wiersz listy renderował się z PUSTYM tekstem (niewidoczne pozycje).
            public string Label { get; set; }
            public RestFolder Folder;   // null = korzeń kolekcji
            public int AuthType;
            public string AuthUsername = "";
            public string AuthSecret = "";
        }

        private readonly RestCollection _coll;
        private readonly string _collAuthCredTarget;
        private ObservableCollection<AuthEntry> _entries;
        private AuthEntry _current;
        private bool _loading;

        private static string L(string key) => LocalizationManager.S(key);

        public RestAuthWindow(RestCollection coll, string collAuthCredTarget)
        {
            InitializeComponent();
            _coll = coll;
            _collAuthCredTarget = collAuthCredTarget;
            Title = L("S.rest.authmgr.title");
            WinTitleBar.Title = Title;

            _entries = new ObservableCollection<AuthEntry>
            {
                new AuthEntry
                {
                    Label = L("S.rest.authmgr.root"),
                    Folder = null,
                    AuthType = _coll.AuthType,
                    AuthUsername = _coll.AuthUsername,
                    AuthSecret = CredentialStore.TryRead(_collAuthCredTarget, out var cs) ? cs : ""
                }
            };
            foreach (var f in _coll.Folders.OrderBy(FolderPath))
                _entries.Add(new AuthEntry
                {
                    Label = FolderPath(f),
                    Folder = f,
                    AuthType = f.AuthType,
                    AuthUsername = f.AuthUsername,
                    AuthSecret = CredentialStore.TryRead(f.AuthCredTarget, out var fs) ? fs : ""
                });

            EntryList.ItemsSource = _entries;
            EntryList.SelectedIndex = 0;
        }

        // Ścieżka „Rodzic / Dziecko" — czytelne rozróżnienie zagnieżdżonych folderów o tej samej nazwie.
        private string FolderPath(RestFolder f)
        {
            var parts = new List<string> { f.Name };
            string parentId = f.ParentId;
            while (!string.IsNullOrEmpty(parentId))
            {
                var p = _coll.Folders.FirstOrDefault(x => x.Id == parentId);
                if (p == null) break;
                parts.Insert(0, p.Name);
                parentId = p.ParentId;
            }
            return string.Join(" / ", parts);
        }

        private void Entry_Changed(object sender, SelectionChangedEventArgs e)
        {
            CommitCurrent();
            _current = EntryList.SelectedItem as AuthEntry;
            if (_current == null) return;

            _loading = true;
            bool isFolder = _current.Folder != null;
            DetailHeader.Text = _current.Label;
            BuildTypeCombo(isFolder);
            TypeCombo.SelectedIndex = ClampType(_current.AuthType, isFolder);
            UserBox.Text = _current.AuthUsername;
            TokenBox.Text = _current.AuthType == 1 ? _current.AuthSecret : "";
            PassBox.Password = _current.AuthType == 2 ? _current.AuthSecret : "";
            UpdatePanels();
            _loading = false;
        }

        // Kolekcja (korzeń) nie ma "Dziedzicz" — nie ma z czego dziedziczyć.
        private void BuildTypeCombo(bool allowInherit)
        {
            TypeCombo.Items.Clear();
            TypeCombo.Items.Add(new ComboBoxItem { Content = L("S.rest.auth.none") });
            TypeCombo.Items.Add(new ComboBoxItem { Content = L("S.rest.auth.bearer") });
            TypeCombo.Items.Add(new ComboBoxItem { Content = L("S.rest.auth.basic") });
            if (allowInherit) TypeCombo.Items.Add(new ComboBoxItem { Content = L("S.rest.auth.inherit") });
        }

        private static int ClampType(int type, bool allowInherit) => System.Math.Clamp(type, 0, allowInherit ? 3 : 2);

        private void Type_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            UpdatePanels();
        }

        private void UpdatePanels()
        {
            int i = TypeCombo.SelectedIndex;
            BearerPanel.Visibility = i == 1 ? Visibility.Visible : Visibility.Collapsed;
            BasicPanel.Visibility = i == 2 ? Visibility.Visible : Visibility.Collapsed;
            bool inherit = i == 3;
            InheritHint.Visibility = inherit ? Visibility.Visible : Visibility.Collapsed;
            if (inherit && _current?.Folder != null) InheritHint.Text = DescribeInherited(_current.Folder.ParentId);
        }

        // Gdzie faktycznie rozwiąże się "Dziedzicz" dla TEGO folderu — start od jego RODZICA (pomija siebie).
        private string DescribeInherited(string parentFolderId)
        {
            var (type, _, _, source) = RestAuthResolve.Resolve(_coll, parentFolderId);
            string place = source != null ? source.Name : L("S.rest.authmgr.root");
            string typeLabel = type == 1 ? L("S.rest.auth.bearer") : type == 2 ? L("S.rest.auth.basic") : L("S.rest.auth.none");
            return string.Format(L("S.rest.auth.inherit.desc"), place, typeLabel);
        }

        private void CommitCurrent()
        {
            if (_current == null) return;
            _current.AuthType = TypeCombo.SelectedIndex < 0 ? 0 : TypeCombo.SelectedIndex;
            _current.AuthUsername = UserBox.Text ?? "";
            _current.AuthSecret = _current.AuthType == 1 ? TokenBox.Text
                                 : _current.AuthType == 2 ? PassBox.Password
                                 : "";
        }

        // Kolor {{zmiennych}} w polu tokenu — wg aktywnego środowiska KOLEKCJI (wybór jest per-kolekcja).
        // Zmienne wczytane raz przy otwarciu okna (edycje środowisk w innym oknie — rzadkie, wystarczy).
        private Dictionary<string, string> _envVars;

        private void Token_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (!(sender is TextBox tb)) return;
            string t = tb.Text ?? "";
            if (!RestClient.HasVars(t)) { tb.ClearValue(System.Windows.Controls.Control.ForegroundProperty); return; }
            if (_envVars == null)
            {
                _envVars = new Dictionary<string, string>();
                var env = EnvironmentStore.Load().FirstOrDefault(x => x.Id == _coll.ActiveEnvironmentId);
                if (env != null)
                    foreach (var v in env.Variables)
                        if (!string.IsNullOrWhiteSpace(v.Key)) _envVars[v.Key] = v.Value ?? "";
            }
            bool missing = RestClient.MissingVars(t, _envVars).Count > 0;
            tb.Foreground = (System.Windows.Media.Brush)TryFindResource(missing ? "Danger" : "AccentBright") ?? tb.Foreground;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            CommitCurrent();
            foreach (var entry in _entries)
            {
                string target = entry.Folder == null ? _collAuthCredTarget : entry.Folder.AuthCredTarget;
                if (entry.Folder == null) { _coll.AuthType = entry.AuthType; _coll.AuthUsername = entry.AuthUsername; }
                else { entry.Folder.AuthType = entry.AuthType; entry.Folder.AuthUsername = entry.AuthUsername; }

                if (entry.AuthType == 0 || string.IsNullOrEmpty(entry.AuthSecret)) CredentialStore.Delete(target);
                else CredentialStore.TrySave(target, entry.AuthUsername, entry.AuthSecret);
            }
            DialogResult = true;
        }
    }
}
