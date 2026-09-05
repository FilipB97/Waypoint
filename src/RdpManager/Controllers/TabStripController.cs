using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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

        // Elementy karty per sesja (pasek akcentu aktywnej / kropka statusu / nazwa / ✕) — do odświeżania w miejscu.
        // Który to element (dolna kreska / górna / lewy pasek), decyduje styl paska kart — patrz Core/TabMetrics.
        private readonly Dictionary<Session, Rectangle> _tabMark = new Dictionary<Session, Rectangle>();
        // Pole znacznika stanu sesji (stały rozmiar) — kształt podmienia się przy zmianie stanu.
        private readonly Dictionary<Session, Grid> _tabStatus = new Dictionary<Session, Grid>();
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
        private InsertionAdorner _tabDropAdorner;   // linia „tu wyląduje karta" (warstwa adornerów)

        /// <summary>
        /// Paleta kolorów grup kart. Czytana Z PALETY, bo tablica literałów miała dwie wady naraz.
        ///
        /// Pierwszy wpis był dosłownie #7C6CFB — ten sam odcień, który usunęliśmy z próbnika akcentów
        /// i ze slotu 0 kolorów grup serwerów, bo dzieli go od akcentu #6C6DFF odległość barwna
        /// ΔE 4,1 (w jasnym motywie 12,8). Poprawka objęła wtedy dwa miejsca z trzech, a to trzecie
        /// przydzielało ten kolor PIERWSZEJ tworzonej grupie.
        ///
        /// Druga wada: tablica była statyczna, czyli niezależna od motywu — te same wartości siadały
        /// na jasnym panelu. Klucze palety mają oba warianty.
        ///
        /// Te same klucze niosą kolory grup SERWERÓW, więc grupa kart „Produkcja" i grupa serwerów
        /// „Produkcja" dostają wreszcie ten sam kolor.
        /// </summary>
        private IEnumerable<Color> GroupColors
            => Core.GroupPalette.Keys.Select(k => (_owner.TryFindResource(k) as SolidColorBrush)?.Color
                                          ?? Color.FromRgb(0xD0, 0x6B, 0xD8));
        private const string GroupMenuMark = "grp";   // znacznik pozycji menu karty wstrzykiwanych dla grup

        private static string L(string key) => LocalizationManager.S(key);

        public TabStripController(MainWindow owner) => _owner = owner;

        // Pasek kart ma DWA stopnie gęstości, nie trzy: „Dense" z listy serwerów zachowuje się tu
        // jak „Minimal", bo karta bez ikony protokołu przestałaby nieść cokolwiek poza nazwą.
        private bool IsMinimalList => _owner._settings != null && _owner._settings.ListStyle != "Default";

        /// <summary>Wymiary karty i paska dla bieżącego stylu, widoku i trybu — patrz Core/TabMetrics.</summary>
        private TabMetrics Metrics()
            => TabMetrics.For(TabMetrics.Parse(_owner._settings?.TabStyle),
                              IsMinimalList,
                              _owner.IsImmersive());

        // ---------- Pasek zakładek ----------

        internal FrameworkElement BuildTab(Session session)
        {
            var m = Metrics();
            bool minimal = m.HideAvatar;

            var tab = new Border
            {
                CornerRadius = new CornerRadius(m.Radius),
                BorderThickness = m.Border,
                // Blok: prawa krawędź JEST separatorem, więc widoczna od razu. Pozostałe style
                // trzymają przezroczysty obrys 1 px jako rezerwę miejsca dla wskaźnika „zgrupuj".
                BorderBrush = m.Mark == TabMark.Top ? _owner.Res("Border") : Brushes.Transparent,
                Background = Brushes.Transparent,
                Margin = m.Margin,
                Cursor = Cursors.Hand,
                Tag = session,
                ToolTip = session.Server.Name + " — " + MainWindow.DisplayHost(session.Server)
            };
            if (m.TabHeight > 0) tab.Height = m.TabHeight;

            // Korzeń karty: treść i paski akcentu leżą NA SOBIE (jedna komórka Grida). Dzięki temu
            // pasek górny i lewy siadają na krawędzi karty, poza jej wewnętrznym marginesem — czyli
            // tam, gdzie mają być, bez osobnej geometrii dla każdego stylu.
            var root = new Grid();

            var inner = new Grid
            {
                Margin = m.Padding,
                VerticalAlignment = m.TabHeight > 0 ? VerticalAlignment.Center : VerticalAlignment.Stretch
            };
            inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (m.ReserveBottom) inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            var tabDot = StatusGlyph.Host();
            // Znacznik odzwierciedla ŻYWY stan sesji, nie statyczny status serwera. Startowo „łączenie",
            // bo karta powstaje dokładnie w chwili, gdy sesja zaczyna się łączyć. Dotąd startowała ze
            // statusem serwera „Offline", co znaczyło „rozłączona", a wyglądało jak „serwer nie żyje".
            ApplyTabGlyph(tabDot, SessionState.Connecting);
            _tabStatus[session] = tabDot;

            if (minimal)
            {
                // Bez awatara: znacznik PRZED nazwą — niższa, lżejsza karta. Poza gęstością minimalną
                // dotyczy to trybu skupienia w stylu blokowym (patrz TabMetrics.HideAvatar).
                content.Children.Add(tabDot);
            }
            else
            {
                content.Children.Add(new Border
                {
                    // 14 px kafelek z inicjałami 7 px był nieczytelny — dwa wersaliki zlewały się w plamę.
                    Width = 17, Height = 17, CornerRadius = Radii.Xs,
                    Background = _owner.AvatarBrush(session.Server), VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = MainWindow.ServerInitials(session.Server),
                        Foreground = MainWindow.AvatarInk(_owner.AvatarBrush(session.Server)), FontSize = 9.5, FontWeight = FontWeights.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                    }
                });
            }

            var tabName = new TextBlock
            {
                Text = session.Server.Name, Foreground = _owner.Res("TextPrim"), FontSize = (double)_owner.TryFindResource("FontSmall"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(minimal ? 8 : 7, 0, 0, 0)
            };
            _tabName[session] = tabName;
            content.Children.Add(tabName);
            // Adres nie jest już na karcie (był w 3 miejscach naraz) — zostaje w pasku bocznym,
            // podpowiedzi karty i szybkim przełączaniu. Karta = ikona + nazwa + znacznik + ✕.
            if (!minimal) { tabDot.Margin = new Thickness(7, 0, 0, 0); content.Children.Add(tabDot); }
            content.Children.Add(BuildTabClose(session));
            Grid.SetRow(content, 0);
            inner.Children.Add(content);

            // Dolna kreska. W stylu „lewy znacznik" zostaje jako ROZPÓRKA (na stałe niewidoczna), żeby
            // przełączenie stylu nie zmieniało wysokości paska i nie przesuwało obszaru sesji.
            Rectangle bottom = null;
            if (m.ReserveBottom)
            {
                bottom = new Rectangle
                {
                    Height = m.MarkSize, Fill = _owner.Res("Accent"), RadiusX = 1, RadiusY = 1,
                    Margin = new Thickness(2, minimal ? 2 : 4, 2, 0),
                    Visibility = Visibility.Hidden
                };
                Grid.SetRow(bottom, 1);
                inner.Children.Add(bottom);
            }
            root.Children.Add(inner);

            // Pasek akcentu aktywnej karty — jeden element, trzy możliwe krawędzie.
            Rectangle mark = bottom;
            if (m.Mark == TabMark.Top)
            {
                mark = new Rectangle
                {
                    Height = m.MarkSize, Fill = _owner.Res("Accent"),
                    VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Stretch,
                    Visibility = Visibility.Hidden
                };
                root.Children.Add(mark);
            }
            else if (m.Mark == TabMark.Left)
            {
                mark = new Rectangle
                {
                    Width = m.MarkSize, Fill = _owner.Res("Accent"), RadiusX = 1.5, RadiusY = 1.5,
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Stretch,
                    Margin = new Thickness(4, minimal ? 4 : 6, 0, minimal ? 4 : 6),
                    Visibility = Visibility.Hidden
                };
                root.Children.Add(mark);
            }

            tab.Child = root;
            if (mark != null) _tabMark[session] = mark;
            WireTab(tab, session);
            return tab;
        }

        // ✕ karty (wspólny dla wszystkich stylów): pokazywany na aktywnej/hoverze (Hidden, nie Collapsed — stała szerokość).
        private TextBlock BuildTabClose(Session session)
        {
            var close = new TextBlock
            {
                Text = "✕", Foreground = _owner.Res("TextTer"), FontSize = (double)_owner.TryFindResource("FontCaption"),
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
            // Szukanie w buforze i zrzut transkryptu ma KAŻDY terminal (SSH, Telnet, Serial) — to funkcje
            // samego xterma, nie transportu. Rozgłaszanie zostaje przy SSH, bo tylko tam ma sens.
            if (session.IsTerm)
            {
                var findItem = new MenuItem { Header = L("S.m.find") };
                findItem.Click += (s, e) => session.Term?.OpenFind();
                tabMenu.Items.Add(findItem);

                var transcriptItem = new MenuItem { Header = L("S.m.transcript") };
                transcriptItem.Click += (s, e) => session.Term?.SaveTranscript();
                tabMenu.Items.Add(transcriptItem);

                var snippetsItem = new MenuItem { Header = L("S.m.snippets") };
                snippetsItem.Click += (s, e) => _owner.OpenSnippets();
                tabMenu.Items.Add(snippetsItem);

                if (session.IsSsh)
                {
                    var broadcastItem = new MenuItem { Header = L("S.m.broadcast") };
                    broadcastItem.Click += (s, e) => _owner.BroadcastToSsh();
                    tabMenu.Items.Add(broadcastItem);
                }
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
            var m = Metrics();
            bool block = m.Mark == TabMark.Top;
            var activeFill = _owner.Res(m.ActiveFill) ?? Brushes.Transparent;
            foreach (var s in _owner._sessions)
            {
                if (!(s.TabButton is Border b)) continue;
                bool active = s == _owner._active;
                // Lżej: aktywna = subtelne tło + akcent (underline), bez „pudełkowego" obrysu.
                b.Background = active ? activeFill : Brushes.Transparent;
                // W stylu blokowym prawa krawędź jest separatorem między kartami, a nie obrysem
                // zaznaczenia — wyczyszczenie jej tutaj skleiłoby karty w jedną plamę.
                b.BorderBrush = block ? (_owner.Res("Border") ?? Brushes.Transparent) : Brushes.Transparent;
                // Hierarchia: nieaktywne karty przygaszone (spokojniejszy pasek).
                if (_tabName.TryGetValue(s, out var nm))
                    nm.Foreground = _owner.Res(active ? "TextPrim" : "TextSec");
                if (_tabMark.TryGetValue(s, out var u) && u != null)
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
            var palette = GroupColors.ToList();
            foreach (var c in palette)
                if (!_tabGroups.Any(g => g.Color == c)) return c;
            return palette[_tabGroups.Count % palette.Count];
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
                catch { color = GroupColors.First(); }
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

        // Wysokość paska kart i rozmiar ikon sesji wg stylu (Domyślny / Blok / Lewy znacznik), widoku
        // (Domyślny / Minimalny) i trybu skupienia.
        //
        // Wymiary ISTNIEJĄCYCH kart są aktualizowane w miejscu, a nie przez odbudowę paska: wejście
        // w tryb skupienia zmienia wysokość karty blokowej, a odbudowa w tej samej chwili gubiłaby
        // trwające przeciąganie i mrugała paskiem dokładnie wtedy, gdy okno i tak się przelicza.
        internal void ApplyTabStripStyle()
        {
            var m = Metrics();

            _owner.TabStrip.Margin = new Thickness(m.StripPadH, m.StripPadV, m.StripPadH, m.StripPadV);

            // Kreska pod paskiem: w stylu zrastającym rysuje ją prostokąt LEŻĄCY POD kartami (żeby
            // aktywna mogła go zasłonić), w pozostałych — obramowanie hosta, jak dotąd.
            _owner.TabStripHost.BorderThickness = m.FuseWithContent ? new Thickness(0) : new Thickness(0, 0, 0, 1);
            _owner.TabStripUnderline.Visibility = m.FuseWithContent ? Visibility.Visible : Visibility.Collapsed;

            // Przyciski okna na pasku kart schodzą do wersji zwartej razem z kartą — inaczej to ONE,
            // a nie karta, wyznaczałyby wysokość paska i całe obniżenie w skupieniu byłoby pozorne.
            double btn = m.HideAvatar ? TabMetrics.FocusButtonMinimal : TabMetrics.FocusButton;
            foreach (var b in _owner.SessionActions.Children.OfType<Button>()) { b.Width = btn; b.Height = btn; }
            foreach (var b in _owner.FocusControls.Children.OfType<Button>()) { b.Width = btn; b.Height = btn; }

            foreach (var s in _owner._sessions)
            {
                if (!(s.TabButton is Border b)) continue;
                if (m.TabHeight > 0) b.Height = m.TabHeight;
                else b.ClearValue(FrameworkElement.HeightProperty);
            }
        }

        private void ShowTabDropIndicator(Border tab, bool group, bool after)
        {
            ClearTabDropIndicator();
            _tabDropTarget = tab;
            if (group)
            {
                // AccentSoftHover, nie AccentSoft: w stylu blokowym AccentSoft jest tłem karty AKTYWNEJ,
                // więc podpowiedź „zgrupuj" wyglądałaby jak „ta karta właśnie się uaktywniła".
                tab.Background = _owner.Res("AccentSoftHover") ?? _owner.Res("AccentSoft");
                tab.BorderBrush = _owner.Res("Accent");
                tab.BorderThickness = new Thickness(1);   // pełny obrys „zgrupuj" niezależnie od stylu
            }
            else
            {
                // Linia w WARSTWIE ADORNERÓW, nie obramowanie karty. BorderThickness zmienia grubość
                // ramki (karta ma w spoczynku 1 px), więc wskaźnik kolejności przesuwał treść karty
                // o 1–2 px dokładnie w chwili, gdy użytkownik celuje w nią myszą. Ten sam wzorzec
                // i ta sama klasa co w drzewie serwerów — tam działa tak od początku.
                var layer = AdornerLayer.GetAdornerLayer(tab);
                if (layer != null)
                {
                    _tabDropAdorner = new InsertionAdorner(tab, _owner.Res("Accent")) { Vertical = true, AtEnd = after };
                    layer.Add(_tabDropAdorner);
                }
            }
        }

        private void ClearTabDropIndicator()
        {
            if (_tabDropAdorner != null)
            {
                AdornerLayer.GetAdornerLayer(_tabDropAdorner.AdornedElement)?.Remove(_tabDropAdorner);
                _tabDropAdorner = null;
            }
            if (_tabDropTarget == null) return;
            _tabDropTarget.BorderThickness = Metrics().Border;   // grubość spoczynkowa wg stylu (w bloku: sam separator)
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

            // W stylu blokowym kontener grupy też traci zaokrąglenie i przylega do sąsiadów: zostają
            // boczne krawędzie w kolorze grupy, bo to one mówią, gdzie grupa się zaczyna i kończy.
            bool block = Metrics().Mark == TabMark.Top;
            var box = new Border
            {
                CornerRadius = block ? new CornerRadius(0) : Radii.Sm,
                Background = tint, BorderBrush = strong,
                BorderThickness = block ? new Thickness(1, 0, 1, 0) : new Thickness(1),
                Padding = block ? new Thickness(0, 0, 0, 0) : new Thickness(3, 0, 4, 0),
                Margin = block ? new Thickness(0) : new Thickness(0, 0, 5, 0)
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            box.Child = row;

            // Pastylka z nazwą: klik = zwiń/rozwiń; prawy klik = menu (nazwa / kolor / rozgrupuj).
            var pill = new Border
            {
                CornerRadius = block ? new CornerRadius(0) : Radii.Xs, Background = strong, Cursor = Cursors.Hand,
                Padding = new Thickness(6, IsMinimalList ? 1 : 2, 7, IsMinimalList ? 1 : 3),
                Margin = new Thickness(1, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
                ContextMenu = BuildGroupMenu(g)
            };
            var pillRow = new StackPanel { Orientation = Orientation.Horizontal };
            pillRow.Children.Add(new TextBlock
            {
                Text = g.Collapsed ? "▸" : "▾", Foreground = new SolidColorBrush(color), FontSize = (double)_owner.TryFindResource("FontCaption"),
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
                    CornerRadius = Radii.Sm, Background = _owner.Res("Elevated"),
                    Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = members.Count.ToString(), Foreground = _owner.Res("TextSec"), FontSize = (double)_owner.TryFindResource("FontCaption") }
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
                var swatch = new MenuItem { Header = new TextBlock { Text = "●", Foreground = new SolidColorBrush(cc), FontSize = (double)_owner.TryFindResource("FontBodyLg") } };
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
        internal void SetTabStatus(Session s, SessionState state)
        {
            if (_tabStatus.TryGetValue(s, out var dot)) ApplyTabGlyph(dot, state);
        }

        /// <summary>Wstawia kształt stanu sesji do pola karty; opisuje go też dla czytnika ekranu.</summary>
        private void ApplyTabGlyph(Grid host, SessionState state)
        {
            var (shape, key) = StatusGlyph.For(state);
            StatusGlyph.Set(host, shape, key == null ? null : _owner.Res(key));
            System.Windows.Automation.AutomationProperties.SetName(host, MainWindow.SessionStateLabel(state));
        }

        /// <summary>Sprzątanie karty przy zamknięciu sesji (woła MainWindow.CloseSession): odłącz od paska
        /// i usuń wpisy elementów karty. Usunięcie z listy sesji i odbudowę paska robi wołający.</summary>
        internal void OnSessionClosed(Session s)
        {
            DetachTab(s);              // odłącz kartę od paska / kontenera grupy (grupa serwera zostaje)
            _tabMark.Remove(s);
            _tabStatus.Remove(s);
            _tabName.Remove(s);
            _tabClose.Remove(s);
        }
    }
}
