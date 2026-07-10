using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using RdpManager.Core;

namespace RdpManager.Controllers
{
    /// <summary>
    /// Dwie powiązane maszyny stanu „ukrywania chrome": PEŁNY EKRAN (styl/stan/granice okna przez P/Invoke,
    /// pasek pełnoekranowy z auto-chowaniem + przypinaniem, rozdzielczość 1:1 na monitorze) oraz TRYB SKUPIENIA
    /// (peek panelu bocznego wysuwany z lewej krawędzi, puls przerysowania paska kart — obejście quirku WPF).
    /// Wyniesione 1:1 z MainWindow (PR 5 planu docs/REFACTOR-MAINWINDOW.md, wzorzec „back-reference move-method")
    /// — bez zmian logiki. Zgodnie z planem w MainWindow ZOSTAJĄ: deklaracje P/Invoke + struktury (poszerzone do
    /// internal, tu wołane jako <c>MainWindow.*</c>), <c>UpdateImmersive</c>/<c>IsImmersive</c> (rdzeń trybu
    /// skupienia, woła kontroler przez <c>_owner.</c>) oraz flaga <c>_isFullscreen</c> (czytana w wielu miejscach).
    /// Kontroler udostępnia szwy: Init/ApplyBarDelay/SetPeekPolling/HideFocusPeek/StartTabStripRepaintPulse/
    /// ToggleFullscreen/ToggleFocus/TogglePin/OnWindowRestored + właściwości FocusOverride/Peeking.
    /// </summary>
    internal sealed class FullscreenController
    {
        private readonly MainWindow _owner;

        // Pełny ekran: zapamiętany stan okna do przywrócenia po wyjściu.
        private WindowStyle _prevStyle;
        private WindowState _prevState;
        private ResizeMode _prevResize;
        private double _prevLeft, _prevTop, _prevWidth, _prevHeight;
        private bool _prevTopmost;
        private double _prevScale = 1.0;
        private bool _fsPinned;           // pasek pełnoekranowy „przypięty" (bez auto-chowania)
        private double _fsBarOffset;      // przesunięcie paska od środka (przeciąganie w poziomie)
        private MainWindow.RECT _fsMonRect;   // prostokąt monitora w pikselach fizycznych (do wykrycia górnej krawędzi)

        // Opóźnienie pojawienia się paska pełnoekranowego (jak w mstsc) + polling pozycji kursora.
        private DispatcherTimer _fsBarDelay;
        private DispatcherTimer _fsCursorPoll;
        private DispatcherTimer _focusPeekPoll;    // wykrywa najechanie na lewą krawędź w trybie skupienia
        private DispatcherTimer _focusPeekDelay;   // opóźnienie przytrzymania (jak pasek pełnoekranowy)
        private bool _focusPeeking;                // panel boczny chwilowo wysunięty w trybie skupienia
        private bool? _focusOverride;              // ręczne wł/wył skupienia (null = wg ustawienia); reset po un-maximize

        // Puls przerysowania paska kart w trybie skupienia (obejście quirku WPF — patrz StartTabStripRepaintPulse).
        private bool _tabPulseOn;
        private int _tabPulseCooldown;

        private static string L(string key) => LocalizationManager.S(key);

        public FullscreenController(MainWindow owner) => _owner = owner;

        /// <summary>Ręczny override skupienia (null = wg ustawienia) — czytany przez MainWindow.IsImmersive.</summary>
        internal bool? FocusOverride => _focusOverride;
        /// <summary>Czy panel boczny jest chwilowo wysunięty (peek) — czytane przez MainWindow.UpdateImmersive.</summary>
        internal bool Peeking => _focusPeeking;

        // ---------- Inicjalizacja timerów (z Window_Loaded) ----------

        internal void Init()
        {
            _owner.FsPopup.CustomPopupPlacementCallback = PlaceFsPopup;
            _fsBarDelay = new DispatcherTimer(DispatcherPriority.Normal, _owner.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(Math.Clamp(_owner._settings.FullscreenBarDelayMs, 0, 3000))
            };
            _fsBarDelay.Tick += (s, a) =>
            {
                _fsBarDelay.Stop();
                if (_owner._isFullscreen) _owner.FsPopup.IsOpen = true;
            };

            _fsCursorPoll = new DispatcherTimer(DispatcherPriority.Normal, _owner.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(90)
            };
            _fsCursorPoll.Tick += FsCursorPollTick;

            // Tryb skupienia: to samo opóźnienie „przytrzymania" co pasek pełnoekranowy.
            _focusPeekDelay = new DispatcherTimer(DispatcherPriority.Normal, _owner.Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(Math.Clamp(_owner._settings.FullscreenBarDelayMs, 0, 3000)) };
            _focusPeekDelay.Tick += (s, a) => { _focusPeekDelay.Stop(); ShowFocusPeek(); };
            _focusPeekPoll = new DispatcherTimer(DispatcherPriority.Normal, _owner.Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(90) };
            _focusPeekPoll.Tick += FocusPeekPollTick;
        }

        /// <summary>Po zapisie ustawień: przelicz opóźnienie paska/peeku (z Ustawień → FullscreenBarDelayMs).</summary>
        internal void ApplyBarDelay()
        {
            _fsBarDelay.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_owner._settings.FullscreenBarDelayMs, 0, 3000));
            if (_focusPeekDelay != null) _focusPeekDelay.Interval = _fsBarDelay.Interval;
        }

        /// <summary>Wł/wył polling krawędzi peeku wg trybu skupienia (woła MainWindow.UpdateImmersive).</summary>
        internal void SetPeekPolling(bool immersive)
        {
            if (immersive) { if (_focusPeekPoll != null && !_focusPeekPoll.IsEnabled) _focusPeekPoll.Start(); }
            else { _focusPeekPoll?.Stop(); _focusPeekDelay?.Stop(); }
        }

        /// <summary>Powrót do okna (un-maximize) kasuje ręczny override skupienia (woła Window_StateChanged).</summary>
        internal void OnWindowRestored() => _focusOverride = null;

        // ---------- Pasek u samej góry: pozycja (przeciąganie w poziomie) ----------

        private CustomPopupPlacement[] PlaceFsPopup(Size popupSize, Size targetSize, Point offset)
        {
            double free = Math.Max(0, targetSize.Width - popupSize.Width);
            double x = free / 2.0 + _fsBarOffset;
            if (x < 0) x = 0;
            if (x > free) x = free;
            return new[] { new CustomPopupPlacement(new Point(x, 0), PopupPrimaryAxis.None) };
        }

        internal void OnBarThumbDragDelta(DragDeltaEventArgs e)
        {
            _fsBarOffset += e.HorizontalChange;
            // Wymuś ponowne przeliczenie pozycji popupu (bez zmiany netto offsetu).
            if (_owner.FsPopup.IsOpen) { _owner.FsPopup.HorizontalOffset += 0.01; _owner.FsPopup.HorizontalOffset -= 0.01; }
        }

        // ---------- Tryb skupienia (peek panelu + puls paska kart) ----------

        // Podłoże peeku panelu w trybie skupienia: solidny kolor zbliżony do kanwy z kryciem z ustawień
        // (FocusPeekOpacity). Panele reparentowane do peeku mają prawie przezroczyste tło, więc bez tego
        // podłoża jasna sesja przebija spod panelu (Compass — czytelność w skupieniu).
        private Brush FocusPeekBackground()
        {
            int pct = Math.Clamp(_owner._settings.FocusPeekOpacity, 0, 100);
            byte a = (byte)Math.Round(pct * 255.0 / 100.0);
            var c = ThemeManager.IsLight
                ? System.Windows.Media.Color.FromArgb(a, 0xF2, 0xF3, 0xF5)
                : System.Windows.Media.Color.FromArgb(a, 0x0E, 0x0F, 0x14);
            return new SolidColorBrush(c);
        }

        private void ShowFocusPeek()
        {
            if (!_owner.IsImmersive() || _focusPeeking) return;
            if (MainWindow.GetForegroundWindow() != new WindowInteropHelper(_owner).Handle) return;   // tylko gdy Waypoint na wierzchu
            _focusPeeking = true;
            _owner.BodyGrid.Children.Remove(_owner.Rail);
            _owner.BodyGrid.Children.Remove(_owner.Sidebar);
            _owner.Rail.Visibility = Visibility.Visible;
            _owner.Sidebar.Visibility = Visibility.Visible;
            _owner.FocusPeekHost.Children.Add(_owner.Rail);
            _owner.FocusPeekHost.Children.Add(_owner.Sidebar);
            // Popup nie dziedziczy RootScale (osobny HWND) — nadaj mu ręcznie zoom UI, żeby peek miał tę
            // samą skalę co panel dokowany i mieścił się na ekranie (przy <100% był ucięty od dołu).
            _owner.FocusPeekScale.ScaleX = _owner.FocusPeekScale.ScaleY = _owner.RootScale.ScaleY;
            _owner.FocusPeekClip.Height = _owner.BodyGrid.ActualHeight;
            // Solidne podłoże pod prześwitującym panelem (Panel ~3%) — bez niego treść sesji przebija peek.
            _owner.FocusPeekClip.Background = FocusPeekBackground();
            _owner.FocusPeekPopup.IsOpen = true;

            var slide = new System.Windows.Media.Animation.DoubleAnimation(-280, 0,
                new Duration(TimeSpan.FromMilliseconds(160)))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            _owner.FocusPeekSlide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, slide);
        }

        internal void HideFocusPeek()
        {
            if (!_focusPeeking) return;
            _focusPeeking = false;
            _owner.FocusPeekSlide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            _owner.FocusPeekSlide.X = -280;
            _owner.FocusPeekPopup.IsOpen = false;
            _owner.FocusPeekHost.Children.Remove(_owner.Rail);
            _owner.FocusPeekHost.Children.Remove(_owner.Sidebar);
            // Wróć do layoutu (Grid.Column zachowane na elementach). W skupieniu ukryte, poza — widoczne.
            _owner.BodyGrid.Children.Add(_owner.Rail);
            _owner.BodyGrid.Children.Add(_owner.Sidebar);
            _owner.Rail.Visibility = _owner.IsImmersive() ? Visibility.Collapsed : Visibility.Visible;
            _owner.Sidebar.Visibility = (_owner.IsImmersive() || _owner._sidebarCollapsed) ? Visibility.Collapsed : Visibility.Visible;
        }

        // Polling kursora w trybie skupienia: najechanie na lewą krawędź (i przytrzymanie) wysuwa panel;
        // zjechanie na prawo od panelu go chowa. Airspace kontrolki sesji wyklucza zwykłe MouseEnter.
        private void FocusPeekPollTick(object sender, EventArgs e)
        {
            if (!_owner.IsImmersive()) { _focusPeekPoll.Stop(); _focusPeekDelay.Stop(); return; }
            if (_owner.WindowState == WindowState.Minimized || _owner.QuickSwitchPopup.IsOpen) return;

            // Wysuwaj panel TYLKO gdy Waypoint jest na pierwszym planie — inaczej najechanie na lewą krawędź
            // ekranu pokazywało listę serwerów nad inną aplikacją (np. przeglądarką), gdy okno było w tle.
            if (MainWindow.GetForegroundWindow() != new WindowInteropHelper(_owner).Handle)
            {
                _focusPeekDelay.Stop();
                if (_focusPeeking) HideFocusPeek();
                return;
            }

            if (!MainWindow.GetCursorPos(out MainWindow.POINT p)) return;

            // Lewa krawędź LICZONA Z PROSTOKĄTA MONITORA (jak pasek pełnoekranowy), nie z PointToScreen(0,0):
            // zmaksymalizowane okno wystaje ~8px poza monitor, więc PointToScreen dawało ujemny lewy brzeg
            // i próg poza ekranem — trigger nigdy się nie odpalał.
            IntPtr mon = MainWindow.MonitorFromWindow(new WindowInteropHelper(_owner).Handle, MainWindow.MONITOR_DEFAULTTONEAREST);
            var mi = new MainWindow.MONITORINFO { cbSize = Marshal.SizeOf(typeof(MainWindow.MONITORINFO)) };
            if (!MainWindow.GetMonitorInfo(mon, ref mi)) return;
            var r = mi.rcMonitor;

            if (!_focusPeeking)
            {
                bool withinY = p.Y >= r.top && p.Y < r.bottom;
                if (withinY && p.X <= r.left + 3) { if (!_focusPeekDelay.IsEnabled) _focusPeekDelay.Start(); }
                else if (_focusPeekDelay.IsEnabled) _focusPeekDelay.Stop();
            }
            else
            {
                // PointToScreen uwzględnia DPI i zoom UI (Sidebar jest pod RootScale).
                double sbRight = _owner.Sidebar.PointToScreen(new Point(_owner.Sidebar.ActualWidth, 0)).X;
                if (p.X > sbRight + 8) HideFocusPeek();
            }
        }

        // Obejście quirku WPF: w trybie skupienia (pasek kart pod LayoutTransform, obok airspace WindowsFormsHost)
        // zmiana tła podświetlenia ikon (IsMouseOver) jest USTAWIANA, ale WPF jej nie MALUJE — dopóki pętla renderu
        // nie zostanie „obudzona" (robił to dopiero realny resize okna). Gdy mysz jest nad paskiem kart, trzymamy
        // pętlę renderu aktywną (CompositionTarget.Rendering) i znaczymy przyciski „brudne", więc hover maluje się
        // od razu. Po zejściu myszy dogaszamy kilka klatek i odpinamy — brak stałego kosztu renderowania.
        internal void StartTabStripRepaintPulse()
        {
            if (!_owner.IsImmersive()) return;
            _tabPulseCooldown = 15;
            if (_tabPulseOn) return;
            _tabPulseOn = true;
            System.Windows.Media.CompositionTarget.Rendering += TabStripRepaintPulse;
        }

        private void TabStripRepaintPulse(object sender, EventArgs e)
        {
            if (_owner.TabStripHost.IsMouseOver && _owner.IsImmersive())
            {
                _tabPulseCooldown = 15;
                foreach (System.Windows.UIElement c in _owner.FocusControls.Children) c.InvalidateVisual();
            }
            else if (--_tabPulseCooldown <= 0)
            {
                System.Windows.Media.CompositionTarget.Rendering -= TabStripRepaintPulse;
                _tabPulseOn = false;
            }
        }

        // Przełącznik trybu skupienia (przycisk na pasku): wł/wył dla bieżącego zmaksymalizowanego okna.
        internal void ToggleFocus()
        {
            if (_owner.IsImmersive()) { _focusOverride = false; }        // wyłącz — chrome wraca (okno zostaje zmaksymalizowane)
            else
            {
                _focusOverride = true;                             // włącz — schowaj chrome
                if (_owner.WindowState != WindowState.Maximized) _owner.WindowState = WindowState.Maximized;   // by miało efekt
            }
            _owner.UpdateImmersive();
        }

        // ---------- Pełny ekran ----------

        internal void ToggleFullscreen()
        {
            if (_owner._active == null && !_owner._isFullscreen) return;
            if (!_owner._isFullscreen) EnterFullscreen();
            else ExitFullscreen();
        }

        private void EnterFullscreen()
        {
            _prevStyle = _owner.WindowStyle;
            _prevState = _owner.WindowState;
            _prevResize = _owner.ResizeMode;
            _prevTopmost = _owner.Topmost;
            _prevLeft = _owner.Left; _prevTop = _owner.Top; _prevWidth = _owner.Width; _prevHeight = _owner.Height;
            _prevScale = _owner.RootScale.ScaleX;
            _owner.RootScale.ScaleX = _owner.RootScale.ScaleY = 1.0;   // zdalny pulpit ostro 1:1 w pełnym ekranie
            _owner._isFullscreen = true;   // wcześnie: StateChanged w trakcie przełączania nie ruszy trybu skupienia

            _owner.AppTitleBar.Visibility = Visibility.Collapsed;
            _owner.Rail.Visibility = Visibility.Collapsed;
            _owner.Sidebar.Visibility = Visibility.Collapsed;
            _owner.TabStripHost.Visibility = Visibility.Collapsed;
            _owner.SessionToolbar.Visibility = Visibility.Collapsed;
            _owner.SessionHotZoneRow.Height = new GridLength(0);   // host wypełnia CAŁY monitor → rozdzielczość 1:1

            _owner.WindowState = WindowState.Normal;   // trzeba być Normal, żeby ręcznie ustawić granice
            _owner.WindowStyle = WindowStyle.None;
            _owner.ResizeMode = ResizeMode.NoResize;

            // Pełny ekran na monitorze, na którym stoi okno (na inny ekran = osobne okno + przeciągnięcie).
            // SetWindowPos jest poprawny także między monitorami o różnym DPI; zakrywa pasek zadań.
            var hwnd = new WindowInteropHelper(_owner).Handle;
            var screen = System.Windows.Forms.Screen.FromHandle(hwnd);
            var b = screen.Bounds;
            MainWindow.SetWindowPos(hwnd, IntPtr.Zero, b.Left, b.Top, b.Width, b.Height, MainWindow.SWP_SHOWWINDOW);
            _owner.Topmost = true;

            _fsCursorPoll.Start();

            // Rozdzielczość dokładnie = natywne piksele monitora (jak w oknie sesji) — deterministycznie, bez wyścigu DPI.
            IntPtr mon = MainWindow.MonitorFromWindow(hwnd, MainWindow.MONITOR_DEFAULTTONEAREST);
            var mi = new MainWindow.MONITORINFO { cbSize = Marshal.SizeOf(typeof(MainWindow.MONITORINFO)) };
            if (MainWindow.GetMonitorInfo(mon, ref mi))
            {
                _fsMonRect = mi.rcMonitor;
                int pw = mi.rcMonitor.right - mi.rcMonitor.left, ph = mi.rcMonitor.bottom - mi.rcMonitor.top;
                var sess = _owner._active;
                _owner.Dispatcher.BeginInvoke(new Action(() => { if (_owner._isFullscreen) sess?.Resizer?.ApplyExact(pw, ph); }), DispatcherPriority.Background);
            }
            else
            {
                _fsMonRect = new MainWindow.RECT { left = b.Left, top = b.Top, right = b.Right, bottom = b.Bottom };
            }
        }

        private void ExitFullscreen()
        {
            _fsCursorPoll.Stop();
            _fsBarDelay.Stop();
            _owner.RootScale.ScaleX = _owner.RootScale.ScaleY = _prevScale;
            _owner.Topmost = _prevTopmost;
            _owner.WindowStyle = _prevStyle;
            _owner.ResizeMode = _prevResize;
            _owner.Left = _prevLeft; _owner.Top = _prevTop; _owner.Width = _prevWidth; _owner.Height = _prevHeight;
            _owner.WindowState = _prevState;

            _owner.AppTitleBar.Visibility = Visibility.Visible;
            _owner.Rail.Visibility = Visibility.Visible;
            _owner.Sidebar.Visibility = _owner._sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;   // respektuj ręczne zwinięcie
            _owner.TabStripHost.Visibility = Visibility.Visible;
            _owner.SessionToolbar.Visibility = Visibility.Visible;
            _owner.SessionHotZoneRow.Height = new GridLength(0);   // brak odstępu pod paskiem kart (kotwica popupu ma 0)

            _owner.FsPopup.IsOpen = false;
            _owner._isFullscreen = false;
            _fsPinned = false;
            _owner.PinBtn.Content = L("S.fs.pin");
            _owner.UpdateImmersive();
        }

        /// <summary>
        /// Prostokąt pełnego ekranu w DIP: bieżący monitor albo — przy span — cały wirtualny
        /// pulpit (wszystkie monitory). Zapisuje też prostokąt w pikselach do pollingu krawędzi.
        /// </summary>
        private bool TryGetFullscreenRectDip(bool allMonitors, out Rect rect)
        {
            rect = default;
            IntPtr hwnd = new WindowInteropHelper(_owner).Handle;
            if (hwnd == IntPtr.Zero) return false;

            MainWindow.RECT r;
            if (allMonitors)
            {
                r.left = MainWindow.GetSystemMetrics(MainWindow.SM_XVIRTUALSCREEN);
                r.top = MainWindow.GetSystemMetrics(MainWindow.SM_YVIRTUALSCREEN);
                r.right = r.left + MainWindow.GetSystemMetrics(MainWindow.SM_CXVIRTUALSCREEN);
                r.bottom = r.top + MainWindow.GetSystemMetrics(MainWindow.SM_CYVIRTUALSCREEN);
                if (r.right <= r.left || r.bottom <= r.top) return false;
            }
            else
            {
                IntPtr mon = MainWindow.MonitorFromWindow(hwnd, MainWindow.MONITOR_DEFAULTTONEAREST);
                var mi = new MainWindow.MONITORINFO { cbSize = Marshal.SizeOf(typeof(MainWindow.MONITORINFO)) };
                if (!MainWindow.GetMonitorInfo(mon, ref mi)) return false;
                r = mi.rcMonitor;
            }
            _fsMonRect = r;   // piksele — do pollingu górnej krawędzi (działa też dla span)

            var src = PresentationSource.FromVisual(_owner);
            if (src?.CompositionTarget == null) return false;
            Matrix toDip = src.CompositionTarget.TransformFromDevice;

            Point tl = toDip.Transform(new Point(r.left, r.top));
            Point br = toDip.Transform(new Point(r.right, r.bottom));
            rect = new Rect(tl, br);
            return true;
        }

        /// <summary>Liczba monitorów w systemie (bramkuje ścieżkę multimon).</summary>
        private static int MonitorCount()
        {
            try { return System.Windows.Forms.Screen.AllScreens.Length; }
            catch { return 1; }
        }

        // Polling kursora w pełnym ekranie: łapie SAMĄ górną krawędź (y=0), niezależnie od
        // nibeklienckiego brzegu okna i od "airspace" kontrolki RDP.
        private void FsCursorPollTick(object sender, EventArgs e)
        {
            if (!_owner._isFullscreen) { _fsCursorPoll.Stop(); return; }
            if (_owner.WindowState == WindowState.Minimized) return;   // zminimalizowane — nie pokazuj paska
            if (_fsPinned) { if (!_owner.FsPopup.IsOpen) _owner.FsPopup.IsOpen = true; return; }   // przypięty: zawsze widoczny
            if (!MainWindow.GetCursorPos(out MainWindow.POINT p)) return;

            bool withinX = p.X >= _fsMonRect.left && p.X < _fsMonRect.right;
            bool atTop = withinX && p.Y <= _fsMonRect.top + 2;

            if (atTop)
            {
                // Nie od razu — dopiero gdy kursor chwilę postoi przy krawędzi (jak w mstsc).
                if (!_owner.FsPopup.IsOpen && !_fsBarDelay.IsEnabled) _fsBarDelay.Start();
            }
            else
            {
                if (_fsBarDelay.IsEnabled) _fsBarDelay.Stop();
                // zabezpieczenie: gdy kursor zjedzie wyraźnie poniżej paska (a flyout zwinięty) — zamknij
                if (_owner.FsPopup.IsOpen && _owner.FsFlyout.Visibility != Visibility.Visible && p.Y > _fsMonRect.top + 140)
                    _owner.FsPopup.IsOpen = false;
            }
        }

        internal void OnFsPopupMouseLeave()
        {
            if (_fsPinned) return;   // przypięty pasek nie chowa się po zjechaniu myszą
            _owner.FsPopup.IsOpen = false;
            _owner.CollapseFlyout();
        }

        internal void TogglePin()
        {
            _fsPinned = !_fsPinned;
            _owner.PinBtn.Content = _fsPinned ? L("S.fs.pinned") : L("S.fs.pin");
            if (_fsPinned) _owner.FsPopup.IsOpen = true;   // przypięty pasek pozostaje widoczny
        }
    }
}
