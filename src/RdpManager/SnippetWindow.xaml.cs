using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RdpManager.Core;
using RdpManager.Models;

namespace RdpManager
{
    /// <summary>
    /// Wiersz listy snippetów. Osobno od modelu, bo numer skrótu NIE JEST cechą snippetu — wynika
    /// z pozycji na liście i zmienia się przy każdym przestawieniu.
    /// </summary>
    public sealed class SnippetRow
    {
        public CommandSnippet Model { get; }
        public int Index { get; set; }

        public SnippetRow(CommandSnippet model) { Model = model; }

        public string Name => string.IsNullOrWhiteSpace(Model.Name) ? SnippetStore.FirstLine(Model.Command) : Model.Name;
        public string Hotkey => Index < 9 ? (Index + 1).ToString() : "";
        public Visibility HotkeyVisibility => Index < 9 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Zarządzanie snippetami komend. Edytuje KOPIE — „Anuluj" i zamknięcie krzyżykiem mają nic nie
    /// utrwalać (ten sam wymóg co w oknie środowisk REST).
    ///
    /// Kolejność na liście jest znacząca: to ona przypisuje skróty Ctrl+Shift+1..9, dlatego strzałki
    /// góra/dół są tu równorzędną akcją, a numer widać przy każdym z pierwszych dziewięciu wpisów.
    /// </summary>
    public partial class SnippetWindow
    {
        private readonly ObservableCollection<SnippetRow> _rows = new ObservableCollection<SnippetRow>();
        private SnippetRow _current;
        private bool _loading;   // wypełnianie pól edytora nie może wracać do modelu jako edycja

        private static string L(string key) => LocalizationManager.S(key);

        public SnippetWindow()
        {
            InitializeComponent();
            Title = L("S.snip.title");
            WinTitleBar.Title = Title;
            VarsHint.Text = string.Format(L("S.snip.vars"),
                string.Join(", ", SnippetVars.Names.Select(n => "{" + n + "}")));

            foreach (var s in SnippetStore.Load()) _rows.Add(new SnippetRow(s.Clone()));
            SnipList.ItemsSource = _rows;
            Renumber();
            if (_rows.Count > 0) SnipList.SelectedIndex = 0; else ShowEditor(null);
        }

        private void Snip_Changed(object sender, SelectionChangedEventArgs e) => ShowEditor(SnipList.SelectedItem as SnippetRow);

        private void ShowEditor(SnippetRow row)
        {
            _current = row;
            _loading = true;
            Editor.IsEnabled = row != null;
            NameBox.Text = row?.Model.Name ?? "";
            CommandBox.Text = row?.Model.Command ?? "";
            EnterCheck.IsChecked = row?.Model.SendEnter ?? true;
            _loading = false;
        }

        private void Name_Changed(object sender, TextChangedEventArgs e)
        {
            if (_loading || _current == null) return;
            _current.Model.Name = NameBox.Text;
            SnipList.Items.Refresh();
        }

        private void Command_Changed(object sender, TextChangedEventArgs e)
        {
            if (_loading || _current == null) return;
            _current.Model.Command = CommandBox.Text;
            SnipList.Items.Refresh();   // wpis bez nazwy pokazuje pierwszy wiersz komendy
        }

        private void Enter_Click(object sender, RoutedEventArgs e)
        {
            if (_loading || _current == null) return;
            _current.Model.SendEnter = EnterCheck.IsChecked == true;
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            var row = new SnippetRow(new CommandSnippet { Name = L("S.snip.new") });
            _rows.Add(row);
            Renumber();
            SnipList.SelectedItem = row;
            NameBox.Focus();
            NameBox.SelectAll();
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (!(SnipList.SelectedItem is SnippetRow row)) return;
            int i = _rows.IndexOf(row);
            _rows.Remove(row);
            Renumber();
            if (_rows.Count == 0) ShowEditor(null);
            else SnipList.SelectedIndex = System.Math.Min(i, _rows.Count - 1);
        }

        private void Up_Click(object sender, RoutedEventArgs e) => Move(-1);
        private void Down_Click(object sender, RoutedEventArgs e) => Move(1);

        private void Move(int delta)
        {
            if (!(SnipList.SelectedItem is SnippetRow row)) return;
            int i = _rows.IndexOf(row), j = i + delta;
            if (j < 0 || j >= _rows.Count) return;
            _rows.Move(i, j);
            Renumber();
            SnipList.SelectedItem = row;   // zaznaczenie ma iść za wpisem, nie za pozycją
        }

        // Numery skrótów to pozycje — po każdej zmianie kolejności trzeba je przeliczyć i odrysować.
        private void Renumber()
        {
            for (int i = 0; i < _rows.Count; i++) _rows[i].Index = i;
            SnipList.Items.Refresh();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var list = new List<CommandSnippet>();
            foreach (var r in _rows)
            {
                r.Model.Name = (r.Model.Name ?? "").Trim();
                list.Add(r.Model);
            }

            // Wpis bez komendy nic nie robi, więc store go odsiewa — ale po cichu wyglądałoby to jak
            // zgubienie zmian („dodałem, nazwałem, zapisałem, zniknęło"). Pytamy, zamiast milczeć.
            int empty = list.Count(x => string.IsNullOrWhiteSpace(x.Command));
            if (empty > 0 &&
                MessageBox.Show(this, string.Format(L("S.snip.emptywarn"), empty), Title,
                                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                return;

            SnippetStore.Save(list);
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
