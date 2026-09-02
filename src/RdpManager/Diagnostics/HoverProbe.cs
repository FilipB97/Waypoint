using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace RdpManager.Diagnostics
{
    /// <summary>
    /// Sonda diagnostyczna podświetlenia ikon paska kart („po wyłączeniu i ponownym włączeniu trybu
    /// skupienia hover przestaje się malować, dopóki nie zmieni się rozmiar okna").
    ///
    /// Trzy hipotezy naprawcze (puls przerysowania bez bramki trybu, hover Setterem zamiast animacji,
    /// zdjęcie IsHitTestVisibleInChrome) nie usunęły objawu, a aplikacji WPF nie da się uruchomić w
    /// środowisku, w którym powstaje kod. Zamiast czwartej hipotezy — POMIAR: nakładka pokazuje na żywo
    /// stan wejścia (kto jest pod kursorem, kto ma przechwycenie myszy, co odpowiada WM_NCHITTEST) obok
    /// stanu malowania (jakie krycie ma warstwa hoveru i czy pętla renderu w ogóle tyka). Dwie rozłączne
    /// odpowiedzi:
    ///   • IsMouseOver=False mimo kursora nad przyciskiem  → problem WEJŚCIA (hit-test/przechwycenie),
    ///   • IsMouseOver=True i HoverFill.Opacity=1, a piksele bez zmian → problem MALOWANIA (kompozycja).
    /// Rozstrzyga to też wymuszenie (Ctrl+Shift+F11): ustawia krycie warstwy hoveru na 1 z pominięciem
    /// triggera. Widać podświetlenie → wejście; nie widać → malowanie.
    ///
    /// Skróty (tylko diagnostyka, bez wpływu na zwykłą pracę aplikacji):
    ///   Ctrl+Shift+F12 — pokaż/ukryj nakładkę,
    ///   Ctrl+Shift+F11 — wymuś/zdejmij krycie warstwy hoveru na wszystkich ikonach paska kart,
    ///   Ctrl+Shift+F10 — zrzuć bieżącą próbkę do pliku obok ustawień (hover-probe.log).
    /// </summary>
    internal sealed class HoverProbe
    {
        private readonly MainWindow _w;
        private Popup _popup;
        private TextBlock _text;
        private long _frames;
        private bool _forced;
        private string _last = "";

        private HoverProbe(MainWindow w) => _w = w;

        private static HoverProbe _inst;
        private static HoverProbe For(MainWindow w) => _inst ??= new HoverProbe(w);

        internal static void Toggle(MainWindow w) => For(w).ToggleOverlay();
        internal static void ToggleForcedHover(MainWindow w) => For(w).ToggleForced();
        internal static string Dump(MainWindow w) => For(w).WriteDump();

        // ---------- Nakładka ----------

        private void ToggleOverlay()
        {
            if (_popup != null && _popup.IsOpen) { Close(); return; }
            if (_popup == null) Build();
            _popup.IsOpen = true;
            MakeClickThrough();
            CompositionTarget.Rendering += OnFrame;
        }

        private void Close()
        {
            CompositionTarget.Rendering -= OnFrame;
            _popup.IsOpen = false;
        }

        private void Build()
        {
            _text = new TextBlock
            {
                FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
                FontSize = 11,
                LineHeight = 15,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE7, 0xE8, 0xEE)),
                TextWrapping = TextWrapping.NoWrap
            };
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x10, 0x12, 0x16)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x6C, 0x6D, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 12, 9),
                Child = _text
            };
            _popup = new Popup
            {
                Child = card,
                AllowsTransparency = true,
                StaysOpen = true,
                Focusable = false,
                Placement = PlacementMode.Relative,
                PlacementTarget = _w,
                HorizontalOffset = 12,
                VerticalOffset = 12,
                IsHitTestVisible = false
            };
        }

        // Nakładka nie może uczestniczyć w hit-teście, inaczej sama zaburza to, co mierzy
        // (WPF-owe IsHitTestVisible=false nie dotyczy OKNA popupu — potrzebny WS_EX_TRANSPARENT).
        private void MakeClickThrough()
        {
            var src = PresentationSource.FromVisual(_popup.Child) as HwndSource;
            if (src == null) return;
            int ex = GetWindowLong(src.Handle, GWL_EXSTYLE);
            SetWindowLong(src.Handle, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
        }

        private void OnFrame(object sender, EventArgs e)
        {
            _frames++;
            if ((_frames & 3) != 0) return;   // ~co czwarta klatka wystarczy, a nie obciąża renderu
            double want = Math.Max(12, _w.ActualHeight - 12 - _text.ActualHeight - 40);
            if (Math.Abs(_popup.VerticalOffset - want) > 2) _popup.VerticalOffset = want;
            _last = Sample();
            _text.Text = _last;
        }

        // ---------- Próbka ----------

        private string Sample()
        {
            var sb = new StringBuilder();
            sb.Append("SONDA HOVER  klatki=").Append(_frames)
              .Append(_forced ? "   [WYMUSZONE KRYCIE]" : "").AppendLine();

            sb.Append("tryb: skupienie=").Append(_w.IsImmersive())
              .Append("  pelnyEkran=").Append(_w._isFullscreen)
              .Append("  stanOkna=").Append(_w.WindowState)
              .Append("  peek=").Append(_w.IsFocusPeeking)
              .Append("  zoom=").Append(_w.RootScale.ScaleX.ToString("0.00", CultureInfo.InvariantCulture)).AppendLine();

            sb.Append("widocznosc: TitleBar=").Append(Short(_w.AppTitleBar.Visibility))
              .Append(" TabStripHost=").Append(Short(_w.TabStripHost.Visibility))
              .Append(" FocusControls=").Append(Short(_w.FocusControls.Visibility))
              .Append(" SessionActions=").Append(Short(_w.SessionActions.Visibility)).AppendLine();
            // Zwinięty pasek tytułu WPF-UI potrafi nadal zajmować strefę niekliencką nad paskiem kart
            // (patrz MainWindow.SetTitleBarChromeHitTest) — stąd ta linia obok WM_NCHITTEST poniżej.
            sb.Append("titleBar: IsHitTestVisible=").Append(_w.AppTitleBar.IsHitTestVisible).AppendLine();

            // --- wejscie ---
            GetCursorPos(out POINT sp);
            IntPtr hwndMain = new WindowInteropHelper(_w).Handle;
            IntPtr hwndAt = WindowFromPoint(sp);
            int ht = (int)SendMessage(hwndMain, WM_NCHITTEST, IntPtr.Zero, MakeLParam(sp.X, sp.Y));

            sb.Append("kursor(ekran)=").Append(sp.X).Append(',').Append(sp.Y)
              .Append("  HWND pod kursorem=").Append(Hex(hwndAt))
              .Append(hwndAt == hwndMain ? " (GLOWNE)" : " (INNE: " + ClassOf(hwndAt) + ")").AppendLine();
            sb.Append("WM_NCHITTEST(glowne)=").Append(ht).Append(' ').Append(HtName(ht)).AppendLine();

            sb.Append("Mouse.DirectlyOver=").Append(Describe(Mouse.DirectlyOver as DependencyObject)).AppendLine();
            sb.Append("Mouse.Captured=").Append(Mouse.Captured == null ? "(brak)" : Describe(Mouse.Captured as DependencyObject))
              .Append("  Captures=").Append(Mouse.Captured == null ? "-" : Mouse.Captured.GetType().Name).AppendLine();

            // Wlasny hit-test w drzewie wizualnym okna — niezalezny od routingu wejscia WPF.
            Point wp = ToWindow(sp);
            var hit = HitTestTop(wp);
            sb.Append("VisualTreeHelper.HitTest(").Append(wp.X.ToString("0", CultureInfo.InvariantCulture)).Append(',')
              .Append(wp.Y.ToString("0", CultureInfo.InvariantCulture)).Append(")=").Append(Describe(hit)).AppendLine();
            sb.Append("TabStripHost.IsMouseOver=").Append(_w.TabStripHost.IsMouseOver)
              .Append("  IsMouseDirectlyOver=").Append(_w.TabStripHost.IsMouseDirectlyOver).AppendLine();

            // --- malowanie: kazdy przycisk widocznego panelu akcji ---
            var panel = _w.FocusControls.Visibility == Visibility.Visible ? (Panel)_w.FocusControls
                      : _w.SessionActions.Visibility == Visibility.Visible ? _w.SessionActions : null;
            sb.AppendLine("--- ikony paska kart (" + (panel == null ? "brak widocznego panelu" : panel.Name) + ") ---");
            if (panel != null)
            {
                int i = 0;
                foreach (var b in panel.Children.OfType<Button>())
                {
                    string tag = Label(b, i++);
                    var fill = b.Template?.FindName("HoverFill", b) as Border;
                    string op = fill == null ? "brak szablonu"
                        : fill.Opacity.ToString("0.00", CultureInfo.InvariantCulture)
                          + " baza=" + ToD(fill.GetAnimationBaseValue(UIElement.OpacityProperty))
                          + " anim=" + (fill.GetAnimationBaseValue(UIElement.OpacityProperty) is double bv
                                        && Math.Abs(bv - fill.Opacity) > 0.001);
                    sb.Append("  ").Append(tag.PadRight(14))
                      .Append(" over=").Append(b.IsMouseOver ? "T" : "f")
                      .Append(" direct=").Append(b.IsMouseDirectlyOver ? "T" : "f")
                      .Append(" press=").Append(b.IsPressed ? "T" : "f")
                      .Append(" hit=").Append(b.IsHitTestVisible ? "T" : "f")
                      .Append(" vis=").Append(b.IsVisible ? "T" : "f")
                      .Append("  HoverFill.Opacity=").Append(op).AppendLine();
                }
            }
            sb.Append("skroty: Ctrl+Shift+F12 nakladka | F11 wymus krycie | F10 zrzut do pliku");
            return sb.ToString();
        }

        private static string ToD(object v) => v is double d ? d.ToString("0.00", CultureInfo.InvariantCulture) : "?";

        private string Label(Button b, int i)
        {
            if (!string.IsNullOrEmpty(b.Name)) return b.Name;
            var an = AutomationPropertiesName(b);
            return string.IsNullOrEmpty(an) ? "przycisk#" + i : an;
        }

        private static string AutomationPropertiesName(DependencyObject d)
        {
            try { return System.Windows.Automation.AutomationProperties.GetName(d); }
            catch { return null; }
        }

        // ---------- Wymuszenie krycia (rozstrzyga: wejscie czy malowanie) ----------

        private void ToggleForced()
        {
            _forced = !_forced;
            foreach (var b in IconButtons())
            {
                if (!(b.Template?.FindName("HoverFill", b) is Border fill)) continue;
                fill.BeginAnimation(UIElement.OpacityProperty, null);
                fill.Opacity = _forced ? 1.0 : 0.0;
            }
        }

        private IEnumerable<Button> IconButtons()
        {
            foreach (var b in _w.FocusControls.Children.OfType<Button>()) yield return b;
            foreach (var b in _w.SessionActions.Children.OfType<Button>()) yield return b;
        }

        // ---------- Zrzut do pliku ----------

        private string WriteDump()
        {
            string path = System.IO.Path.Combine(SettingsStore.Dir, "hover-probe.log");
            string body = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine
                        + (string.IsNullOrEmpty(_last) ? Sample() : _last) + Environment.NewLine
                        + new string('-', 78) + Environment.NewLine;
            try { System.IO.File.AppendAllText(path, body); return path; }
            catch (Exception ex) { return "BLAD: " + ex.Message; }
        }

        // ---------- Pomocnicze ----------

        private static string Short(Visibility v) => v == Visibility.Visible ? "V" : v == Visibility.Collapsed ? "C" : "H";

        private Point ToWindow(POINT screen)
        {
            try { return _w.PointFromScreen(new Point(screen.X, screen.Y)); }
            catch { return new Point(double.NaN, double.NaN); }
        }

        private DependencyObject HitTestTop(Point p)
        {
            if (double.IsNaN(p.X)) return null;
            DependencyObject found = null;
            try
            {
                VisualTreeHelper.HitTest(_w, null,
                    r => { found = r.VisualHit; return HitTestResultBehavior.Stop; },
                    new PointHitTestParameters(p));
            }
            catch { }
            return found;
        }

        private static string Describe(DependencyObject d)
        {
            if (d == null) return "(null)";
            var sb = new StringBuilder(d.GetType().Name);
            if (d is FrameworkElement fe && !string.IsNullOrEmpty(fe.Name)) sb.Append(" '").Append(fe.Name).Append('\'');
            // Najblizszy przodek-przycisk mowi, ktorej ikony dotyczy trafienie.
            DependencyObject cur = d;
            for (int i = 0; i < 12 && cur != null; i++)
            {
                if (cur is Button b) { sb.Append(" w Button"); if (!string.IsNullOrEmpty(b.Name)) sb.Append(" '").Append(b.Name).Append('\''); break; }
                cur = cur is Visual ? VisualTreeHelper.GetParent(cur) : null;
            }
            var src = d is Visual v ? PresentationSource.FromVisual(v) as HwndSource : null;
            if (src != null) sb.Append("  hwnd=").Append(Hex(src.Handle));
            return sb.ToString();
        }

        private static string Hex(IntPtr h) => "0x" + h.ToInt64().ToString("X");

        private static string ClassOf(IntPtr h)
        {
            var sb = new StringBuilder(160);
            return GetClassName(h, sb, sb.Capacity) > 0 ? sb.ToString() : "?";
        }

        private static string HtName(int ht)
        {
            switch (ht)
            {
                case 0: return "HTNOWHERE";
                case 1: return "HTCLIENT";
                case 2: return "HTCAPTION";
                case 3: return "HTSYSMENU";
                case 8: return "HTMINBUTTON";
                case 9: return "HTMAXBUTTON";
                case 10: return "HTLEFT";
                case 11: return "HTRIGHT";
                case 12: return "HTTOP";
                case 13: return "HTTOPLEFT";
                case 14: return "HTTOPRIGHT";
                case 15: return "HTBOTTOM";
                case 16: return "HTBOTTOMLEFT";
                case 17: return "HTBOTTOMRIGHT";
                case 20: return "HTCLOSE";
                default: return "HT" + ht;
            }
        }

        private static IntPtr MakeLParam(int x, int y) => (IntPtr)((y << 16) | (x & 0xFFFF));

        private const int WM_NCHITTEST = 0x0084;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder buf, int max);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
    }
}
