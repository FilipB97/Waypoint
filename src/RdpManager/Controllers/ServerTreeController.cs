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
        // Pole znacznika stanu (stały rozmiar), nie sam kształt: kształt podmienia się przy każdej
        // zmianie statusu, a pozycja kolumny musi zostać ta sama — patrz Core/StatusGlyph.
        private readonly Dictionary<ServerInfo, Grid> _serverStatusDot = new Dictionary<ServerInfo, Grid>();
        private readonly Dictionary<ServerInfo, TextBlock> _serverLatency = new Dictionary<ServerInfo, TextBlock>();
        private readonly Dictionary<ServerInfo, TextBlock> _serverActions = new Dictionary<ServerInfo, TextBlock>();   // „⋯" pod kursorem (A10)

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

        private ListDensity Density => ListMetrics.ParseDensity(_owner._settings?.ListStyle);
        private GroupLayout Layout => ListMetrics.ParseLayout(_owner._settings?.GroupLayout);

        /// <summary>Wymiary wiersza i sekcji dla bieżącej gęstości i układu — patrz Core/ListMetrics.</summary>
        private ListMetrics Metrics() => ListMetrics.For(Density, Layout);

        private bool IsMinimalList => Density != ListDensity.Default;

        /// <summary>
        /// Czy wiersz ma nieść tag protokołu („RDP"/„SFTP"…). Przy AKTYWNYM filtrze protokołu tag jest
        /// tautologią — cała lista jest już jednego protokołu — a w wierszu wysokości ~22 px konkuruje
        /// z paskiem koloru, kropką statusu, nazwą, opóźnieniem i gwiazdką. Pasek koloru zostaje jako
        /// tożsamość serwera; tag pokazujemy tylko wtedy, gdy naprawdę coś rozróżnia (filtr „Wszystkie").
        /// </summary>
        private bool ShowProtocolTag => _protocolFilter == null;

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

            // Bez chipa „Wszystkie": zjadał ~1/3 szerokości paska i spychał resztę do drugiego rzędu, a to
            // samo robi klik w aktywny chip (zaznaczony → wyczyść). Podpowiedź siedzi w tooltipie chipa.
            foreach (var p in protos)
                _owner.ProtoFilterBar.Children.Add(MakeProtocolChip(MainWindow.ProtocolShort(p), p, _owner.ProtocolBrush(p)));
        }

        private FrameworkElement MakeProtocolChip(string text, RemoteProtocol? proto, Brush accent)
        {
            bool selected = _protocolFilter == proto;
            var chip = new Border
            {
                ToolTip = selected ? L("S.proto.filter.clear") : null,
                // Pełne zaokrąglenie: chip filtra to etykieta-pigułka, a nie mały przycisk — przy
                // promieniu ze skali (8 na wysokości 21) wychodził kształt „prawie pigułka", czyli
                // najgorszy z możliwych. Border przycina promień do połowy boku, więc 999 jest bezpieczne.
                CornerRadius = Radii.Pill,
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
            // Klik w aktywny chip = powrót do „wszystkie" (zastępuje usunięty chip „Wszystkie").
            chip.MouseLeftButtonUp += (s, e) =>
            {
                _protocolFilter = (_protocolFilter == proto) ? null : proto;
                RenderTree(_owner.SearchBox.Text);
            };
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
            _serverActions.Clear();
            _multiSelect.Clear();
            _selectAnchor = null;
            _visibleOrder.Clear();
            _sections.Clear();

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
                EmitSection(PinnedGroupKey, pinned, pinCollapsed, isPinned: true);
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
                EmitSection(g, byGroup[g], collapsed, isPinned: false);
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

            // Nakładka z przyklejonym nagłówkiem musi znać nowe sekcje — i zniknąć, gdy układ grup
            // przestał ją przewidywać. Po ułożeniu, bo pozycje sekcji liczą się dopiero wtedy.
            _stickyKey = null;
            _owner.Dispatcher.BeginInvoke(new System.Action(UpdateStickyHeader),
                                          System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // Nagłówki sekcji w kolejności renderowania — potrzebne do przyklejania nagłówka przy
        // przewijaniu (układ płaski). Element, bo pozycję liczymy dopiero po ułożeniu.
        private readonly List<(string Key, bool Pinned, List<ServerInfo> Servers, bool Collapsed, FrameworkElement Element)> _sections
            = new List<(string, bool, List<ServerInfo>, bool, FrameworkElement)>();

        /// <summary>
        /// Wypuszcza jedną sekcję (nagłówek + wiersze) wedle wybranego układu grup.
        ///
        /// Układ z pasem opakowuje sekcję w kontener z pionową kreską w kolorze grupy — dzięki temu
        /// przynależność widać przy KAŻDYM wierszu, a nie tylko przy nagłówku, więc przy przewijaniu
        /// nie trzeba wracać wzrokiem do góry. Pozostałe układy wypuszczają wiersze wprost do listy.
        /// </summary>
        private void EmitSection(string key, List<ServerInfo> servers, bool collapsed, bool isPinned)
        {
            var m = Metrics();
            var header = BuildGroupHeader(key, servers, collapsed, isPinned);

            if (!m.Rail)
            {
                _owner.ServerTree.Children.Add(header);
                _sections.Add((key, isPinned, servers, collapsed, header));
                if (collapsed) return;
                foreach (var s in servers) { _owner.ServerTree.Children.Add(BuildServerRow(s)); _visibleOrder.Add(s); }
                return;
            }

            // Kolor sekcji: ten sam, którym grupa jest oznaczona wszędzie indziej. Przypięte nie są
            // grupą, więc niosą akcent — to jedyny „kolor bez grupy", jaki lista już zna.
            var color = isPinned ? _owner.Res("Accent") : _owner.GroupDotBrush(key);

            var box = new Grid { Margin = new Thickness(8, 2, 0, 4) };
            box.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(m.RailWidth) });
            box.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var rail = new Rectangle
            {
                Width = m.RailWidth, RadiusX = m.RailWidth / 2, RadiusY = m.RailWidth / 2,
                Fill = color, VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(0, 4, 0, 2)
            };
            Grid.SetColumn(rail, 0);
            box.Children.Add(rail);

            var stack = new StackPanel { Margin = new Thickness(9, 0, 0, 0) };
            Grid.SetColumn(stack, 1);
            box.Children.Add(stack);

            stack.Children.Add(header);
            _sections.Add((key, isPinned, servers, collapsed, header));
            if (!collapsed)
                foreach (var s in servers) { stack.Children.Add(BuildServerRow(s)); _visibleOrder.Add(s); }

            _owner.ServerTree.Children.Add(box);
        }

        /// <summary>
        /// Przyklejony nagłówek grupy (układ „Płaskie sekcje"). WPF nie zna odpowiednika
        /// position: sticky, więc nakładka nad obszarem przewijania pokazuje nagłówek tej sekcji,
        /// której początek wyjechał już ponad górną krawędź.
        ///
        /// Nagłówek jest budowany PONOWNIE, a nie przenoszony: przeniesienie wyjęłoby go z listy,
        /// więc treść pod spodem podskoczyłaby o jego wysokość dokładnie w chwili przyklejenia.
        /// </summary>
        internal void UpdateStickyHeader()
        {
            var host = _owner.StickyGroupHeader;
            if (host == null) return;

            if (!Metrics().StickyHeader || _sections.Count == 0)
            {
                host.Visibility = Visibility.Collapsed;
                host.Child = null;
                _stickyKey = null;
                return;
            }

            double offset = _owner.ServerScroll.VerticalOffset;
            (string Key, bool Pinned, List<ServerInfo> Servers, bool Collapsed, FrameworkElement Element)? current = null;
            foreach (var sec in _sections)
            {
                double y;
                try { y = sec.Element.TransformToAncestor(_owner.ServerTree).Transform(new Point(0, 0)).Y; }
                catch { continue; }   // element jeszcze nieułożony (pierwsze przejście layoutu)
                if (y <= offset + 0.5) current = sec; else break;
            }

            // Pierwsza sekcja w całości widoczna — nie ma czego przyklejać.
            if (current == null) { host.Visibility = Visibility.Collapsed; host.Child = null; _stickyKey = null; return; }

            if (_stickyKey != current.Value.Key)
            {
                _stickyKey = current.Value.Key;
                host.Child = BuildGroupHeader(current.Value.Key, current.Value.Servers,
                                              current.Value.Collapsed, current.Value.Pinned);
            }
            host.Visibility = Visibility.Visible;
        }

        private string _stickyKey;

        private FrameworkElement BuildGroupHeader(string name, List<ServerInfo> servers, bool collapsed, bool isPinned)
        {
            // Włosowa kreska nad każdą grupą POZA pierwszą — rozdziela sekcje bez dokładania pustej
            // przestrzeni. Pierwszy nagłówek jej nie dostaje, bo nad nim jest już pasek chipów.
            var m = Metrics();
            bool first = _owner.ServerTree.Children.Count == 0;
            var row = new Border
            {
                // Minimal: ciaśniejszy padding niż domyślny (lżejsze nagłówki grup i sekcja przypiętych).
                // Padding 12 od lewej stawia tytuł grupy w tej samej kolumnie co kafelki wierszy, a 8
                // od prawej kończy licznik równo z kolumną statusu. Dotąd tytuł zaczynał się 18 px od
                // brzegu, a licznik kończył 6 px — obie krawędzie mijały się z wierszami pod spodem.
                Padding = m.HeaderPadding,
                Background = m.StickyHeader ? _owner.Res("Panel") : Brushes.Transparent,
                BorderBrush = _owner.Res("Border"),
                // Układ płaski daje nagłówkowi własne tło (przykleja się przy przewijaniu, więc musi
                // zasłaniać wiersze pod sobą), a układ z pasem rozdziela sekcje kolorem — w obu
                // kreska nad nagłówkiem byłaby zdublowaniem tego samego sygnału.
                BorderThickness = (first || !m.HeaderRule) ? new Thickness(0) : new Thickness(0, 1, 0, 0),
                // Bez dodatkowego marginesu: kreska włosowa JUŻ rozdziela sekcje. Dotąd działały oba
                // naraz, więc odstęp nad grupą był dwa razy większy, niż wynikało z drabiny odstępów.
                Margin = new Thickness(0),
                Cursor = Cursors.Hand
            };

            // Grid, nie StackPanel: licznik ma siedzieć przy prawej krawędzi (kolumna liczb), a nie
            // doklejony do nazwy przez „ · ” — przy różnych długościach nazw to była poszarpana linia.
            var sp = new Grid();
            sp.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            sp.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            sp.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            sp.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Strzałka zwijania (▸ zwinięte / ▾ rozwinięte).
            var arrow = new TextBlock
            {
                Text = collapsed ? "▸" : "▾",
                // Strzałka to afordancja („tu się klika, żeby zwinąć"), a nie ozdobnik — w TextTer 10px
                // była praktycznie niewidoczna. TextSec + 11px (FontCaption).
                Foreground = _owner.Res("TextSec"), FontSize = (double)_owner.TryFindResource("FontCaption"), Width = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(arrow, 0);
            sp.Children.Add(arrow);

            // Kropka koloru grupy zniknęła: kolor niesie teraz kafelek W KAŻDYM wierszu grupy, więc
            // powtarzanie go w nagłówku dokładało trzeci znacznik do i tak gęstej linii. Gwiazdka
            // sekcji przypiętych zostaje — ona nie ma odpowiednika w wierszu.
            if (isPinned)
            {
                var star = new TextBlock
                {
                    // TextTer, nie Idle: „Idle" to kolor STATUSU (wolna odpowiedź serwera). Ten sam
                    // odcień raz znaczyłby stan, a raz nic — to uczy błędnego kodu barwnego.
                    Text = "★", Foreground = _owner.Res("TextTer"), FontSize = (double)_owner.TryFindResource("FontCaption"),
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
                };
                Grid.SetColumn(star, 1);
                sp.Children.Add(star);
            }

            // Nazwa: mniejsza i wersalikami — nagłówek ma organizować listę, a nie z nią konkurować.
            var title = new TextBlock
            {
                Text = isPinned ? L("S.group.pinned") : name.ToUpperInvariant(),
                Foreground = _owner.Res("TextSec"),
                FontSize = (double)_owner.TryFindResource("FontCaption"), FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(title, 2);
            sp.Children.Add(title);

            var counter = BuildGroupCounter(servers, collapsed);
            Grid.SetColumn(counter, 3);
            sp.Children.Add(counter);

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


        /// <summary>
        /// Licznik grupy. Rozwinięta grupa pokazuje samą liczbę; ZWINIĘTA, w której coś jest
        /// niedostępne, pokazuje „N/M" z N w kolorze Offline.
        ///
        /// Zwinięcie nie może ukryć informacji, że część serwerów nie odpowiada — inaczej jedyny sposób,
        /// żeby się o tym dowiedzieć, to rozwinąć każdą grupę po kolei. Gdy wszystko odpowiada, licznik
        /// zostaje pojedynczy: znacznik należy się stanowi wymagającemu uwagi, nie normie.
        ///
        /// Cyfry tabelaryczne, bo liczniki stoją jedna pod drugą w kolumnie przy prawej krawędzi.
        /// </summary>
        private TextBlock BuildGroupCounter(List<ServerInfo> servers, bool collapsed)
        {
            var model = Core.GroupCounter.For(servers, collapsed);

            var counter = new TextBlock
            {
                // Był 10.5px w TextTer, gdy TextTer miał 2.71 kontrastu — mały i blady naraz. Sam TextTer
                // jest już poprawiony, więc zostaje: trzyma hierarchię wobec nazwy grupy (TextSec, bold).
                Foreground = _owner.Res("TextTer"),
                FontSize = (double)_owner.TryFindResource("FontCaption"),
                FontFamily = (FontFamily)_owner.TryFindResource("Mono"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            System.Windows.Documents.Typography.SetNumeralAlignment(counter, FontNumeralAlignment.Tabular);

            if (model.ShowsOffline)
            {
                counter.Inlines.Add(new System.Windows.Documents.Run(model.Offline.ToString()) { Foreground = _owner.Res("Offline") });
                // Separator przygaszony przez SAM PĘDZEL (Run nie ma Opacity): ma rozdzielać liczby,
                // nie konkurować z nimi.
                counter.Inlines.Add(new System.Windows.Documents.Run("/") { Foreground = TintBrush(_owner.Res("TextTer"), 0.55) });
                counter.Inlines.Add(new System.Windows.Documents.Run(model.Total.ToString()));
                System.Windows.Automation.AutomationProperties.SetName(counter,
                    string.Format(L("S.group.offlinecount"), model.Offline, model.Total));
            }
            else
            {
                counter.Text = model.Total.ToString();
            }
            return counter;
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
        {
            switch (Density)
            {
                case ListDensity.Minimal: return BuildServerRowMinimal(server);
                case ListDensity.Dense: return BuildServerRowDense(server);
                default: return BuildServerRowDefault(server);
            }
        }

        // Wspólne dla wszystkich gęstości: margines i promień wiersza zależą od UKŁADU GRUP, a nie
        // od tego, co jest w wierszu. Układ płaski ciągnie wiersz na pełną szerokość (bez zaokrągleń
        // po bokach), pozostałe zostawiają go jako osobny „kafelek" z wcięciem.
        private void ApplyRowShape(Border row, ListMetrics m)
        {
            row.Margin = new Thickness(m.RowIndent, m.RowGap, 0, m.RowGap);
            row.CornerRadius = m.FullBleedRow ? new CornerRadius(0) : Radii.Sm;
        }

        // Wariant DOMYŚLNY: awatar 22px + dwie linie (nazwa/host) + kropka statusu po prawej.
        private FrameworkElement BuildServerRowDefault(ServerInfo server)
        {
            var m = Metrics();
            var row = new Border
            {
                Padding = m.RowPadding,                 // -2 z każdej strony: miejsce na ramkę fokusu 2 px
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Tag = server
            };
            ApplyRowShape(row, m);   // wcięcie („należę do grupy powyżej") i promień wg układu grup

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });                     // pasek aktywności
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // kafelek
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });  // nazwa
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // etykieta protokołu
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // opóźnienie / „⋯"
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(StatusGlyph.Field) });     // status

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
                Width = 22, Height = 22, CornerRadius = Radii.Sm,
                Background = _owner.AvatarBrush(server), Margin = new Thickness(8, 0, 0, 0),
                Child = new TextBlock
                {
                    // 9.5 px było poniżej progu czytelności dwóch wersalików na 22 px kafelku.
                    Text = MainWindow.ServerInitials(server),
                    Foreground = MainWindow.AvatarInk(_owner.AvatarBrush(server)), FontSize = 10.5, FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            };
            Grid.SetColumn(avatar, 1);
            grid.Children.Add(avatar);

            // Sam adres (DisplayHost) zdjęty z wiersza — nie mieścił się z nazwą; jest w tooltipie (WireServerRow).
            var meta = new StackPanel { Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var nameText = new TextBlock { Text = server.Name, Foreground = _owner.Res("TextPrim"), FontSize = (double)_owner.TryFindResource("FontSmall"), TextTrimming = TextTrimming.CharacterEllipsis };
            meta.Children.Add(nameText);
            Grid.SetColumn(meta, 2);
            grid.Children.Add(meta);

            AddRowRightColumns(grid, server, tagColumn: 3, withTag: m.ProtocolTag);

            var status = StatusGlyph.Host();
            ApplyRowStatusGlyph(status, server.Status);
            _serverStatusDot[server] = status;
            Grid.SetColumn(status, 5);
            grid.Children.Add(status);

            row.Child = grid;

            _serverActivate[server] = active =>
            {
                row.Background = active ? _owner.Res("AccentSoft") : Brushes.Transparent;
                accent.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
                // Samo tło akcentu bywa ledwo widoczne (zwłaszcza na jasnym motywie) — nazwa dobija sygnał.
                nameText.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            };
            WireServerRow(row, server);
            return row;
        }

        // Wariant MINIMALISTYCZNY: jednowierszowy, bez awatara — pasek koloru + kropka statusu + nazwa/host.
        private FrameworkElement BuildServerRowMinimal(ServerInfo server)
        {
            var m = Metrics();
            var row = new Border
            {
                // Padding o 2 mniej z każdej strony, bo doszła stała ramka 2px (przezroczysta w spoczynku,
                // akcent przy fokusie klawiatury). Rezerwujemy ją zawsze, żeby fokus nie przesuwał treści.
                // 2 px, nie 1 — taka sama grubość jak w każdym innym szablonie; przy 1 px obwódka ginęła
                // na tle włosowych kresek listy.
                Padding = m.RowPadding,
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                MinHeight = m.RowMinHeight,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Tag = server
            };
            ApplyRowShape(row, m);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // glif protokołu
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });  // nazwa
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // etykieta protokołu
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // opóźnienie / „⋯"
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(StatusGlyph.Field) });     // status

            // Jeden element po lewej zamiast dwóch: kafelek niesie ORAZ kolor serwera (tożsamość), ORAZ ikonę
            // protokołu (co to za połączenie). Wcześniej stały tu obok siebie pasek koloru i kropka statusu —
            // dwa znaczniki o różnym znaczeniu w odległości kilku pikseli, które oko musiało rozróżniać.
            // Status przenosi się na prawą stronę wiersza, gdzie nic z nim nie konkuruje.
            var serverColor = _owner.AvatarBrush(server);
            var glyph = new Border
            {
                Width = 22, Height = 22, CornerRadius = Radii.Sm,
                Background = TintBrush(serverColor, 0.16),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new Wpf.Ui.Controls.SymbolIcon
                {
                    Symbol = MainWindow.ProtocolSymbol(server.Protocol),
                    // Poza skalą Icon* świadomie: rozmiar jest dobrany do kafelka 22 px, a nie do
                    // hierarchii ikon (IconXs 11 gubi się w kafelku, IconSm 14 go wypełnia po brzegi).
                    FontSize = 13, Foreground = serverColor,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            };
            Grid.SetColumn(glyph, 0);
            grid.Children.Add(glyph);

            var name = new TextBlock
            {
                Text = server.Name, Foreground = _owner.Res("TextPrim"), FontSize = (double)_owner.TryFindResource("FontSmall"), FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(name, 1);
            grid.Children.Add(name);

            // Bez etykiety protokołu: niesie ją glif po lewej. Kolumna 2 zostaje pusta, kolumnę 3
            // wypełnia opóźnienie/„⋯" — te same indeksy w obu gęstościach, więc jedna metoda.
            AddRowRightColumns(grid, server, tagColumn: 2, withTag: false);

            var status = StatusGlyph.Host();
            ApplyRowStatusGlyph(status, server.Status);
            _serverStatusDot[server] = status;
            Grid.SetColumn(status, 4);
            grid.Children.Add(status);

            row.Child = grid;

            _serverActivate[server] = active =>
            {
                row.Background = active ? _owner.Res("AccentSoft") : Brushes.Transparent;
                name.FontWeight = active ? FontWeights.SemiBold : FontWeights.Medium;
            };
            WireServerRow(row, server);
            return row;
        }

        // Wariant GĘSTY: pasek koloru protokołu + nazwa + opóźnienie + znacznik stanu. Bez kafelka
        // i bez awatara, więc wiersz schodzi z 27 px na 22 px — przy trzydziestu serwerach to cztery
        // pozycje więcej bez przewijania.
        //
        // Świadoma strata: znika tożsamość WIZUALNA serwera (kolor grupy w kafelku). Zostaje kolor
        // protokołu, który mówi „czym się łączę", a nie „co to za maszyna" — kto potrzebuje tego
        // drugiego, wybiera gęstość domyślną. Pasek jest przy krawędzi, a nie w środku wiersza, żeby
        // przy przewijaniu tworzył ciągłą kolumnę koloru.
        private FrameworkElement BuildServerRowDense(ServerInfo server)
        {
            var m = Metrics();
            var row = new Border
            {
                Padding = m.RowPadding,
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                MinHeight = m.RowMinHeight,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Tag = server
            };
            ApplyRowShape(row, m);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });                     // pasek protokołu
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });  // nazwa
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // (pusta — bez etykiety)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // opóźnienie / „⋯"
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(StatusGlyph.Field) });     // status

            var bar = new Rectangle
            {
                Width = 3, RadiusX = 1.5, RadiusY = 1.5,
                Fill = _owner.ProtocolBrush(server.Protocol),
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(0, 3, 0, 3)
            };
            Grid.SetColumn(bar, 0);
            grid.Children.Add(bar);

            var name = new TextBlock
            {
                Text = server.Name, Foreground = _owner.Res("TextPrim"),
                FontSize = (double)_owner.TryFindResource("FontSmall"), FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(9, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(name, 1);
            grid.Children.Add(name);

            AddRowRightColumns(grid, server, tagColumn: 2, withTag: false);

            var status = StatusGlyph.Host();
            ApplyRowStatusGlyph(status, server.Status);
            _serverStatusDot[server] = status;
            Grid.SetColumn(status, 4);
            grid.Children.Add(status);

            row.Child = grid;

            _serverActivate[server] = active =>
            {
                row.Background = active ? _owner.Res("AccentSoft") : Brushes.Transparent;
                name.FontWeight = active ? FontWeights.SemiBold : FontWeights.Medium;
            };
            WireServerRow(row, server);
            return row;
        }

        /// <summary>
        /// Przygaszona wersja pędzla serwera na tło kafelka z glifem. Bierze kolor „środkowy" (dla gradientu
        /// pierwszy stop, bo to on definiuje wrażenie barwy) i nakłada mu krycie — kafelek ma być podkładem
        /// pod ikonę, a nie kolejną plamą konkurującą z nazwą. Zamrożony, bo wiersze powstają setkami.
        /// </summary>
        private static Brush TintBrush(Brush source, double opacity)
        {
            Color c = source switch
            {
                SolidColorBrush s => s.Color,
                GradientBrush g when g.GradientStops.Count > 0 => g.GradientStops[0].Color,
                _ => Colors.Gray
            };
            var b = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * opacity), c.R, c.G, c.B));
            b.Freeze();
            return b;
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


        /// <summary>
        /// Prawa strona wiersza jako stałe kolumny, nie <c>StackPanel</c>. Panel układał elementy jeden
        /// za drugim, więc kolumna statusu wędrowała w poziomie zależnie od tego, czy wiersz ma etykietę
        /// protokołu i ile cyfr ma opóźnienie — a oko skanuje tę kolumnę pionowo i potrzebuje jej w tym
        /// samym miejscu w każdym wierszu.
        ///
        /// Znacznik statusu siedzi w OSTATNIEJ kolumnie o stałej szerokości, więc jest zakotwiczony do
        /// prawej krawędzi niezależnie od reszty. Opóźnienie dostaje cyfry tabelaryczne i wyrównanie do
        /// prawej — inaczej „9 ms" i „128 ms" nie stoją w jednej kolumnie liczb.
        ///
        /// Ta sama komórka niesie akcje wiersza (A10): pod kursorem opóźnienie ustępuje „⋯" DOKŁADNIE
        /// w swoim miejscu, więc nic nie skacze. Menu wiersza wymagało dotąd prawego klawisza, czyli
        /// było niewidoczne dla kogoś, kto go nie spróbuje.
        /// </summary>
        private void AddRowRightColumns(Grid grid, ServerInfo server, int tagColumn, bool withTag)
        {
            if (withTag && ShowProtocolTag)
            {
                var tag = BuildProtocolTag(server);
                Grid.SetColumn(tag, tagColumn);
                grid.Children.Add(tag);
            }

            bool showLatency = _owner._settings != null && _owner._settings.ShowLatency;
            var meta = new Grid
            {
                // 46 px mieści „<1 ms" i „1234 ms"; bez opóźnień wystarczy miejsce na samo „⋯".
                MinWidth = showLatency ? 46 : 26,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            Grid.SetColumn(meta, tagColumn + 1);
            grid.Children.Add(meta);

            if (showLatency)
            {
                var lat = new TextBlock
                {
                    Text = RdpUtils.FormatLatency(server.LatencyMs),
                    Foreground = _owner.Res("TextTer"),
                    FontSize = (double)_owner.TryFindResource("FontCaption"),
                    FontFamily = (FontFamily)_owner.TryFindResource("Mono"),
                    TextAlignment = TextAlignment.Right,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Center
                };
                System.Windows.Documents.Typography.SetNumeralAlignment(lat, FontNumeralAlignment.Tabular);
                _serverLatency[server] = lat;
                meta.Children.Add(lat);
            }

            var more = new TextBlock
            {
                Text = "⋯",
                Foreground = _owner.Res("TextSec"),
                FontSize = (double)_owner.TryFindResource("FontBody"),
                TextAlignment = TextAlignment.Right,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
                Cursor = Cursors.Hand,
                ToolTip = L("S.row.actions")
            };
            System.Windows.Automation.AutomationProperties.SetName(more, L("S.row.actions"));
            more.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;   // bez tego klik w „⋯" wpadłby w handler wiersza i połączył sesję
                var menu = BuildServerContextMenu(server);
                menu.PlacementTarget = more;
                menu.IsOpen = true;
            };
            meta.Children.Add(more);
            _serverActions[server] = more;
        }

        // Pod kursorem: opóźnienie ustępuje „⋯". Hidden, nie Collapsed — komórka ma trzymać szerokość,
        // żeby wiersz nie drgnął w chwili, gdy użytkownik celuje w niego myszą.
        private void ToggleRowActions(ServerInfo server, bool hovering)
        {
            if (!_serverActions.TryGetValue(server, out var more)) return;
            more.Visibility = hovering ? Visibility.Visible : Visibility.Collapsed;
            if (_serverLatency.TryGetValue(server, out var lat))
                lat.Visibility = hovering ? Visibility.Hidden : Visibility.Visible;
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

            // Hover i fokus klawiatury malowały DOKŁADNIE to samo tło (Elevated), więc przechodząc listę
            // Tabem z myszą leżącą nad innym wierszem nie dało się powiedzieć, który jest który. Fokus
            // dostaje własny znak — obwódkę akcentu — i jest niezależny od tła: może wystąpić razem
            // z hoverem i z zaznaczeniem, i wtedy widać wszystkie trzy stany naraz.
            row.MouseEnter += (s, e) =>
            {
                if (_owner._active?.Server != server) row.Background = RowHoverBackground(server);
                ToggleRowActions(server, true);
            };
            row.MouseLeave += (s, e) =>
            {
                if (_owner._active?.Server != server) row.Background = RowRestBackground(server);
                ToggleRowActions(server, false);
            };
            row.GotKeyboardFocus += (s, e) => row.BorderBrush = _owner.Res("Accent");
            row.LostKeyboardFocus += (s, e) => row.BorderBrush = Brushes.Transparent;
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
        /// <summary>
        /// Wstawia kształt statusu do pola wiersza. Kolor idzie z palety przez klucz, a nie przez
        /// StatusBrush — dzięki temu kształt i barwa są opisane w JEDNYM miejscu (StatusGlyph).
        /// </summary>
        private void ApplyRowStatusGlyph(Grid host, ServerStatus status)
        {
            var (shape, key) = StatusGlyph.For(status);
            StatusGlyph.Set(host, shape, _owner.Res(key));
        }

        private Brush RowRestBackground(ServerInfo s)
            => _multiSelect.Contains(s) ? _owner.Res("AccentSoft") : Brushes.Transparent;

        /// <summary>
        /// Tło wiersza POD KURSOREM. Musi być krokiem NAD stanem spoczynkowym, a nie jego zamiennikiem:
        /// wcześniej hover ustawiał „Elevated" bezwarunkowo, więc przejechanie myszą nad zaznaczeniem
        /// gasiło „AccentSoft" na wszystkich mijanych wierszach — zaznaczenie znikało w trakcie wodzenia
        /// wzrokiem po liście, choć logicznie trwało (RowRestBackground uwzględniał _multiSelect,
        /// MouseEnter nie). Zaznaczony wiersz dostaje mocniejszy stopień TEGO SAMEGO akcentu,
        /// niezaznaczony — neutralne uniesienie.
        /// </summary>
        private Brush RowHoverBackground(ServerInfo s)
            => _multiSelect.Contains(s) ? _owner.Res("AccentSoftHover") : _owner.Res("Elevated");

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
            if (_serverStatusDot.TryGetValue(server, out var dot)) ApplyRowStatusGlyph(dot, status);
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
                if (_dropAdorner.AtEnd != bottom) { _dropAdorner.AtEnd = bottom; _dropAdorner.InvalidateVisual(); }
                return;
            }
            ClearDropIndicator();
            _dropAdorner = new InsertionAdorner(row, _owner.Res("Accent")) { AtEnd = bottom };
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

            Color accent = (_owner.TryFindResource("Accent") as SolidColorBrush)?.Color ?? Color.FromRgb(0x6C, 0x6D, 0xFF);
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
