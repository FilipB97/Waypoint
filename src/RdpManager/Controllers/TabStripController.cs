using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using RdpManager.Core;
using RdpManager.Models;

namespace RdpManager.Controllers
{
    /// <summary>
    /// Pasek kart sesji: budowa karty (styl domyślny/minimalny), zachowanie karty (hover / klik /
    /// środkowy-klik / przeciąganie / menu), grupy kart „jak w Vivaldi" (tworzenie/dodawanie/rozgrupowanie,
    /// zapis do ustawień, kontenery i zwijanie), zmiana kolejności drag&drop oraz odbudowa/odświeżanie paska.
    /// Wyniesione 1:1 z MainWindow (PR 4 planu docs/REFACTOR-MAINWINDOW.md, wzorzec „back-reference
    /// move-method") — bez zmian logiki. Cykl życia sesji i podział ekranu zostają w MainWindow (PR 5/6)
    /// i są wołane przez <c>_owner.</c>; kropkę statusu karty aktualizuje szew <see cref="SetTabStatus"/>,
    /// a sprzątanie przy zamknięciu — <see cref="OnSessionClosed"/>.
    /// </summary>
    internal sealed class TabStripController
    {
        private readonly MainWindow _owner;

        // Elementy karty per sesja (podkreślenie aktywnej / kropka statusu / nazwa / ✕) — do odświeżania w miejscu.
        private readonly Dictionary<Session, Rectangle> _tabUnderline = new Dictionary<Session, Rectangle>();
        private readonly Dictionary<Session, Ellipse> _tabStatus = new Dictionary<Session, Ellipse>();
        private readonly Dictionary<Session, TextBlock> _tabName = new Dictionary<Session, TextBlock>();
        private readonly Dictionary<Session, TextBlock> _tabClose = new Dictionary<Session, TextBlock>();
        // Grupy kart (stosy jak w Vivaldi). Przynależność po Id serwera (w TabGroup.ServerIds), więc
        // grupy zapisują się do ustawień i wracają po restarcie. Runtime-lista ładowana z _settings.
        private readonly List<TabGroup> _tabGroups = new List<TabGroup>();

        // Drag&drop kart w pasku (grupowanie / zmiana kolejności).
        private Point _tabDragStart;
        private Session _tabDragSession;
        private bool _tabDidDrag;
        // Podpowiedź przy przeciąganiu karty: środek celu = podświetlenie („zgrupuj"), brzeg = pionowa
        // krawędź („wstaw przed/za"). Czyszczenie przywraca style wszystkich kart (RefreshTabStyles).
        private Border _tabDropTarget;

        // Paleta kolorów grup (spójna z akcentami/awatarami motywu). Nowa grupa dostaje pierwszy nieużyty.
        private static readonly Color[] GroupColors =
        {
            Color.FromRgb(0x7C, 0x6C, 0xFB),  // fiolet
            Color.FromRgb(0x36, 0xB8, 0xC4),  // turkus
            Color.FromRgb(0xFF, 0xB4, 0x54),  // bursztyn
            Color.FromRgb(0x37, 0x8A, 0xDD),  // błękit
            Color.FromRgb(0xD4, 0x53, 0x7E),  // róż
            Color.FromRgb(0x3D, 0xDC, 0x97),  // zieleń
        };
        private const string GroupMenuMark = "grp";   // znacznik pozycji menu karty wstrzykiwanych dla grup

        private static string L(string key) => LocalizationManager.S(key);

        public TabStripController(MainWindow owner) => _owner = owner;

        private bool IsMinimalList => _owner._settings != null && _owner._settings.ListStyle == "Minimal";

        // ---------- Pasek zakładek ----------

        internal FrameworkElement BuildTab(Session session)
            => IsMinimalList ? BuildTabMinimal(session) : BuildTabDefault(session);

        // Wariant DOMYŚLNY: awatar 14px + nazwa + kropka statusu + ✕; aktywna = podkreślenie akcentem.
        private FrameworkElement BuildTabDefault(Session session)
        {
            var tab = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Transparent,
                Background = Brushes.Transparent,
                Padding = new Thickness(10, 6, 7, 5),
                Margin = new Thickness(0, 0, 4, 0),
                Cursor = Cursors.Hand,
                Tag = session,
                ToolTip = session.Server.Name + " — " + MainWindow.DisplayHost(session.Server)
            };

            // 2 wiersze: treść (góra) + pasek podświetlenia (dół) z odstępem — pasek nie nachodzi na nazwę.
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new Border
            {
                Width = 14, Height = 14, CornerRadius = new CornerRadius(4),
                Background = _owner.AvatarBrush(session.Server), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = MainWindow.ServerInitials(session.Server), Foreground = Brushes.White, FontSize = 7, FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            });
            var tabName = new TextBlock
            {
                Text = session.Server.Name, Foreground = _owner.Res("TextPrim"), FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0)
            };
            _tabName[session] = tabName;
            content.Children.Add(tabName);
            // Adres nie jest już na karcie (był w 3 miejscach naraz) — zostaje w pasku bocznym,
            // podpowiedzi karty i szybkim przełączaniu. Karta = ikona + nazwa + kropka + ✕.
            // Kropka odzwierciedla ŻYWY stan sesji (nie statyczny status serwera): startowo rozłączona.
            var tabDot = new Ellipse
            {
                Width = 6, Height = 6, Fill = _owner.StatusBrush(ServerStatus.Offline),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0)
            };
            _tabStatus[session] = tabDot;
            content.Children.Add(tabDot);
            content.Children.Add(BuildTabClose(session));
            Grid.SetRow(content, 0);
            grid.Children.Add(content);

            var underline = BuildTabUnderline(new Thickness(2, 4, 2, 0));
            Grid.SetRow(underline, 1);
            grid.Children.Add(underline);

            tab.Child = grid;
            _tabUnderline[session] = underline;
            WireTab(tab, session);
            return tab;
        }

        // Wariant MINIMALISTYCZNY: kropka statusu + nazwa (bez awatara i hosta) — niższa, lżejsza karta.
        private FrameworkElement BuildTabMinimal(Session session)
        {
            var tab = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Transparent,
                Background = Brushes.Transparent,
                Padding = new Thickness(11, 2, 6, 2),
                Margin = new Thickness(0, 0, 4, 0),
                Cursor = Cursors.Hand,
                Tag = session,
                ToolTip = session.Server.Name + " — " + MainWindow.DisplayHost(session.Server)
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            var tabDot = new Ellipse
            {
                Width = 7, Height = 7, Fill = _owner.StatusBrush(ServerStatus.Offline), VerticalAlignment = VerticalAlignment.Center
            };
            _tabStatus[session] = tabDot;
            content.Children.Add(tabDot);
            var tabName = new TextBlock
            {
                Text = session.Server.Name, Foreground = _owner.Res("TextPrim"), FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0)
            };
            _tabName[session] = tabName;
            content.Children.Add(tabName);
            content.Children.Add(BuildTabClose(session));
            Grid.SetRow(content, 0);
            grid.Children.Add(content);

            var underline = BuildTabUnderline(new Thickness(2, 2, 2, 0));
            Grid.SetRow(underline, 1);
            grid.Children.Add(underline);

            tab.Child = grid;
            _tabUnderline[session] = underline;
            WireTab(tab, session);
            return tab;
        }

        // ✕ karty (wspólny dla obu stylów): pokazywany na aktywnej/hoverze (Hidden, nie Collapsed — stała szerokość).
        private TextBlock BuildTabClose(Session session)
        {
            var close = new TextBlock
            {
                Text = "✕", Foreground = _owner.Res("TextTer"), FontSize = 11,
                Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(3, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand,
                Visibility = Visibility.Hidden
            };
            close.MouseEnter += (s, e) => close.Foreground = _owner.Res("Danger");
            close.MouseLeave += (s, e) => close.Foreground = _owner.Res("TextTer");
            close.MouseLeftButtonUp += (s, e) => { e.Handled = true; _owner.RequestCloseSession(session); };
            _tabClose[session] = close;
            return close;
        }

        private Rectangle BuildTabUnderline(Thickness margin) => new Rectangle
        {
            Height = 2, Fill = _owner.Res("Accent"), RadiusX = 1, RadiusY = 1,
            Margin = margin, Visibility = Visibility.Hidden   // Hidden: karta ma stałą wysokość aktywna/nie
        };

        // Wspólne zachowanie karty (hover / klik / środkowy-klik / przeciąganie: grupuj lub zmień kolejność / menu).
        private void WireTab(Border tab, Session session)
        {
            tab.MouseEnter += (s, e) =>
            {
                if (session != _owner._active) tab.Background = _owner.Res("Elevated") ?? Brushes.Transparent;
                if (_tabClose.TryGetValue(session, out var c)) c.Visibility = Visibility.Visible;
            };
            tab.MouseLeave += (s, e) => RefreshTabStyles();
            tab.MouseLeftButtonUp += (s, e) =>
            {
                if (_tabDidDrag) { _tabDidDrag = false; return; }   // to było przeciąganie, nie klik
                _owner.Activate(session);
            };
            // Środkowy klik zamyka kartę (standard z przeglądarek).
            tab.MouseDown += (s, e) =>
            {
                if (e.ChangedButton == MouseButton.Middle) { _owner.RequestCloseSession(session); e.Handled = true; }
            };

            tab.AllowDrop = true;
            tab.PreviewMouseLeftButtonDown += (s, e) => { _tabDragStart = e.GetPosition(null); _tabDragSession = session; _tabDidDrag = false; };
            tab.PreviewMouseMove += (s, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || _tabDragSession != session) return;
                var pos = e.GetPosition(null);
                if (Math.Abs(pos.X - _tabDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(pos.Y - _tabDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                _tabDidDrag = true;
                tab.Opacity = 0.5;
                bool zone = _owner.ShowSplitDropZone(session);   // „upuść w obszar sesji, aby podzielić" (tylko RDP, ≥2 sesje)
                try { DragDrop.DoDragDrop(tab, session, DragDropEffects.Move); }
                catch { }
                finally { tab.Opacity = 1.0; _tabDragSession = null; if (zone) _owner.HideSplitDropZone(); }
            };
            tab.DragOver += (s, e) =>
            {
                if (!(e.Data.GetData(typeof(Session)) is Session over) || over == session)
                { e.Effects = DragDropEffects.None; return; }
                e.Effects = DragDropEffects.Move; e.Handled = true;
                double x = e.GetPosition(tab).X, w = tab.ActualWidth;
                ShowTabDropIndicator(tab, group: x > w * 0.33 && x < w * 0.67, after: x >= w / 2);
            };
            tab.DragLeave += (s, e) => ClearTabDropIndicator();
            tab.Drop += (s, e) =>
            {
                ClearTabDropIndicator();
                if (!(e.Data.GetData(typeof(Session)) is Session dragged) || dragged == session) return;
                double x = e.GetPosition(tab).X, w = tab.ActualWidth;
                if (x > w * 0.33 && x < w * 0.67) GroupTabs(session, dragged);   // środek celu = grupuj
                else MoveTabTo(dragged, session, after: x >= w / 2);              // brzeg = zmiana kolejności
                e.Handled = true;
            };

            var tabMenu = new ContextMenu();
            var tearItem = new MenuItem { Header = L("S.m.tearoff") };
            tearItem.Click += (s, e) => _owner.TearOffToWindow(session);
            var dupItem = new MenuItem { Header = L("S.m.duplicate") };
            dupItem.Click += (s, e) => _owner.DuplicateSession(session);
            var moveLeft = new MenuItem { Header = L("S.m.moveleft") };
            moveLeft.Click += (s, e) => MoveTab(session, -1);
            var moveRight = new MenuItem { Header = L("S.m.moveright") };
            moveRight.Click += (s, e) => MoveTab(session, +1);
            var closeOthers = new MenuItem { Header = L("S.m.closeothers") };
            closeOthers.Click += (s, e) => _owner.CloseOtherSessions(session);
            var closeThis = new MenuItem { Header = L("S.m.close") };
            closeThis.Click += (s, e) => _owner.RequestCloseSession(session);
            if (session.IsSsh)
            {
                var broadcastItem = new MenuItem { Header = L("S.m.broadcast") };
                broadcastItem.Click += (s, e) => _owner.BroadcastToSsh();
                tabMenu.Items.Add(broadcastItem);
                tabMenu.Items.Add(new Separator());
            }
            MenuItem splitItem = null, unsplitItem = null;
            if (session.Server.Protocol == RemoteProtocol.Rdp)
            {
                tabMenu.Items.Add(tearItem);   // wyciąganie do okna jest RDP-owe
                var cadItem = new MenuItem { Header = L("S.m.cad") };
                cadItem.Click += (s, e) => _owner.SendCtrlAltDel(session);
                tabMenu.Items.Add(cadItem);
                splitItem = new MenuItem { Header = L("S.m.split") };      // ta sesja w prawym panelu, aktywna w lewym
                splitItem.Click += (s, e) => _owner.EnterSplit(session);
                unsplitItem = new MenuItem { Header = L("S.m.unsplit") };
                unsplitItem.Click += (s, e) => _owner.ExitSplit();
                tabMenu.Items.Add(splitItem);
                tabMenu.Items.Add(unsplitItem);
            }
            tabMenu.Items.Add(dupItem);
            tabMenu.Items.Add(new Separator());
            tabMenu.Items.Add(moveLeft);
            tabMenu.Items.Add(moveRight);
            tabMenu.Items.Add(new Separator());
            tabMenu.Items.Add(closeOthers);
            tabMenu.Items.Add(closeThis);
            tab.ContextMenu = tabMenu;
            // Pozycje dot. grup zależą od bieżącego stanu (jakie grupy istnieją) — wstrzykiwane przy otwarciu.
            tabMenu.Opened += (s, e) =>
            {
                PopulateTabGroupItems(tabMenu, session);
                if (splitItem != null)   // „Podziel" gdy są ≥2 sesje RDP i nie ma podziału; „Zakończ podział" w podziale
                {
                    bool split = _owner._paneLeft != null && _owner._paneRight != null;
                    int rdp = _owner._sessions.Count(x => x.Server.Protocol == RemoteProtocol.Rdp);
                    splitItem.Visibility = (!split && rdp >= 2) ? Visibility.Visible : Visibility.Collapsed;
                    unsplitItem.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
                }
            };
        }

        internal void RefreshTabStyles()
        {
            foreach (var s in _owner._sessions)
            {
                if (!(s.TabButton is Border b)) continue;
                bool active = s == _owner._active;
                // Lżej: aktywna = subtelne tło + akcent (underline), bez „pudełkowego" obrysu.
                b.Background = active ? _owner.Res("Panel") : Brushes.Transparent;
                b.BorderBrush = Brushes.Transparent;
                // Hierarchia: nieaktywne karty przygaszone (spokojniejszy pasek).
                if (_tabName.TryGetValue(s, out var nm))
                    nm.Foreground = _owner.Res(active ? "TextPrim" : "TextSec");
                if (_tabUnderline.TryGetValue(s, out var u))
                    u.Visibility = active ? Visibility.Visible : Visibility.Hidden;
                if (_tabClose.TryGetValue(s, out var c))
                    c.Visibility = active ? Visibility.Visible : Visibility.Hidden;   // ✕ tylko na aktywnej/hoverze
            }
        }

        /// <summary>
        /// Rozróżnia zakładki o tej samej nazwie: dopisuje host, a przy duplikatach tej samej
        /// sesji (identyczna nazwa i host) — numer wystąpienia (#2, #3…).
        /// </summary>
        internal void RefreshTabTitles()
        {
            var nameSeen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in _owner._sessions)
            {
                if (!_tabName.TryGetValue(s, out var tn)) continue;

                bool dupName = _owner._sessions.Any(o => o != s &&
                    string.Equals(o.Server.Name, s.Server.Name, StringComparison.OrdinalIgnoreCase));
                string title = dupName ? s.Server.Name + " (" + s.Server.Host + ")" : s.Server.Name;

                nameSeen.TryGetValue(title, out int seen);
                nameSeen[title] = seen + 1;
                if (seen > 0) title += " #" + (seen + 1);   // duplikaty tej samej sesji

                tn.Text = title;
            }
        }

        /// <summary>Wstawia przeciąganą zakładkę przed/za <paramref name="target"/> (drag&amp;drop w pasku).</summary>
        private void MoveTabTo(Session dragged, Session target, bool after)
        {
            int from = _owner._sessions.IndexOf(dragged);
            if (from < 0 || _owner._sessions.IndexOf(target) < 0) return;
            _owner._sessions.RemoveAt(from);
            int to = _owner._sessions.IndexOf(target) + (after ? 1 : 0);
            _owner._sessions.Insert(to, dragged);
            RebuildTabStrip();   // odbudowa respektuje grupy (kontenery) i numerację duplikatów
        }

        /// <summary>Przesuwa zakładkę w pasku o <paramref name="dir"/> (-1 w lewo, +1 w prawo).</summary>
        private void MoveTab(Session s, int dir)
        {
            int i = _owner._sessions.IndexOf(s);
            int j = i + dir;
            if (i < 0 || j < 0 || j >= _owner._sessions.Count) return;

            _owner._sessions.RemoveAt(i);
            _owner._sessions.Insert(j, s);
            RebuildTabStrip();   // odbudowa respektuje grupy (kontenery) i numerację duplikatów
        }

        // ---------- Grupy kart (stosy jak w Vivaldi) ----------

        internal TabGroup GroupOf(Session s) => s == null ? null : _tabGroups.FirstOrDefault(g => g.ServerIds.Contains(s.Server.Id));

        /// <summary>Czy istnieje zwinięta grupa — Activate przebudowuje pasek, by „wypchnąć" aktywną kartę ze stosu.</summary>
        internal bool HasCollapsedGroups => _tabGroups.Any(g => g.Collapsed);

        private Color NextGroupColor()
        {
            foreach (var c in GroupColors)
                if (!_tabGroups.Any(g => g.Color == c)) return c;
            return GroupColors[_tabGroups.Count % GroupColors.Length];
        }

        // Wypina serwer ze wszystkich grup i kasuje grupy, które przez to zostały puste.
        private void DetachServerFromGroups(string serverId)
        {
            foreach (var g in _tabGroups) g.ServerIds.Remove(serverId);
            _tabGroups.RemoveAll(g => g.ServerIds.Count == 0);
        }

        private void CreateGroupFromTab(Session seed)
        {
            string suggested = string.IsNullOrWhiteSpace(seed.Server.Group) ? L("S.group.default") : seed.Server.Group;
            var dlg = new InputDialog(L("S.group.newtitle"), L("S.group.nameprompt"), suggested) { Owner = _owner };
            if (dlg.ShowDialog() != true) return;
            DetachServerFromGroups(seed.Server.Id);
            var group = new TabGroup { Name = string.IsNullOrWhiteSpace(dlg.Value) ? suggested : dlg.Value, Color = NextGroupColor() };
            group.ServerIds.Add(seed.Server.Id);
            _tabGroups.Add(group);
            SaveTabGroups();
            RebuildTabStrip();
        }

        private void AddToGroup(Session s, TabGroup g)
        {
            if (s == null || g == null) return;
            DetachServerFromGroups(s.Server.Id);          // przenieś z ewentualnej innej grupy
            if (!_tabGroups.Contains(g)) return;          // (gdyby odpięcie ją opróżniło)
            if (!g.ServerIds.Contains(s.Server.Id)) g.ServerIds.Add(s.Server.Id);
            SaveTabGroups();
            RebuildTabStrip();
        }

        // Upuszczenie karty NA środek innej (jak w Vivaldi): tworzy grupę z obu (gdy cel luzem) albo
        // dokłada przeciąganą do grupy celu. Bez pytania o nazwę — nazwę zmienia się z menu pastylki.
        private void GroupTabs(Session target, Session dragged)
        {
            if (target == null || dragged == null || target == dragged || target.Server.Id == dragged.Server.Id) return;
            var g = GroupOf(target);
            if (g == null)
            {
                g = new TabGroup { Name = AutoGroupName(target), Color = NextGroupColor() };
                g.ServerIds.Add(target.Server.Id);
                _tabGroups.Add(g);
            }
            DetachServerFromGroups(dragged.Server.Id);    // wyjmij z ewentualnej starej grupy
            if (!_tabGroups.Contains(g)) return;
            if (!g.ServerIds.Contains(dragged.Server.Id)) g.ServerIds.Add(dragged.Server.Id);
            SaveTabGroups();
            RebuildTabStrip();
        }

        private string AutoGroupName(Session seed) =>
            string.IsNullOrWhiteSpace(seed.Server.Group) ? L("S.group.default") : seed.Server.Group;

        private void RemoveFromGroup(Session s)
        {
            if (s == null) return;
            DetachServerFromGroups(s.Server.Id);
            SaveTabGroups();
            RebuildTabStrip();
        }

        private void Ungroup(TabGroup g)
        {
            _tabGroups.Remove(g);
            SaveTabGroups();
            RebuildTabStrip();
        }

        // Zapis/odczyt grup w ustawieniach (kolor jako #AARRGGBB) — grupy przeżywają restart aplikacji.
        private void SaveTabGroups()
        {
            _owner._settings.TabGroups = _tabGroups.Select(g => new TabGroupDef
            {
                Name = g.Name,
                Color = string.Format("#{0:X2}{1:X2}{2:X2}{3:X2}", g.Color.A, g.Color.R, g.Color.G, g.Color.B),
                Collapsed = g.Collapsed,
                ServerIds = g.ServerIds.ToList()
            }).ToList();
            SettingsStore.Save(_owner._settings);
        }

        internal void LoadTabGroups()
        {
            _tabGroups.Clear();
            foreach (var d in _owner._settings.TabGroups ?? new List<TabGroupDef>())
            {
                Color color;
                try { color = (Color)ColorConverter.ConvertFromString(d.Color); }
                catch { color = GroupColors[0]; }
                _tabGroups.Add(new TabGroup
                {
                    Name = d.Name, Color = color, Collapsed = d.Collapsed,
                    ServerIds = (d.ServerIds ?? new List<string>()).ToList()
                });
            }
        }

        private static void DetachTab(Session s)
        {
            if (s?.TabButton is FrameworkElement fe && fe.Parent is Panel p) p.Children.Remove(fe);
        }

        // Minimal: niższy pasek kart (mniejszy margines) i drobniejsze ikony sesji po prawej stronie paska.
        internal void ApplyTabStripStyle()
        {
            bool min = IsMinimalList;
            _owner.TabStrip.Margin = new Thickness(8, min ? 2 : 6, 8, min ? 2 : 6);
            foreach (var b in _owner.SessionActions.Children.OfType<Button>())
            {
                b.Width = min ? 24 : 28;
                b.Height = min ? 24 : 28;
            }
        }

        private void ShowTabDropIndicator(Border tab, bool group, bool after)
        {
            ClearTabDropIndicator();
            _tabDropTarget = tab;
            if (group)
            {
                tab.Background = _owner.Res("AccentSoft");
                tab.BorderBrush = _owner.Res("Accent");
                tab.BorderThickness = new Thickness(1);
            }
            else
            {
                tab.BorderBrush = _owner.Res("Accent");
                tab.BorderThickness = after ? new Thickness(0, 0, 2, 0) : new Thickness(2, 0, 0, 0);
            }
        }

        private void ClearTabDropIndicator()
        {
            if (_tabDropTarget == null) return;
            _tabDropTarget.BorderThickness = new Thickness(1);   // domyślna grubość z BuildTab
            _tabDropTarget = null;
            RefreshTabStyles();
        }

        /// <summary>Porządkuje _sessions tak, by członkowie każdej grupy stali obok siebie (stabilnie, wg
        /// pierwszego wystąpienia) — dzięki temu grupa renderuje się jako jeden kontener.</summary>
        private void NormalizeGroupOrder()
        {
            var ordered = new List<Session>(_owner._sessions.Count);
            var emitted = new HashSet<TabGroup>();
            foreach (var s in _owner._sessions)
            {
                var g = GroupOf(s);
                if (g == null) { ordered.Add(s); continue; }
                if (emitted.Add(g)) ordered.AddRange(_owner._sessions.Where(x => GroupOf(x) == g));
            }
            _owner._sessions.Clear();
            _owner._sessions.AddRange(ordered);
        }

        /// <summary>Przebudowuje pasek: karty luzem trafiają wprost do paska, a ciągi kart tej samej grupy —
        /// do wspólnego kontenera (z możliwością zwinięcia do liczby). Odłącza karty od starych rodziców.</summary>
        internal void RebuildTabStrip()
        {
            ApplyTabStripStyle();
            foreach (var s in _owner._sessions) DetachTab(s);   // karta = jeden rodzic naraz
            _owner.TabStrip.Children.Clear();
            NormalizeGroupOrder();

            int i = 0;
            while (i < _owner._sessions.Count)
            {
                var g = GroupOf(_owner._sessions[i]);
                if (g == null) { _owner.TabStrip.Children.Add(_owner._sessions[i].TabButton); i++; continue; }

                var members = new List<Session>();
                while (i < _owner._sessions.Count && GroupOf(_owner._sessions[i]) == g) { members.Add(_owner._sessions[i]); i++; }
                _owner.TabStrip.Children.Add(BuildGroupContainer(g, members));
            }

            RefreshTabTitles();
            RefreshTabStyles();
        }

        private FrameworkElement BuildGroupContainer(TabGroup g, List<Session> members)
        {
            var color = g.Color;
            var tint = new SolidColorBrush(Color.FromArgb(0x22, color.R, color.G, color.B));
            var strong = new SolidColorBrush(Color.FromArgb(0x3A, color.R, color.G, color.B));

            var box = new Border
            {
                CornerRadius = new CornerRadius(8), Background = tint, BorderBrush = strong, BorderThickness = new Thickness(1),
                Padding = new Thickness(3, 0, 4, 0), Margin = new Thickness(0, 0, 5, 0)
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            box.Child = row;

            // Pastylka z nazwą: klik = zwiń/rozwiń; prawy klik = menu (nazwa / kolor / rozgrupuj).
            var pill = new Border
            {
                CornerRadius = new CornerRadius(5), Background = strong, Cursor = Cursors.Hand,
                Padding = new Thickness(6, IsMinimalList ? 1 : 2, 7, IsMinimalList ? 1 : 3),
                Margin = new Thickness(1, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
                ContextMenu = BuildGroupMenu(g)
            };
            var pillRow = new StackPanel { Orientation = Orientation.Horizontal };
            pillRow.Children.Add(new TextBlock
            {
                Text = g.Collapsed ? "▸" : "▾", Foreground = new SolidColorBrush(color), FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0)
            });
            pillRow.Children.Add(new TextBlock
            {
                Text = g.Name, Foreground = new SolidColorBrush(color), FontSize = IsMinimalList ? 11 : 11.5, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            if (g.Collapsed)
                pillRow.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(9), Background = _owner.Res("Elevated"),
                    Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = members.Count.ToString(), Foreground = _owner.Res("TextSec"), FontSize = 10.5 }
                });
            pill.Child = pillRow;
            // e.Handled: klik przebudowuje pasek (usuwa tę pastylkę) — nie pozwól zdarzeniu bąbelkować dalej.
            pill.MouseLeftButtonUp += (s, e) => { e.Handled = true; g.Collapsed = !g.Collapsed; SaveTabGroups(); RebuildTabStrip(); };
            row.Children.Add(pill);

            // Rozwinięta: wszystkie karty. Zwinięta: pastylka + licznik, ale aktywna karta „wychodzi" ze
            // stosu (jak w Vivaldi) — widać, którą sesję się ogląda. Przełączenie aktywnej odświeża pasek.
            foreach (var m in members)
                if (!g.Collapsed || m == _owner._active) row.Children.Add(m.TabButton);

            return box;
        }

        private ContextMenu BuildGroupMenu(TabGroup g)
        {
            var menu = new ContextMenu();

            var rename = new MenuItem { Header = L("S.m.grp.rename") };
            rename.Click += (s, e) =>
            {
                var dlg = new InputDialog(L("S.group.renametitle"), L("S.group.nameprompt"), g.Name) { Owner = _owner };
                if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Value)) { g.Name = dlg.Value; SaveTabGroups(); RebuildTabStrip(); }
            };
            menu.Items.Add(rename);

            var colorItem = new MenuItem { Header = L("S.m.grp.color") };
            foreach (var c in GroupColors)
            {
                var cc = c;
                var swatch = new MenuItem { Header = new TextBlock { Text = "●", Foreground = new SolidColorBrush(cc), FontSize = 15 } };
                swatch.Click += (s, e) => { g.Color = cc; SaveTabGroups(); RebuildTabStrip(); };
                colorItem.Items.Add(swatch);
            }
            menu.Items.Add(colorItem);

            var toggle = new MenuItem { Header = g.Collapsed ? L("S.m.grp.expand") : L("S.m.grp.collapse") };
            toggle.Click += (s, e) => { g.Collapsed = !g.Collapsed; SaveTabGroups(); RebuildTabStrip(); };
            menu.Items.Add(toggle);

            menu.Items.Add(new Separator());
            var ungroup = new MenuItem { Header = L("S.m.grp.ungroup") };
            ungroup.Click += (s, e) => Ungroup(g);
            menu.Items.Add(ungroup);
            return menu;
        }

        // Wstrzykuje na górę menu karty pozycje dot. grup (lista grup zmienia się w czasie — stąd przy otwarciu).
        private void PopulateTabGroupItems(ContextMenu menu, Session session)
        {
            for (int k = menu.Items.Count - 1; k >= 0; k--)
                if (menu.Items[k] is FrameworkElement fe && (fe.Tag as string) == GroupMenuMark)
                    menu.Items.RemoveAt(k);

            var inject = new List<Control>();
            var g = GroupOf(session);
            if (g == null)
            {
                var ng = new MenuItem { Header = L("S.m.newgroup"), Tag = GroupMenuMark };
                ng.Click += (s, e) => CreateGroupFromTab(session);
                inject.Add(ng);

                if (_tabGroups.Count > 0)
                {
                    var add = new MenuItem { Header = L("S.m.addtogroup"), Tag = GroupMenuMark };
                    foreach (var grp in _tabGroups)
                    {
                        var gg = grp;
                        var gi = new MenuItem
                        {
                            Header = grp.Name,
                            Icon = new Rectangle { Width = 10, Height = 10, RadiusX = 3, RadiusY = 3, Fill = new SolidColorBrush(grp.Color) }
                        };
                        gi.Click += (s, e) => AddToGroup(session, gg);
                        add.Items.Add(gi);
                    }
                    inject.Add(add);
                }
            }
            else
            {
                var rm = new MenuItem { Header = L("S.m.removefromgroup"), Tag = GroupMenuMark };
                rm.Click += (s, e) => RemoveFromGroup(session);
                inject.Add(rm);
            }

            inject.Add(new Separator { Tag = GroupMenuMark });
            for (int k = inject.Count - 1; k >= 0; k--) menu.Items.Insert(0, inject[k]);
        }

        /// <summary>Kropka statusu karty (szew wołany przez MainWindow z cyklu życia sesji).</summary>
        internal void SetTabStatus(Session s, ServerStatus status)
        {
            if (_tabStatus.TryGetValue(s, out var dot)) dot.Fill = _owner.StatusBrush(status);
        }

        /// <summary>Sprzątanie karty przy zamknięciu sesji (woła MainWindow.CloseSession): odłącz od paska
        /// i usuń wpisy elementów karty. Usunięcie z listy sesji i odbudowę paska robi wołający.</summary>
        internal void OnSessionClosed(Session s)
        {
            DetachTab(s);              // odłącz kartę od paska / kontenera grupy (grupa serwera zostaje)
            _tabUnderline.Remove(s);
            _tabStatus.Remove(s);
            _tabName.Remove(s);
            _tabClose.Remove(s);
        }
    }
}
