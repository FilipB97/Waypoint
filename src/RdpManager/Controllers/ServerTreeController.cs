using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using RdpManager.Core;
using RdpManager.Models;

namespace RdpManager.Controllers
{
    /// <summary>
    /// Drzewo serwerów w sidebarze: render grup/wierszy (styl domyślny/minimalny), filtr protokołów,
    /// zwijanie grup, przypinanie, wielozaznaczenie (Ctrl/Shift), drag&drop kolejności, menu kontekstowe
    /// i podświetlanie aktywnego wiersza. Wyniesione 1:1 z MainWindow (PR 3 planu docs/REFACTOR-MAINWINDOW.md,
    /// wzorzec „back-reference move-method") — bez zmian logiki. Operacje CRUD/sesji/pomocnicze pędzle
    /// zostają w MainWindow i są wołane przez <c>_owner.</c>; Reachability aktualizuje wiersze przez
    /// szew <see cref="SetRowStatus"/>.
    /// </summary>
    internal sealed class ServerTreeController
    {
        private readonly MainWindow _owner;

        // Wiersze + akcje aktywacji + kropki statusu + etykiety opóźnień (klucz = serwer).
        private readonly Dictionary<ServerInfo, Border> _serverRows = new Dictionary<ServerInfo, Border>();
        private readonly Dictionary<ServerInfo, Action<bool>> _serverActivate = new Dictionary<ServerInfo, Action<bool>>();
        private readonly Dictionary<ServerInfo, Ellipse> _serverStatusDot = new Dictionary<ServerInfo, Ellipse>();
        private readonly Dictionary<ServerInfo, TextBlock> _serverLatency = new Dictionary<ServerInfo, TextBlock>();

        // Aktywny filtr protokołu z paska chipów (null = „Wszystkie"). Stan sesyjny.
        private RemoteProtocol? _protocolFilter;

        // Drag&drop kolejności serwerów w drzewie.
        private Point _dragStartPoint;
        private ServerInfo _dragCandidate;
        private bool _didDrag;

        // Zaznaczenie wielu serwerów (Ctrl/Shift+klik). Nietrwałe — czyszczone przy każdej przebudowie drzewa.
        private readonly HashSet<ServerInfo> _multiSelect = new HashSet<ServerInfo>();
        private ServerInfo _selectAnchor;
        private readonly List<ServerInfo> _visibleOrder = new List<ServerInfo>();

        private InsertionAdorner _dropAdorner;   // linia „tu wyląduje" na krawędzi wiersza
        private Border _dropRow;                  // wiersz, do którego przypięty jest adorner

        // Klucz sekcji „Przypięte" w AppSettings.CollapsedGroups (nie koliduje z nazwami grup użytkownika).
        private const string PinnedGroupKey = "__pinned__";

        private static string L(string key) => LocalizationManager.S(key);

        public ServerTreeController(MainWindow owner) => _owner = owner;

        private bool IsMinimalList => _owner._settings != null && _owner._settings.ListStyle == "Minimal";

        // ---------- Filtr protokołów ----------

        private void BuildProtocolFilter()
        {
            _owner.ProtoFilterBar.Children.Clear();
            // Bez REST — kolekcje mają własny moduł w railu, chip byłby martwy (lista ich nie pokazuje).
            var protos = _owner._vm.Servers.Select(s => s.Protocol).Where(p => p != RemoteProtocol.Rest)
                                    .Distinct().OrderBy(p => (int)p).ToList();

            // Filtr wskazujący nieobecny już protokół (usunięto ostatni taki serwer) → reset do „Wszystkie".
            if (_protocolFilter.HasValue && !protos.Contains(_protocolFilter.Value)) _protocolFilter = null;

            if (protos.Count < 2) { _owner.ProtoFilterBar.Visibility = Visibility.Collapsed; return; }
            _owner.ProtoFilterBar.Visibility = Visibility.Visible;

            _owner.ProtoFilterBar.Children.Add(MakeProtocolChip(L("S.proto.filter.all"), null, _owner.Res("TextSec")));
            foreach (var p in protos)
                _owner.ProtoFilterBar.Children.Add(MakeProtocolChip(MainWindow.ProtocolShort(p), p, _owner.ProtocolBrush(p)));
        }

        private FrameworkElement MakeProtocolChip(string text, RemoteProtocol? proto, Brush accent)
        {
            bool selected = _protocolFilter == proto || (proto == null && _protocolFilter == null);
            var chip = new Border
            {
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(9, 3, 9, 3),
                Margin = new Thickness(0, 0, 5, 5),
                Background = selected ? _owner.Res("AccentSoft") : Brushes.Transparent,
                BorderBrush = selected ? _owner.Res("Accent") : _owner.Res("Border"),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = text,
                    Foreground = selected ? _owner.Res("TextPrim") : accent,
                    FontSize = (double)_owner.TryFindResource("FontCaption"),
                    FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal
                }
            };
            chip.MouseLeftButtonUp += (s, e) => { _protocolFilter = proto; RenderTree(_owner.SearchBox.Text); };
            return chip;
        }

        // ---------- Drzewo serwerów ----------

        internal void BuildServerTree()
        {
            _owner._vm.LoadServers(ServerRepository.Load());
            RenderTree();
        }

        internal void RenderTree(string filter = null)
        {
            string filterDisplay = (filter ?? "").Trim();
            filter = filterDisplay.ToLowerInvariant();
            _owner.ServerTree.Children.Clear();
            _serverRows.Clear();
            _serverActivate.Clear();
            _serverStatusDot.Clear();
            _serverLatency.Clear();
            _multiSelect.Clear();
            _selectAnchor = null;
            _visibleOrder.Clear();

            // Pasek chipów filtra protokołów nad listą (Compass §4.2); też weryfikuje _protocolFilter
            // względem obecnych serwerów (gdy protokół zniknął — reset do „Wszystkie").
            BuildProtocolFilter();

            // Dostępność: strzałki i Tab przenoszą fokus między wierszami serwerów.
            KeyboardNavigation.SetDirectionalNavigation(_owner.ServerTree, KeyboardNavigationMode.Continue);
            KeyboardNavigation.SetTabNavigation(_owner.ServerTree, KeyboardNavigationMode.Continue);

            // Sekcja „Przypięte" na górze — ulubione serwery (kolejność z listy), niezależnie od grupy.
            // Wpisy REST NIE żyją na liście serwerów — mają własny moduł w railu (przypięcie sortuje je TAM).
            var pinned = _owner._vm.Servers.Where(s => s.Pinned && s.Protocol != RemoteProtocol.Rest
                && RdpUtils.MatchesFilter(s, filter) && RdpUtils.MatchesProtocol(s, _protocolFilter)).ToList();
            if (pinned.Count > 0)
            {
                bool pinCollapsed = _owner._settings.CollapsedGroups.Contains(PinnedGroupKey);
                _owner.ServerTree.Children.Add(BuildGroupHeader(PinnedGroupKey, pinned.Count, pinCollapsed, isPinned: true));
                if (!pinCollapsed)
                    foreach (var s in pinned) { _owner.ServerTree.Children.Add(BuildServerRow(s)); _visibleOrder.Add(s); }
            }

            // Zwykłe grupy (bez przypiętych).
            var order = new List<string>();
            var byGroup = new Dictionary<string, List<ServerInfo>>();
            foreach (var s in _owner._vm.Servers)
            {
                if (s.Protocol == RemoteProtocol.Rest) continue;   // kolekcje REST → moduł w railu, nie lista
                if (s.Pinned) continue;
                if (!RdpUtils.MatchesFilter(s, filter)) continue;
                if (!RdpUtils.MatchesProtocol(s, _protocolFilter)) continue;
                var g = string.IsNullOrWhiteSpace(s.Group) ? L("S.group.serversdefault") : s.Group;
                if (!byGroup.ContainsKey(g)) { order.Add(g); byGroup[g] = new List<ServerInfo>(); }
                byGroup[g].Add(s);
            }
            foreach (var g in order)
            {
                bool collapsed = _owner._settings.CollapsedGroups.Contains(g);
                _owner.ServerTree.Children.Add(BuildGroupHeader(g, byGroup[g].Count, collapsed, isPinned: false));
                if (!collapsed)
                    foreach (var s in byGroup[g])
                    { _owner.ServerTree.Children.Add(BuildServerRow(s)); _visibleOrder.Add(s); }
            }
            UpdateActiveRows();

            // Pusty stan drzewa (3.1 z przeglądu): rozróżnij "w ogóle brak serwerów" od "filtr nic nie znalazł" —
            // liczymy dopasowania, nie _visibleOrder (te pomija zwinięte grupy, więc byłoby mylące gdy wszystko zwinięte).
            int matchCount = pinned.Count + byGroup.Values.Sum(l => l.Count);
            if (_owner._vm.Servers.Count == 0) { _owner.TreeEmptyHint.Text = L("S.tree.empty"); _owner.TreeEmptyHint.Visibility = Visibility.Visible; }
            else if (matchCount == 0)
            {
                // Puste dopasowanie może wynikać z tekstu w polu szukania i/lub z filtra protokołu — pokaż
                // to, co faktycznie zawęża (sam „{0}" byłby pusty, gdy filtruje tylko chip protokołu).
                string needle = filterDisplay.Length > 0 ? filterDisplay
                              : _protocolFilter.HasValue ? MainWindow.ProtocolLabel(_protocolFilter.Value) : "";
                _owner.TreeEmptyHint.Text = string.Format(L("S.tree.noresults"), needle);
                _owner.TreeEmptyHint.Visibility = Visibility.Visible;
            }
            else _owner.TreeEmptyHint.Visibility = Visibility.Collapsed;
        }

        private FrameworkElement BuildGroupHeader(string name, int count, bool collapsed, bool isPinned)
        {
            var row = new Border
            {
                // Minimal: ciaśniejszy padding niż domyślny (lżejsze nagłówki grup i sekcja przypiętych).
                Padding = IsMinimalList ? new Thickness(6, 5, 6, 2) : new Thickness(6, 10, 6, 4),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };

            // Strzałka zwijania (▸ zwinięte / ▾ rozwinięte).
            sp.Children.Add(new TextBlock
            {
                Text = collapsed ? "▸" : "▾",
                Foreground = _owner.Res("TextTer"), FontSize = 10, Width = 12,
                VerticalAlignment = VerticalAlignment.Center
            });

            if (isPinned)
                sp.Children.Add(new TextBlock
                {
                    Text = "★", Foreground = _owner.Res("Idle"), FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
                });
            else
                sp.Children.Add(new Ellipse
                {
                    Width = 6, Height = 6, Fill = _owner.GroupDotBrush(name),
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
                });

            sp.Children.Add(new TextBlock
            {
                Text = (isPinned ? L("S.group.pinned") : name.ToUpperInvariant()) + "  ·  " + count,
                Foreground = _owner.Res("TextSec"),
                FontSize = 13, FontWeight = FontWeights.Bold,   // grupa nadrzędna — wyraźniej niż wiersze w środku
                VerticalAlignment = VerticalAlignment.Center
            });

            row.Child = sp;

            string key = isPinned ? PinnedGroupKey : name;
            row.MouseLeftButtonUp += (s, e) => ToggleGroupCollapse(key);

            if (!isPinned)
            {
                var menu = new ContextMenu();
                var rename = new MenuItem { Header = L("S.m.renamegroup") };
                rename.Click += (s, e) => RenameGroup(name);
                menu.Items.Add(rename);
                row.ContextMenu = menu;
            }
            return row;
        }

        // Zwija/rozwija grupę i zapamiętuje stan w ustawieniach.
        private void ToggleGroupCollapse(string key)
        {
            if (!_owner._settings.CollapsedGroups.Remove(key)) _owner._settings.CollapsedGroups.Add(key);
            SettingsStore.Save(_owner._settings);
            RenderTree(_owner.SearchBox.Text);
        }

        // Zmienia nazwę grupy dla WSZYSTKICH jej serwerów naraz (bez wchodzenia w każdy z osobna).
        private void RenameGroup(string oldName)
        {
            var dlg = new InputDialog(L("S.prompt.renamegroup.title"),
                string.Format(L("S.prompt.renamegroup.label"), oldName), oldName) { Owner = _owner };
            if (dlg.ShowDialog() != true) return;

            string newName = dlg.Value;
            if (string.IsNullOrWhiteSpace(newName) || newName == oldName) return;

            foreach (var s in _owner._vm.Servers)
                if ((string.IsNullOrWhiteSpace(s.Group) ? L("S.group.serversdefault") : s.Group) == oldName)
                    s.Group = newName;

            // Przenieś stan zwinięcia na nową nazwę.
            if (_owner._settings.CollapsedGroups.Remove(oldName) && !_owner._settings.CollapsedGroups.Contains(newName))
                _owner._settings.CollapsedGroups.Add(newName);
            SettingsStore.Save(_owner._settings);

            _owner.PersistServers();
            RenderTree(_owner.SearchBox.Text);
        }

        // Przypina/odpina serwer (sekcja „Przypięte" na górze).
        internal void TogglePin(ServerInfo server)
        {
            server.Pinned = !server.Pinned;
            _owner.PersistServers();
            RenderTree(_owner.SearchBox.Text);
            if (_owner._restMode) _owner.BuildRestModule();   // przypięcie sortuje kolekcje w module
        }

        private FrameworkElement BuildServerRow(ServerInfo server)
            => IsMinimalList ? BuildServerRowMinimal(server) : BuildServerRowDefault(server);

        // Wariant DOMYŚLNY: awatar 22px + dwie linie (nazwa/host) + kropka statusu po prawej.
        private FrameworkElement BuildServerRowDefault(ServerInfo server)
        {
            var row = new Border
            {
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(18, 1, 0, 1),   // wcięcie = element należy do grupy powyżej (Compass §4.3)
                Padding = new Thickness(6, 7, 8, 7),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Tag = server
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var accent = new Rectangle
            {
                Width = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = _owner.Res("Accent"),
                VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(0, 2, 0, 2),
                Visibility = Visibility.Collapsed
            };
            Grid.SetColumn(accent, 0);
            grid.Children.Add(accent);

            var avatar = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(6),
                Background = _owner.AvatarBrush(server), Margin = new Thickness(8, 0, 0, 0),
                Child = new TextBlock
                {
                    Text = MainWindow.ServerInitials(server), Foreground = Brushes.White, FontSize = 9.5, FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            };
            Grid.SetColumn(avatar, 1);
            grid.Children.Add(avatar);

            // Sam adres (DisplayHost) zdjęty z wiersza — nie mieścił się z nazwą; jest w tooltipie (WireServerRow).
            var meta = new StackPanel { Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            meta.Children.Add(new TextBlock { Text = server.Name, Foreground = _owner.Res("TextPrim"), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(meta, 2);
            grid.Children.Add(meta);

            var status = new Ellipse
            {
                Width = 7, Height = 7, Fill = _owner.StatusBrush(server.Status),
                VerticalAlignment = VerticalAlignment.Center
            };
            _serverStatusDot[server] = status;

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(BuildProtocolTag(server));
            AddLatencyLabel(right, server);
            if (server.Pinned)
                right.Children.Add(new TextBlock
                {
                    Text = "★", Foreground = _owner.Res("Idle"), FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
                });
            right.Children.Add(status);
            Grid.SetColumn(right, 3);
            grid.Children.Add(right);

            row.Child = grid;

            _serverActivate[server] = active =>
            {
                row.Background = active ? _owner.Res("AccentSoft") : Brushes.Transparent;
                accent.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            };
            WireServerRow(row, server);
            return row;
        }

        // Wariant MINIMALISTYCZNY: jednowierszowy, bez awatara — pasek koloru + kropka statusu + nazwa/host.
        private FrameworkElement BuildServerRowMinimal(ServerInfo server)
        {
            var row = new Border
            {
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(18, 1, 0, 1),   // wcięcie = element należy do grupy powyżej (Compass §4.3)
                Padding = new Thickness(0, 3, 8, 3),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Tag = server
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });                    // pasek koloru
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                      // kropka
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // nazwa
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                      // host

            // Pasek koloru przy lewej krawędzi = tożsamość serwera; zmienia się na akcent, gdy zaznaczony.
            var serverColor = _owner.AvatarBrush(server);
            var bar = new Rectangle
            {
                Width = 3, RadiusX = 2, RadiusY = 2, Fill = serverColor,
                VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(0, 3, 0, 3)
            };
            Grid.SetColumn(bar, 0);
            grid.Children.Add(bar);

            var status = new Ellipse
            {
                Width = 7, Height = 7, Fill = _owner.StatusBrush(server.Status),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(11, 0, 0, 0)
            };
            _serverStatusDot[server] = status;
            Grid.SetColumn(status, 1);
            grid.Children.Add(status);

            var name = new TextBlock
            {
                Text = server.Name, Foreground = _owner.Res("TextPrim"), FontSize = 12, FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(name, 2);
            grid.Children.Add(name);

            // Po prawej: znacznik protokołu (+ opcjonalne opóźnienie / gwiazdka). Adres (DisplayHost) zdjęty
            // z wiersza — nazwa nie mieściła się z adresem; adres jest w tooltipie (WireServerRow).
            var rightPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
            };
            rightPanel.Children.Add(BuildProtocolTag(server));
            AddLatencyLabel(rightPanel, server);
            if (server.Pinned)
                rightPanel.Children.Add(new TextBlock
                {
                    Text = "★", Foreground = _owner.Res("Idle"), FontSize = 9,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
                });
            Grid.SetColumn(rightPanel, 3);
            grid.Children.Add(rightPanel);

            row.Child = grid;

            _serverActivate[server] = active =>
            {
                row.Background = active ? _owner.Res("AccentSoft") : Brushes.Transparent;
                bar.Fill = active ? _owner.Res("Accent") : serverColor;
            };
            WireServerRow(row, server);
            return row;
        }

        // Kolorowa etykieta protokołu (mono) po prawej stronie wiersza — świadoma protokołów lista (Compass §3).
        private TextBlock BuildProtocolTag(ServerInfo server) => new TextBlock
        {
            Text = MainWindow.ProtocolShort(server.Protocol),
            Foreground = _owner.ProtocolBrush(server.Protocol),
            FontSize = (double)_owner.TryFindResource("FontCaption"),
            FontFamily = (FontFamily)_owner.TryFindResource("Mono"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };

        // Etykieta opóźnienia (ms) — tylko gdy włączone „Pokazuj opóźnienia"; rejestrowana do aktualizacji na żywo.
        private void AddLatencyLabel(Panel host, ServerInfo server)
        {
            if (_owner._settings == null || !_owner._settings.ShowLatency) return;
            var lat = new TextBlock
            {
                Text = RdpUtils.FormatLatency(server.LatencyMs),
                Foreground = _owner.Res("TextTer"),
                FontSize = (double)_owner.TryFindResource("FontCaption"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            _serverLatency[server] = lat;
            host.Children.Add(lat);
        }

        // Wspólne zachowanie wiersza (hover / przeciąganie-zmiana kolejności / klik / menu) — jednakowe w obu stylach.
        private void WireServerRow(Border row, ServerInfo server)
        {
            // Dostępność (z PR #21): wiersz fokusowalny (nawigacja klawiaturą), nazwa dla czytnika ekranu
            // (nazwa — host — status), a kropka statusu — swój tekst. Wspólne dla obu stylów listy.
            row.Focusable = true;
            string tagText = (server.Tags != null && server.Tags.Count > 0) ? "  #" + string.Join(" #", server.Tags) : "";
            System.Windows.Automation.AutomationProperties.SetName(row,
                server.Name + " — " + MainWindow.DisplayHost(server) + " — " + MainWindow.StatusLabel(server.Status) + tagText);
            // Adres zdjęty z wiersza (nie mieścił się z nazwą) → pokazujemy go tutaj, w tooltipie, razem
            // z tagami i notatką (jeśli są). Nazwa zawsze; adres prawie zawsze — więc tooltip jest zawsze.
            string dh = MainWindow.DisplayHost(server);
            string hostText = string.IsNullOrWhiteSpace(dh) ? "" : "\n" + dh;
            string tagsTip = (server.Tags != null && server.Tags.Count > 0) ? "\n#" + string.Join(" #", server.Tags) : "";
            string noteText = string.IsNullOrWhiteSpace(server.Notes) ? "" : "\n" + server.Notes.Trim();
            row.ToolTip = server.Name + hostText + tagsTip + noteText;
            if (_serverStatusDot.TryGetValue(server, out var statusDot))
                System.Windows.Automation.AutomationProperties.SetName(statusDot, MainWindow.StatusLabel(server.Status));

            row.MouseEnter += (s, e) => { if (_owner._active?.Server != server) row.Background = _owner.Res("Elevated"); };
            row.MouseLeave += (s, e) => { if (_owner._active?.Server != server && !row.IsKeyboardFocused) row.Background = RowRestBackground(server); };
            row.GotKeyboardFocus += (s, e) => { if (_owner._active?.Server != server) row.Background = _owner.Res("Elevated"); };
            row.LostKeyboardFocus += (s, e) => { if (_owner._active?.Server != server) row.Background = RowRestBackground(server); };
            row.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter || e.Key == Key.Space) { _owner.LaunchServer(server, true); e.Handled = true; }
            };

            // Drag&drop: przeciągnięcie zmienia kolejność (a upuszczenie na inną grupę przenosi do niej).
            row.AllowDrop = true;
            row.PreviewMouseLeftButtonDown += (s, e) => { _dragStartPoint = e.GetPosition(null); _dragCandidate = server; _didDrag = false; };
            row.PreviewMouseMove += (s, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate == null) return;
                var pos = e.GetPosition(null);
                if (Math.Abs(pos.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(pos.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                _didDrag = true;
                row.Opacity = 0.4;   // wizualnie „podnieś" przeciągany wiersz
                try { DragDrop.DoDragDrop(row, _dragCandidate, DragDropEffects.Move); }
                catch { }
                finally { row.Opacity = 1.0; ClearDropIndicator(); _dragCandidate = null; }
            };
            row.DragOver += (s, e) =>
            {
                if (!e.Data.GetDataPresent(typeof(ServerInfo))) return;   // pliki z Eksploratora → obsłuży ServerTree (import .rdp)
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
                var dragged = e.Data.GetData(typeof(ServerInfo)) as ServerInfo;
                if (dragged == null || dragged == server) { ClearDropIndicator(); return; }
                bool bottom = e.GetPosition(row).Y > row.ActualHeight / 2;
                ShowDropIndicator(row, bottom);
            };
            row.Drop += (s, e) =>
            {
                if (!e.Data.GetDataPresent(typeof(ServerInfo))) return;   // pliki bąbelkują do ServerTree (import .rdp)
                ClearDropIndicator();
                bool bottom = e.GetPosition(row).Y > row.ActualHeight / 2;
                ReorderServer(e.Data.GetData(typeof(ServerInfo)) as ServerInfo, server, bottom);
                e.Handled = true;
            };
            row.MouseLeftButtonUp += (s, e) =>
            {
                if (_didDrag) { _didDrag = false; return; }   // to było przeciąganie, nie klik
                var mods = Keyboard.Modifiers;
                if (mods.HasFlag(ModifierKeys.Shift) && _selectAnchor != null) { RangeSelect(server); e.Handled = true; return; }
                if (mods.HasFlag(ModifierKeys.Control) || mods.HasFlag(ModifierKeys.Shift)) { ToggleSelect(server); e.Handled = true; return; }
                ClearMultiSelect();   // zwykły klik = połącz i wyczyść zaznaczenie
                _owner.LaunchServer(server, true);
            };

            row.ContextMenu = BuildServerContextMenu(server);
            row.ContextMenuOpening += (s, e) =>
            {
                // Prawy-klik na zaznaczonym wierszu przy zaznaczeniu ≥2 → menu zbiorcze; inaczej menu pojedyncze
                // (prawy-klik poza zaznaczeniem czyści zaznaczenie i pokazuje zwykłe menu wiersza).
                if (_multiSelect.Count >= 2 && _multiSelect.Contains(server))
                    row.ContextMenu = _owner.BuildBulkContextMenu(_multiSelect.ToList());
                else
                {
                    ClearMultiSelect();
                    row.ContextMenu = BuildServerContextMenu(server);
                }
            };
            _serverRows[server] = row;
        }

        // Tło wiersza w stanie spoczynku (nie hover/focus/aktywny): zaznaczony = AccentSoft, inaczej przezroczysty.
        private Brush RowRestBackground(ServerInfo s)
            => _multiSelect.Contains(s) ? _owner.Res("AccentSoft") : Brushes.Transparent;

        // Ctrl+klik: przełącz pojedynczy wiersz w zaznaczeniu (ustaw kotwicę dla ewentualnego Shift).
        private void ToggleSelect(ServerInfo server)
        {
            if (!_multiSelect.Remove(server)) _multiSelect.Add(server);
            _selectAnchor = server;
            RefreshSelectionVisuals();
        }

        // Shift+klik: zaznacz ciągły zakres od kotwicy do wskazanego wiersza (w kolejności widocznej).
        private void RangeSelect(ServerInfo server)
        {
            int a = _visibleOrder.IndexOf(_selectAnchor), b = _visibleOrder.IndexOf(server);
            if (a < 0 || b < 0) { ToggleSelect(server); return; }
            if (a > b) { (a, b) = (b, a); }
            _multiSelect.Clear();
            for (int i = a; i <= b; i++) _multiSelect.Add(_visibleOrder[i]);
            RefreshSelectionVisuals();
        }

        private void ClearMultiSelect()
        {
            _selectAnchor = null;
            if (_multiSelect.Count == 0) return;
            _multiSelect.Clear();
            RefreshSelectionVisuals();
        }

        // Odśwież tło wierszy wg zaznaczenia. Pomijamy: aktywną sesję (maluje ją UpdateActiveRows) oraz wiersze
        // pod kursorem / z fokusem (te odświeżą własne handlery MouseLeave/LostKeyboardFocus).
        private void RefreshSelectionVisuals()
        {
            foreach (var kv in _serverRows)
            {
                if (_owner._active?.Server == kv.Key || kv.Value.IsMouseOver || kv.Value.IsKeyboardFocused) continue;
                kv.Value.Background = _multiSelect.Contains(kv.Key) ? _owner.Res("AccentSoft") : Brushes.Transparent;
            }
        }

        internal ContextMenu BuildServerContextMenu(ServerInfo server)
        {
            var menu = new ContextMenu();
            bool rdp = server.Protocol == RemoteProtocol.Rdp;
            bool rest = server.Protocol == RemoteProtocol.Rest;   // kolekcja — nie serwer: bez WoL, „Duplikuj kolekcję"
            var pinItem = new MenuItem { Header = L(server.Pinned ? "S.m.unpin" : "S.m.pin") };
            pinItem.Click += (s, e) => TogglePin(server);
            var newWinItem = new MenuItem { Header = L("S.m.newwin") };
            newWinItem.Click += (s, e) => _owner.OpenInNewWindow(server);
            var connectAsItem = new MenuItem { Header = L("S.m.connectas") };
            connectAsItem.Click += (s, e) =>
            {
                _owner.OpenServer(server);
                if (_owner._active?.Server == server) _owner.PromptAndConnect(_owner._active, L("S.prompt.connectas"));
            };
            var editItem = new MenuItem { Header = L("S.m.edit") };
            editItem.Click += (s, e) => _owner.EditServer(server);
            var dupItem = new MenuItem { Header = L(rest ? "S.m.dupcollection" : "S.m.dupserver") };
            dupItem.Click += (s, e) => _owner.DuplicateServer(server);

            // Kopiuj ▸ — pojedyncze pola (i login+hasło) do schowka. Hasło z Credential Managera na żądanie.
            var copyMenu = new MenuItem { Header = L("S.m.copy") };
            void AddCopy(string key, Func<string> value)
            {
                var mi = new MenuItem { Header = L(key) };
                mi.Click += (s, e) => _owner.CopyToClipboard(value());
                copyMenu.Items.Add(mi);
            }
            AddCopy("S.m.copy.name", () => server.Name);
            AddCopy("S.m.copy.host", () => server.Host);
            if (server.Protocol != RemoteProtocol.Http && server.Protocol != RemoteProtocol.Rest)
                AddCopy("S.m.copy.port", () => server.Port.ToString());   // WWW/REST: URL niesie port
            if (rdp || server.Protocol == RemoteProtocol.Ssh || server.Protocol == RemoteProtocol.Sftp || server.Protocol == RemoteProtocol.Ftp)
            {
                AddCopy("S.m.copy.user", () => _owner.EffUser(server));
                if (rdp) AddCopy("S.m.copy.domain", () => _owner.EffDomain(server));
                copyMenu.Items.Add(new Separator());
                AddCopy("S.m.copy.pass", () => _owner.ReadEffPassword(server));
                AddCopy("S.m.copy.userpass", () => _owner.EffUser(server) + "\t" + _owner.ReadEffPassword(server));
            }

            var diagItem = new MenuItem { Header = L("S.m.diag") };
            diagItem.Click += (s, e) => _owner.DiagnoseServer(server);
            var wolItem = new MenuItem
            {
                Header = L("S.m.wol"),
                IsEnabled = !string.IsNullOrWhiteSpace(server.MacAddress)   // bez MAC nie ma czego budzić
            };
            wolItem.Click += (s, e) => _owner.WakeServer(server);
            var exportItem = new MenuItem { Header = L("S.m.exportrdp") };
            exportItem.Click += (s, e) => _owner.ExportRdp(server);
            var delItem = new MenuItem { Header = L("S.m.delete") };
            delItem.Click += (s, e) => _owner.DeleteServer(server);
            // Moduł REST: klik wiersza kolekcji zwija/rozwija, więc otwarcie konsoli ma jawny wpis w menu;
            // do tego tworzenie żądań/folderów w korzeniu (foldery i żądania mają własne menu z pełną strukturą).
            if (rest)
            {
                var openItem = new MenuItem { Header = L("S.m.opencoll") };
                openItem.Click += (s, e) => _owner.LaunchServer(server, true);
                menu.Items.Add(openItem);
                var newReqItem = new MenuItem { Header = L("S.rest.newreq") };
                newReqItem.Click += (s, e) => _owner.AddRestRequestCmd(server, "");
                menu.Items.Add(newReqItem);
                var newFolderItem = new MenuItem { Header = L("S.rest.newfolder") };
                newFolderItem.Click += (s, e) => _owner.AddRestFolderCmd(server, "");
                menu.Items.Add(newFolderItem);
                menu.Items.Add(new Separator());
            }
            menu.Items.Add(pinItem);
            menu.Items.Add(new Separator());
            if (rdp) menu.Items.Add(newWinItem);       // osobne okno sesji jest RDP-owe
            if (rdp || server.Protocol == RemoteProtocol.Ssh || server.Protocol == RemoteProtocol.Sftp || server.Protocol == RemoteProtocol.Ftp) menu.Items.Add(connectAsItem);
            menu.Items.Add(editItem);
            menu.Items.Add(dupItem);
            menu.Items.Add(copyMenu);
            if (server.Protocol != RemoteProtocol.Serial && server.Protocol != RemoteProtocol.Http && server.Protocol != RemoteProtocol.Rest)
                menu.Items.Add(diagItem);   // sonda TCP — nie dla COM/URL/REST
            if (!rest) menu.Items.Add(wolItem);   // Wake-on-LAN nie dotyczy kolekcji REST
            if (rdp) menu.Items.Add(exportItem);       // .rdp ma sens tylko dla RDP
            menu.Items.Add(new Separator());
            menu.Items.Add(delItem);
            return menu;
        }

        internal void UpdateActiveRows()
        {
            foreach (var kv in _serverActivate)
                kv.Value(_owner._active != null && _owner._active.Server == kv.Key);
        }

        // Szew dla ReachabilityService: po sondzie ustaw kropkę statusu i etykietę opóźnienia wiersza.
        internal void SetRowStatus(ServerInfo server, ServerStatus status, int rttMs)
        {
            if (_serverStatusDot.TryGetValue(server, out var dot)) dot.Fill = _owner.StatusBrush(status);
            if (_serverLatency.TryGetValue(server, out var lat)) lat.Text = RdpUtils.FormatLatency(rttMs);
        }

        /// <summary>Zmienia kolejność serwerów (drag&drop): wstawia <paramref name="dragged"/> przed albo
        /// za <paramref name="target"/> (zależnie od <paramref name="after"/> = połowa wiersza, na którą
        /// upuszczono); upuszczenie na inną grupę przenosi serwer do tej grupy.</summary>
        private void ReorderServer(ServerInfo dragged, ServerInfo target, bool after = false)
        {
            if (dragged == null || target == null || dragged == target) return;
            int from = _owner._vm.Servers.IndexOf(dragged);
            int to = _owner._vm.Servers.IndexOf(target);
            if (from < 0 || to < 0) return;

            dragged.Group = target.Group;   // upuszczenie na inną grupę = przeniesienie do niej

            // Docelowy indeks po usunięciu z „from": przed/za wskazanym wierszem.
            if (after && from > to) to += 1;
            else if (!after && from < to) to -= 1;
            to = Math.Max(0, Math.Min(to, _owner._vm.Servers.Count - 1));

            _owner._vm.Servers.Move(from, to);
            _owner.PersistServers();
            RenderTree(_owner.SearchBox.Text);
            FlashRow(dragged);   // podświetl, gdzie wylądował
        }

        // Pokazuje/aktualizuje linię wskazującą miejsce upuszczenia na krawędzi wiersza.
        private void ShowDropIndicator(Border row, bool bottom)
        {
            var layer = AdornerLayer.GetAdornerLayer(row);
            if (layer == null) { ClearDropIndicator(); return; }

            if (_dropRow == row && _dropAdorner != null)
            {
                if (_dropAdorner.AtBottom != bottom) { _dropAdorner.AtBottom = bottom; _dropAdorner.InvalidateVisual(); }
                return;
            }
            ClearDropIndicator();
            _dropAdorner = new InsertionAdorner(row, _owner.Res("Accent")) { AtBottom = bottom };
            layer.Add(_dropAdorner);
            _dropRow = row;
        }

        private void ClearDropIndicator()
        {
            if (_dropAdorner != null && _dropRow != null)
                AdornerLayer.GetAdornerLayer(_dropRow)?.Remove(_dropAdorner);
            _dropAdorner = null;
            _dropRow = null;
        }

        // Krótkie podświetlenie wiersza (akcent → zanik) po zmianie kolejności — żeby oko złapało, gdzie wylądował.
        private void FlashRow(ServerInfo server)
        {
            if (server == null || !_serverRows.TryGetValue(server, out var row)) return;

            Color accent = (_owner.TryFindResource("Accent") as SolidColorBrush)?.Color ?? Color.FromRgb(0x29, 0xC5, 0xD6);
            var brush = new SolidColorBrush(Color.FromArgb(0x66, accent.R, accent.G, accent.B));
            row.Background = brush;

            var anim = new ColorAnimation
            {
                To = Color.FromArgb(0x00, accent.R, accent.G, accent.B),
                Duration = TimeSpan.FromMilliseconds(700),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            anim.Completed += (s, e) =>
            {
                bool active = _owner._active != null && _owner._active.Server == server;
                row.Background = active ? _owner.Res("AccentSoft") : Brushes.Transparent;
            };
            brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }

        // Import .rdp: upuść pliki z Eksploratora na drzewo serwerów.
        internal void WireTreeFileDrop()
        {
            _owner.ServerTree.Background = Brushes.Transparent;   // hit-test także w pustym obszarze drzewa
            _owner.ServerTree.AllowDrop = true;
            _owner.ServerTree.DragOver += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; e.Handled = true; }
            };
            _owner.ServerTree.Drop += (s, e) =>
            {
                if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
                if (!(e.Data.GetData(DataFormats.FileDrop) is string[] files)) return;
                var rdps = files.Where(f => f.EndsWith(".rdp", StringComparison.OrdinalIgnoreCase)).ToArray();
                _owner.ImportRdpFiles(rdps);
                e.Handled = true;
            };
        }
    }
}
