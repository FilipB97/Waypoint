using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms.Integration;
using System.Windows.Media;
using System.Windows.Threading;
using AxMSTSCLib;
using RdpManager.Core;

namespace RdpManager
{
    /// <summary>
    /// Utrzymuje rozdzielczość sesji RDP równą fizycznemu rozmiarowi kontrolki
    /// (dynamic resolution / "Display Update"), żeby pełny ekran i zmiana rozmiaru
    /// dawały ostry obraz 1:1 zamiast rozmytego skalowania.
    ///
    /// Kluczowe (z adwersaryjnej weryfikacji):
    ///  - WindowsFormsHost raportuje rozmiar w DIP (96) — trzeba przeliczyć na piksele
    ///    fizyczne przez VisualTreeHelper.GetDpi i wtedy skala serwera = 100/100.
    ///  - rdp.Connected to Int16 (VARIANT_BOOL): porównujemy == 1.
    ///  - wymiary parzyste, zakres [200..8192]; SmartSizing=false na happy-path,
    ///    włączany tylko jako fallback przy COMException (stare hosty).
    /// </summary>
    public sealed class RdpDynamicResolution : IDisposable
    {
        private const int MinDim = RdpUtils.MinDim;

        // Debounce zdarzeń rozmiaru vs. odstęp ponowień po nieudanym UpdateSessionDisplaySettings
        // (wywołany za wcześnie po zalogowaniu rzuca COMException — patrz OnDebounceTick).
        private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);
        private const int MaxApplyRetries = 4;

        private readonly Session _session;
        private readonly WindowsFormsHost _host;
        private readonly DispatcherTimer _debounce;

        private int _lastW = -1, _lastH = -1;
        private int _retries;
        private bool _disposed;

        public RdpDynamicResolution(Session session, WindowsFormsHost host)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _host = host ?? throw new ArgumentNullException(nameof(host));

            _debounce = new DispatcherTimer(DispatcherPriority.Normal, _host.Dispatcher)
            {
                Interval = DebounceDelay
            };
            _debounce.Tick += OnDebounceTick;

            // Główny trigger: strona WPF — odpala się po arrange, z poprawnym ActualWidth + DPI.
            _host.SizeChanged += OnHostSizeChanged;
            if (_host.Child is System.Windows.Forms.Control child)
                child.Resize += OnChildResize;
        }

        /// <summary>Wołać z OnLoginComplete/OnConnected — pierwszy legalny moment na resize.</summary>
        public void ApplyInitial() => Kick();

        private bool _fit;
        /// <summary>„Dopasuj do okna": host skaluje pulpit do swojego rozmiaru (SmartSizing), więc pulpit zawsze
        /// się mieści — także gdy serwer nie renegocjuje rozdzielczości (wąski panel podziału ekranu). Gdy
        /// rozdzielczość i tak pasuje do panelu, SmartSizing nic nie skaluje → ostry render. Ustawiane przez
        /// UpdateCanvas wg trybu podziału (panele = true, pojedynczy widok = false).</summary>
        public bool FitToWindow
        {
            get => _fit;
            set
            {
                if (_fit == value) return;
                _fit = value;
                TrySetSmartSizing(_session.Rdp, value);   // zastosuj od razu
                if (!value) Kick();                        // powrót do natywnej rozdzielczości (re-negocjacja)
            }
        }

        /// <summary>
        /// Ustawia rozdzielczość sesji DOKŁADNIE na podane piksele fizyczne, z pominięciem pomiaru
        /// hosta (DIP×DPI). Używane przy wejściu w pełny ekran, gdzie znamy natywny rozmiar monitora
        /// z GetMonitorInfo — bez tego przeliczanie DIP→piksele bywa błędne, gdy DPI monitora docelowego
        /// dochodzi z opóźnieniem (rozjazd skalowania po przeniesieniu na inny ekran).
        /// </summary>
        public void ApplyExact(int physW, int physH)
        {
            if (_disposed) return;

            physW = RdpUtils.NormalizeDim(physW);
            physH = RdpUtils.NormalizeDim(physH);
            if (physW < MinDim || physH < MinDim) return;

            var rdp = _session.Rdp;
            if (rdp == null) return;

            bool live;
            try { live = _session.Connected && rdp.Connected == 1; }
            catch (InvalidComObjectException) { return; }
            catch (COMException) { return; }
            if (!live) return;

            _debounce.Stop();   // ubijemy ewentualny wyścig ze ścieżką SizeChanged
            try
            {
                rdp.UpdateSessionDisplaySettings((uint)physW, (uint)physH, 0u, 0u, 0u, 100u, 100u);
                TrySetSmartSizing(rdp, false);
                _lastW = physW; _lastH = physH;
            }
            catch (COMException) { TrySetSmartSizing(rdp, true); }
            catch (InvalidComObjectException) { }
        }

        private void OnHostSizeChanged(object sender, SizeChangedEventArgs e) => Kick();
        private void OnChildResize(object sender, EventArgs e) => Kick();

        private void Kick()
        {
            if (_disposed) return;
            _debounce.Stop();   // koalescencja serii zdarzeń (drag okna, wejście w pełny ekran)
            _retries = 0;                       // nowe zdarzenie rozmiaru = nowy cykl ponowień
            _debounce.Interval = DebounceDelay; // przywróć krótki debounce po ewentualnej serii ponowień
            _debounce.Start();
        }

        private void OnDebounceTick(object sender, EventArgs e)
        {
            _debounce.Stop();
            if (_disposed) return;

            var rdp = _session.Rdp;
            if (rdp == null) return;

            bool live;
            try { live = _session.Connected && rdp.Connected == 1; }
            catch (InvalidComObjectException) { return; }
            catch (COMException) { return; }
            if (!live) return;

            if (!TryGetPhysicalPixels(out int w, out int h)) return;

            w = RdpUtils.NormalizeDim(w);
            h = RdpUtils.NormalizeDim(h);
            if (w < MinDim || h < MinDim) return;
            if (w == _lastW && h == _lastH) return;

            try
            {
                // Piksele fizyczne "wbite" w rozdzielczość => serwer bez dodatkowego skalowania (100/100).
                rdp.UpdateSessionDisplaySettings((uint)w, (uint)h, 0u, 0u, 0u, 100u, 100u);
                TrySetSmartSizing(rdp, _fit);   // natywny (ostry); w „dopasuj do okna" SmartSizing dobija resztę
                _lastW = w; _lastH = h;
                _retries = 0;
            }
            catch (COMException)
            {
                // Stary host / brak kanału Display-Update / ZA WCZEŚNIE po zalogowaniu — degradacja:
                // rozciągnij (SmartSizing), żeby obraz od razu wypełniał kontrolkę. Ale nie zostawiamy
                // tego na stałe: tuż po OnLoginComplete (ApplyInitial, typowo autostart) wywołanie potrafi
                // rzucić E_FAIL, bo kanał Display-Update jeszcze nie wstał — a kolejna próba przychodziła
                // dopiero z ręcznym resize (stąd „szare pasy letterboxu aż do maksymalizacji okna").
                // Ponawiamy więc z odstępem do limitu; sukces przywraca ostry render bez SmartSizingu.
                TrySetSmartSizing(rdp, true);
                if (_retries < MaxApplyRetries)
                {
                    _retries++;
                    _debounce.Interval = RetryDelay;
                    _debounce.Start();
                }
            }
            catch (InvalidComObjectException)
            {
                // Kontrolka zniszczona między guardem a wywołaniem — nic nie robimy.
            }
        }

        private bool TryGetPhysicalPixels(out int w, out int h)
        {
            w = h = 0;
            double dipW = _host.ActualWidth, dipH = _host.ActualHeight;
            if (dipW <= 0 || dipH <= 0) return false;

            double sx, sy;
            try
            {
                DpiScale dpi = VisualTreeHelper.GetDpi(_host);
                sx = dpi.DpiScaleX; sy = dpi.DpiScaleY;
            }
            catch
            {
                var src = PresentationSource.FromVisual(_host);
                if (src?.CompositionTarget == null) return false;
                var m = src.CompositionTarget.TransformToDevice;
                sx = m.M11; sy = m.M22;
            }
            if (sx <= 0 || sy <= 0) return false;

            w = (int)Math.Round(dipW * sx);
            h = (int)Math.Round(dipH * sy);
            return true;
        }

        private static void TrySetSmartSizing(AxMsRdpClient11NotSafeForScripting rdp, bool on)
        {
            try { rdp.AdvancedSettings9.SmartSizing = on; }
            catch (COMException) { }
            catch (InvalidComObjectException) { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _debounce.Tick -= OnDebounceTick;
            _debounce.Stop();
            _host.SizeChanged -= OnHostSizeChanged;
            if (_host.Child is System.Windows.Forms.Control child)
                child.Resize -= OnChildResize;
        }
    }
}
