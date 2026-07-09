using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RdpManager.Models;

namespace RdpManager
{
    /// <summary>
    /// Zarządzanie GLOBALNYMI środowiskami REST i ich zmiennymi ({{klucz}}) — wspólnymi dla wszystkich
    /// kolekcji (EnvironmentStore). Edytuje kopie robocze; trwały zapis (do environments.json) następuje
    /// dopiero w „Zamknij". Zmienne są jawne w pliku — nie na sekrety.
    /// </summary>
    public partial class RestEnvWindow
    {
        private ObservableCollection<RestEnvironment> _envs;
        private ObservableCollection<RestVariable> _vars;
        private RestEnvironment _current;

        private static string L(string key) => LocalizationManager.S(key);

        public RestEnvWindow()
        {
            InitializeComponent();
            Title = L("S.rest.env.title");
            WinTitleBar.Title = Title;

            // Kopie robocze — edycja pól (binding TwoWay) NIE MOŻE dotykać obiektów ze store'u, inaczej
            // zamknięcie okna „na krzyżyku" (bez Close_Click) i tak zapisywałoby zmiany (A8 z przeglądu).
            _envs = new ObservableCollection<RestEnvironment>(EnvironmentStore.Load().Select(CloneEnv));
            EnvList.ItemsSource = _envs;
            if (_envs.Count > 0) EnvList.SelectedIndex = 0;
            else { _vars = new ObservableCollection<RestVariable>(); VarsList.ItemsSource = _vars; VarsList.IsEnabled = false; }
        }

        private void Env_Changed(object sender, SelectionChangedEventArgs e)
        {
            CommitVars();
            _current = EnvList.SelectedItem as RestEnvironment;
            _vars = new ObservableCollection<RestVariable>(_current?.Variables ?? new List<RestVariable>());
            VarsList.ItemsSource = _vars;
            VarsList.IsEnabled = _current != null;
        }

        // Zapisuje edytowane zmienne do bieżącego środowiska (obiekty współdzielone — edycje pól już w nich są;
        // to utrwala dodania/usunięcia wierszy).
        private void CommitVars()
        {
            if (_current != null && _vars != null) _current.Variables = _vars.ToList();
        }

        private void AddEnv_Click(object sender, RoutedEventArgs e)
        {
            CommitVars();
            var env = new RestEnvironment { Name = UniqueEnvName(L("S.rest.env.newenv")) };
            _envs.Add(env);
            EnvList.SelectedItem = env;
        }

        private void RenameEnv_Click(object sender, RoutedEventArgs e)
        {
            if (!(EnvList.SelectedItem is RestEnvironment env)) return;
            var dlg = new InputDialog(L("S.rest.rename"), L("S.rest.rename.label"), env.Name) { Owner = this };
            if (dlg.ShowDialog() != true || dlg.Value.Trim().Length == 0) return;
            env.Name = dlg.Value.Trim();
            EnvList.Items.Refresh();
        }

        private void DeleteEnv_Click(object sender, RoutedEventArgs e)
        {
            if (!(EnvList.SelectedItem is RestEnvironment env)) return;
            _current = null;   // nie commituj do usuwanego
            _envs.Remove(env);
            if (_envs.Count > 0) EnvList.SelectedIndex = 0;
            else { _current = null; _vars = new ObservableCollection<RestVariable>(); VarsList.ItemsSource = _vars; VarsList.IsEnabled = false; }
        }

        // Import osobnego pliku środowiska Postman (dodaje nowe środowisko do globalnej listy).
        private void ImportEnv_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = L("S.rest.env.importtitle"), Filter = L("S.dlg.postmanenv.filter") };
            if (dlg.ShowDialog(this) != true) return;

            RestEnvironment env;
            List<string> blanked;
            try { env = Core.PostmanImport.ParseEnvironment(System.IO.File.ReadAllText(dlg.FileName), out blanked); }
            catch (System.Exception ex)
            {
                MessageBox.Show(ex.Message, L("S.rest.env.importtitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            CommitVars();
            env.Name = UniqueEnvName(string.IsNullOrWhiteSpace(env.Name) ? L("S.rest.env.newenv") : env.Name);
            _envs.Add(env);
            EnvList.SelectedItem = env;

            // Import to jawna akcja — utrwal OD RAZU (tylko nowe środowisko): zamknięcie „krzyżykiem"
            // nie może go zgubić. Pozostałe edycje dalej zapisują się dopiero w „Zamknij" (A8).
            var stored = EnvironmentStore.Load();
            stored.Add(CloneEnv(env));
            EnvironmentStore.Save(stored);

            // Postman oznaczył te zmienne jako „secret" — ich wartości NIE zaimportowano (nigdy jawnie w rest.json).
            // Bez tego ostrzeżenia użytkownik po cichu dostaje puste zmienne tam, gdzie w Postmanie były realne wartości.
            if (blanked.Count > 0)
                MessageBox.Show(string.Format(L("S.rest.env.import.secretswarn"), string.Join(", ", blanked)),
                    L("S.rest.env.importtitle"), MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void AddVar_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            _vars.Add(new RestVariable());
        }

        private void DeleteVar_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is RestVariable v) _vars.Remove(v);
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            CommitVars();
            EnvironmentStore.Save(_envs.ToList());   // zapis dopiero tutaj (A8: „krzyżyk" nie utrwala zmian)
            DialogResult = true;
        }

        private string UniqueEnvName(string baseName)
        {
            var names = new HashSet<string>(_envs.Select(x => x.Name), System.StringComparer.OrdinalIgnoreCase);
            if (!names.Contains(baseName)) return baseName;
            for (int i = 2; ; i++) { string n = baseName + " " + i; if (!names.Contains(n)) return n; }
        }

        private static RestEnvironment CloneEnv(RestEnvironment e) => new RestEnvironment
        {
            Id = e.Id,
            Name = e.Name,
            Variables = e.Variables.Select(CloneVar).ToList()
        };

        private static RestVariable CloneVar(RestVariable v) => new RestVariable { Key = v.Key, Value = v.Value };
    }
}
