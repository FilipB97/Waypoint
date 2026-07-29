using System;
using System.Reflection;
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
    ///    fizyczne przez VisualTreeHelper.GetDpi.
    ///  - rdp.Connected to Int16 (VARIANT_BOOL): porównujemy == 1.
    ///  - wymiary parzyste, zakres [200..8192]; SmartSizing=false na happy-path,
    ///    włączany tylko jako fallback przy COMException (stare hosty).
    ///
    /// Ustawienia użytkownika (<see cref="AppSettings.RdpDesktopWidth"/> / <see cref="AppSettings.RdpScalePercent"/>,
    /// przeliczane przez <see cref="RdpDisplay"/>) modyfikują oba wymiary tego zachowania:
    ///  - stała rozdzielczość → nie mierzymy panelu, wysyłamy wybrany rozmiar i włączamy SmartSizing
    ///    (pulpit i tak wypełnia okno, kosztem skalowania);
    ///  - skala DPI → jedzie w ulDesktopScaleFactor, więc zdalny pulpit rysuje UI większe zamiast
    ///    upychać je w natywnych pikselach 4K (to ratunek na „nic nie da się odczytać").
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
        private readonly AppSettings _settings;
        private readonly DispatcherTimer _debounce;

        private int _lastW = -1, _lastH = -1;
        private int _retries;
        private bool _disposed;

        /// <param name="settings">Ustawienia aplikacji (rozdzielczość + skala DPI). Null = domyślne
        /// „dopasuj do okna" / „jak ekran lokalny" — czytamy je przy każdym użyciu, więc zmiana
        /// w Ustawieniach działa na żywo (patrz <see cref="ApplyDisplaySettings"/>).</param>
        public RdpDynamicResolution(Session session, WindowsFormsHost host, AppSettings settings)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _settings = settings;

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

        /// <summary>Stała rozdzielczość z ustawień, albo (0, 0) gdy „dopasuj do okna" (domyślnie).</summary>
        private (int W, int H) FixedRes()
        {
            if (_settings == null) return (0, 0);
            var (w, h) = RdpDisplay.Normalize(_settings.RdpDesktopWidth, _settings.RdpDesktopHeight);
            return (w, h);
        }

        /// <summary>Skala DPI dla serwera: wybór użytkownika, a przy „auto" mnożnik DPI okna (jak mstsc).</summary>
        private uint EffectiveScale()
        {
            double dpi = TryGetDpiScale(out var sx, out _) ? sx : 1.0;
            return (uint)RdpDisplay.EffectiveScale(_settings?.RdpScalePercent ?? 0, dpi);
        }

        /// <summary>Przy stałej rozdzielczości SmartSizing musi zostać włączony niezależnie od trybu podziału —
        /// inaczej wybrany pulpit siedziałby w letterboxie zamiast wypełniać okno.</summary>
        private bool WantSmartSizing(bool hasFixedRes) => _fit || hasFixedRes;

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
                bool hasFixed = FixedRes().W > 0;
                TrySetSmartSizing(_session.Rdp, WantSmartSizing(hasFixed));   // zastosuj od razu
                // Powrót do natywnej rozdzielczości (re-negocjacja) — przy stałej nie ma czego negocjować.
                if (!value && !hasFixed) Kick();
            }
        }

        /// <summary>
        /// Wołać PRZED <c>Connect()</c>: rozmiar pulpitu i skala DPI dla PIERWSZEJ klatki sesji. Bez tego
        /// wybrana rozdzielczość/skala pojawiałaby się dopiero po pierwszym Display-Update (po zalogowaniu),
        /// a sesja startowałaby z rozmiarem domyślnym kontrolki. Kasuje też cache ostatnich wymiarów —
        /// nowe połączenie nie ma jeszcze nic wynegocjowanego.
        /// </summary>
        public void ApplyPreConnect()
        {
            if (_disposed) return;
            var rdp = _session.Rdp;
            if (rdp == null) return;

            _lastW = _lastH = -1;
            _debounce.Stop();

            var fx = FixedRes();
            if (fx.W > 0)
            {
                // Tylko dla stałej rozdzielczości. W trybie „dopasuj do okna" zostawiamy domyślny rozmiar
                // kontrolki i renegocjujemy po zalogowaniu (ApplyInitial) — tak działało to do tej pory.
                try { rdp.DesktopWidth = fx.W; rdp.DesktopHeight = fx.H; }
                catch (COMException) { }
                catch (InvalidComObjectException) { }
            }

            TrySetSmartSizing(rdp, WantSmartSizing(fx.W > 0));
            TrySetPreConnectScale(rdp, EffectiveScale());
        }

        /// <summary>
        /// Ustawienia rozdzielczości/skali zmieniły się w trakcie sesji (zapis w Ustawieniach) — zastosuj bez
        /// ponownego łączenia. Cache <c>_lastW/_lastH</c> musi polecieć, inaczej ścieżka resize uznałaby,
        /// że nie ma czego zmieniać (wymiary te same, zmieniła się tylko skala albo tryb).
        /// </summary>
        public void ApplyDisplaySettings()
        {
            if (_disposed) return;
            _lastW = _lastH = -1;
            TrySetSmartSizing(_session.Rdp, WantSmartSizing(FixedRes().W > 0));
            Kick();
        }

        /// <summary>
        /// Ustawia rozdzielczość sesji DOKŁADNIE na podane piksele fizyczne, z pominięciem pomiaru
        /// hosta (DIP×DPI). Używane przy wejściu w pełny ekran, gdzie znamy natywny rozmiar monitora
        /// z GetMonitorInfo — bez tego przeliczanie DIP→piksele bywa błędne, gdy DPI monitora docelowego
        /// dochodzi z opóźnieniem (rozjazd skalowania po przeniesieniu na inny ekran).
        /// Przy stałej rozdzielczości z ustawień pełny ekran jej NIE zmienia — rozciąga ją SmartSizingiem.
        /// </summary>
        public void ApplyExact(int physW, int physH)
        {
            if (_disposed) return;

            var fx = FixedRes();
            if (fx.W > 0) { physW = fx.W; physH = fx.H; }

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
                UpdateDisplay(rdp, physW, physH);
                TrySetSmartSizing(rdp, WantSmartSizing(fx.W > 0));
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

            int w, h;
            var fx = FixedRes();
            if (fx.W > 0)
            {
                w = fx.W; h = fx.H;   // stała rozdzielczość: nie mierzymy panelu, tylko wysyłamy wybraną
            }
            else
            {
                if (!TryGetPhysicalPixels(out w, out h)) return;
                w = RdpUtils.NormalizeDim(w);
                h = RdpUtils.NormalizeDim(h);
            }
            if (w < MinDim || h < MinDim) return;
            if (w == _lastW && h == _lastH) return;

            try
            {
                // Piksele fizyczne "wbite" w rozdzielczość => serwer nie skaluje bitmapy; czytelność
                // reguluje osobno skala DPI (desktopScaleFactor), nie rozmycie.
                UpdateDisplay(rdp, w, h);
                TrySetSmartSizing(rdp, WantSmartSizing(fx.W > 0));   // natywny (ostry) albo dobicie do okna
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

        /// <summary>
        /// Jedno miejsce na Display-Update: wymiary + skala DPI z ustawień. Gdy host odrzuci skalę
        /// (starsze serwery znają tylko 100/100), ponawiamy bez niej — resize jest ważniejszy niż DPI,
        /// a wołający i tak potraktowałby COMException jako „kanał jeszcze nie wstał" i włączył SmartSizing.
        /// </summary>
        private void UpdateDisplay(AxMsRdpClient11NotSafeForScripting rdp, int w, int h)
        {
            uint scale = EffectiveScale();
            if (scale == 100u)
            {
                rdp.UpdateSessionDisplaySettings((uint)w, (uint)h, 0u, 0u, 0u, 100u, RdpDisplay.DeviceScaleFactor);
                return;
            }
            try
            {
                rdp.UpdateSessionDisplaySettings((uint)w, (uint)h, 0u, 0u, 0u, scale, RdpDisplay.DeviceScaleFactor);
            }
            catch (COMException)
            {
                rdp.UpdateSessionDisplaySettings((uint)w, (uint)h, 0u, 0u, 0u, 100u, RdpDisplay.DeviceScaleFactor);
            }
        }

        private bool TryGetPhysicalPixels(out int w, out int h)
        {
            w = h = 0;
            double dipW = _host.ActualWidth, dipH = _host.ActualHeight;
            if (dipW <= 0 || dipH <= 0) return false;
            if (!TryGetDpiScale(out double sx, out double sy)) return false;

            w = (int)Math.Round(dipW * sx);
            h = (int)Math.Round(dipH * sy);
            return true;
        }

        /// <summary>Mnożnik DPI okna (1.5 = 150%). Potrzebny i do przeliczenia DIP→piksele, i do skali
        /// „jak ekran lokalny".</summary>
        private bool TryGetDpiScale(out double sx, out double sy)
        {
            sx = sy = 0;
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
            return sx > 0 && sy > 0;
        }

        /// <summary>
        /// Skala DPI PRZED połączeniem — kontrolka czyta ją z <c>IMsRdpExtendedSettings.set_Property</c> pod
        /// tymi samymi nazwami, co klucze <c>desktopscalefactor</c>/<c>devicescalefactor</c> w plikach .rdp
        /// mstsc. Po zalogowaniu DPI zmienia już Display-Update; tu chodzi o pierwszą klatkę sesji.
        ///
        /// Wołamy późno-wiążąco (IDispatch), a nie przez rzutowanie na interfejs interopu: w wygenerowanym
        /// MSTSCLib para get_Property/set_Property wychodzi jako akcesory WŁAŚCIWOŚCI, a tych C# nie pozwala
        /// wywołać wprost (CS0571). Jedno wywołanie na połączenie, więc koszt refleksji bez znaczenia.
        /// </summary>
        private static void TrySetPreConnectScale(AxMsRdpClient11NotSafeForScripting rdp, uint scale)
        {
            try
            {
                object ocx = rdp.GetOcx();
                if (ocx == null) return;
                SetExtendedProperty(ocx, "DesktopScaleFactor", scale);
                SetExtendedProperty(ocx, "DeviceScaleFactor", RdpDisplay.DeviceScaleFactor);
            }
            catch (Exception) { /* kontrolka bez rozszerzonych ustawień — DPI dobije Display-Update */ }
        }

        private static void SetExtendedProperty(object ocx, string name, uint value)
            => ocx.GetType().InvokeMember("set_Property", BindingFlags.InvokeMethod, null, ocx,
                                          new object[] { name, value });

        private static void TrySetSmartSizing(AxMsRdpClient11NotSafeForScripting rdp, bool on)
        {
            if (rdp == null) return;
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
