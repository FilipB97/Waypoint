using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RdpManager.Models;

namespace RdpManager
{
    /// <summary>
    /// Zarządzanie środowiskami REST i ich zmiennymi ({{klucz}}). Edytuje kolekcję w pamięci (przekazaną
    /// przez referencję); trwały zapis robi konsola przez „Zapisz". Zmienne są jawne w pliku — nie na sekrety.
    /// </summary>
    public partial class RestEnvWindow
    {
        private readonly RestCollection _coll;
        private ObservableCollection<RestEnvironment> _envs;
        private ObservableCollection<RestVariable> _vars;
        private RestEnvironment _current;

        private static string L(string key) => LocalizationManager.S(key);

        public RestEnvWindow(RestCollection coll)
        {
            InitializeComponent();
            _coll = coll;
            Title = L("S.rest.env.title");
            WinTitleBar.Title = Title;

            _envs = new ObservableCollection<RestEnvironment>(_coll.Environments);
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

        // Import osobnego pliku środowiska Postman (dodaje nowe środowisko do tej kolekcji).
        private void ImportEnv_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = L("S.rest.env.importtitle"), Filter = L("S.dlg.postman.filter") };
            if (dlg.ShowDialog(this) != true) return;

            RestEnvironment env;
            try { env = Core.PostmanImport.ParseEnvironment(System.IO.File.ReadAllText(dlg.FileName)); }
            catch (System.Exception ex)
            {
                MessageBox.Show(ex.Message, L("S.rest.env.importtitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            CommitVars();
            env.Name = UniqueEnvName(string.IsNullOrWhiteSpace(env.Name) ? L("S.rest.env.newenv") : env.Name);
            _envs.Add(env);
            EnvList.SelectedItem = env;
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
            _coll.Environments = _envs.ToList();
            DialogResult = true;
        }

        private string UniqueEnvName(string baseName)
        {
            var names = new HashSet<string>(_envs.Select(x => x.Name), System.StringComparer.OrdinalIgnoreCase);
            if (!names.Contains(baseName)) return baseName;
            for (int i = 2; ; i++) { string n = baseName + " " + i; if (!names.Contains(n)) return n; }
        }
    }
}
