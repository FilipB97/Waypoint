using System;
using System.Globalization;

namespace RdpManager.Core
{
    /// <summary>
    /// Czyste przeliczenia rozdzielczości i skalowania (DPI) sesji RDP — bez WPF/ActiveX, więc testowalne.
    /// Na kontrolce stosuje je <c>RdpDynamicResolution</c>.
    ///
    /// Dwa niezależne pokrętła (Ustawienia → Połączenie → Domyślne RDP):
    ///  - ROZDZIELCZOŚĆ: 0×0 = „dopasuj do okna" (domyślne — sesja trzyma natywne piksele panelu, obraz 1:1),
    ///    wartość stała (np. 1600×900) = sesja negocjuje ten rozmiar, a SmartSizing rozciąga go do okna.
    ///  - SKALOWANIE: 0 = „jak ekran lokalny" (mnożnik DPI okna — tak robi mstsc), inaczej stałe procenty.
    ///    Dwa mechanizmy, bo protokół zna DPI tylko w zakresie 100..500%:
    ///     • &gt;= 100% → ulDesktopScaleFactor (UpdateSessionDisplaySettings) i „DesktopScaleFactor" przed
    ///       Connect, więc zdalny pulpit RYSUJE interfejs większy — bez utraty ostrości. Na ekranie 4K/HiDPI
    ///       sesja 1:1 przy 100% daje mikroskopijny tekst; to jest na to lekarstwo.
    ///     • &lt; 100% → DPI zostaje 100%, a żądamy PULPITU WIĘKSZEGO niż panel (<see cref="ResolutionMultiplier"/>)
    ///       i zmniejszamy obraz SmartSizingiem: więcej pulpitu, mniejszy UI, kosztem lekkiego rozmycia.
    /// </summary>
    public static class RdpDisplay
    {
        // Zakresy z dokumentacji IMsRdpClient9::UpdateSessionDisplaySettings: ulDesktopScaleFactor 100..500,
        // ulDeviceScaleFactor TYLKO 100/140/180 (inna wartość => E_INVALIDARG, cała zmiana odrzucona).
        // Device trzymamy na 100 („bez dodatkowego skalowania urządzenia") — DPI sesji ustala desktopScaleFactor.
        public const int MaxScale = 500;
        public const uint DeviceScaleFactor = 100u;

        /// <summary>Dół protokołu: DPI sesji nie zejdzie poniżej 100% (mniejsze wartości serwer IGNORUJE).</summary>
        public const int ProtocolMinScale = 100;

        /// <summary>Dół naszego suwaka. Poniżej 100% DPI nie wchodzi w grę, więc pomniejszenie robimy drugą
        /// drogą — patrz <see cref="ResolutionMultiplier"/>. Niżej niż 50% nie ma sensu (rozdzielczość rośnie
        /// dwukrotnie, tekst i tak nieczytelny).</summary>
        public const int MinScale = 50;

        /// <summary>Czy ustawienie opisuje STAŁĄ rozdzielczość (a nie „dopasuj do okna").</summary>
        public static bool IsFixed(int width, int height)
            => width >= RdpUtils.MinDim && height >= RdpUtils.MinDim;

        /// <summary>
        /// Sprowadza zapisaną parę do postaci używalnej przez kontrolkę: (0, 0) = „dopasuj do okna",
        /// inaczej wymiary znormalizowane jak przy Display-Update (parzyste, [200..8192]).
        /// Wartości poniżej minimum traktujemy jak brak wyboru — plik settings.json bywa edytowany ręcznie.
        /// </summary>
        public static (int Width, int Height) Normalize(int width, int height)
            => IsFixed(width, height)
                ? (RdpUtils.NormalizeDim(width), RdpUtils.NormalizeDim(height))
                : (0, 0);

        /// <summary>„1920x1080" (albo „1920×1080") → (1920, 1080); „", „Auto", śmieci → (0, 0) = dopasuj do okna.</summary>
        public static (int Width, int Height) ParseResolution(string tag)
        {
            string s = (tag ?? "").Trim();
            if (s.Length == 0) return (0, 0);

            int sep = s.IndexOfAny(new[] { 'x', 'X', '×' });
            if (sep <= 0 || sep == s.Length - 1) return (0, 0);

            if (!int.TryParse(s.Substring(0, sep).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)) return (0, 0);
            if (!int.TryParse(s.Substring(sep + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)) return (0, 0);
            return Normalize(w, h);
        }

        /// <summary>Odwrotność <see cref="ParseResolution"/> — Tag pozycji listy dla zapisanej wartości („" = auto).</summary>
        public static string FormatResolution(int width, int height)
        {
            var (w, h) = Normalize(width, height);
            return w == 0 ? "" : w.ToString(CultureInfo.InvariantCulture) + "x" + h.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Tag pozycji listy skalowania → wartość do zapisu: „" / „Auto" / śmieci = 0 („jak ekran lokalny").</summary>
        public static int ParseScale(string tag)
        {
            string s = (tag ?? "").Trim().TrimEnd('%').Trim();
            if (s.Length == 0) return 0;
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pct) || pct <= 0) return 0;
            return Math.Clamp(pct, MinScale, MaxScale);
        }

        /// <summary>
        /// Skala wybrana przez użytkownika. <paramref name="configured"/> &gt; 0 = jego wartość (przycięta do
        /// [50..500]); 0 = „jak ekran lokalny", czyli mnożnik DPI okna (1.5 → 150%). Zaokrąglamy do 5%, bo
        /// mnożnik z <c>VisualTreeHelper.GetDpi</c> bywa niecałkowity (np. 1.4999), a serwer i tak operuje
        /// na okrągłych krokach DPI. W trybie „auto" nie schodzimy poniżej 100% — ekran lokalny poniżej 100%
        /// to zwykle artefakt pomiaru, a nie życzenie użytkownika (pomniejszenie wybiera się świadomie).
        /// </summary>
        public static int EffectiveScale(int configured, double localDpiScale)
        {
            if (configured > 0) return Math.Clamp(configured, MinScale, MaxScale);
            if (double.IsNaN(localDpiScale) || double.IsInfinity(localDpiScale) || localDpiScale <= 0) return ProtocolMinScale;

            int pct = (int)Math.Round(localDpiScale * 100.0 / 5.0, MidpointRounding.AwayFromZero) * 5;
            return Math.Clamp(pct, ProtocolMinScale, MaxScale);
        }

        /// <summary>
        /// Wartość do <c>ulDesktopScaleFactor</c>: dla skali &gt;= 100% to wprost skala (serwer rysuje UI
        /// większe, obraz zostaje ostry). Poniżej 100% protokół nie pozwala zejść, więc DPI zostaje na 100%,
        /// a pomniejszenie realizuje <see cref="ResolutionMultiplier"/>.
        /// </summary>
        public static uint DesktopScaleFactor(int effectiveScale)
            => (uint)Math.Clamp(effectiveScale, ProtocolMinScale, MaxScale);

        /// <summary>
        /// Mnożnik ROZDZIELCZOŚCI dla skali poniżej 100%: żądamy pulpitu większego niż panel (100/75 = 1.33×)
        /// i zmniejszamy obraz SmartSizingiem. Skutek jak DPI &lt; 100% — więcej pulpitu, mniejszy interfejs —
        /// kosztem lekkiego rozmycia (skalowanie w dół wygląda znacznie lepiej niż w górę). Dla &gt;= 100%
        /// zwraca 1.0, bo tam pracuje już desktopScaleFactor i obraz ma zostać 1:1.
        /// </summary>
        public static double ResolutionMultiplier(int effectiveScale)
            => effectiveScale >= ProtocolMinScale || effectiveScale <= 0
                ? 1.0
                : ProtocolMinScale / (double)Math.Max(effectiveScale, MinScale);

        /// <summary>
        /// Nakłada <see cref="ResolutionMultiplier"/> na wymiary bazowe (piksele panelu albo stała
        /// rozdzielczość) i normalizuje wynik. Gdy po przemnożeniu któryś wymiar wychodzi za limit sesji
        /// (8192), przycinamy MNOŻNIK, nie pojedynczy wymiar — inaczej pulpit dostałby inne proporcje
        /// niż okno i obraz byłby rozciągnięty.
        /// </summary>
        /// <summary>
        /// Czy wolno teraz wysłać Display-Update (renegocjację rozdzielczości) do serwera.
        ///
        /// Każde „tak" kosztuje widoczne przemalowanie całej sesji, więc pytanie nie jest formalnością.
        /// Trzy powody, dla których odpowiedź brzmi „nie":
        ///  - okno jest ZMINIMALIZOWANE — nikt tego nie ogląda, a po przywróceniu i tak przyszłoby
        ///    zdarzenie rozmiaru z właściwymi wartościami; wysyłka teraz daje wyłącznie dwa przemalowania
        ///    na cykl minimalizuj/przywróć,
        ///  - wymiary są poniżej progu sensu (chwilowy pomiar w trakcie układania okna),
        ///  - wymiary są DOKŁADNIE takie, jakie serwer już zna — nie ma czego negocjować.
        /// </summary>
        /// <param name="minimized">Okno sesji jest zminimalizowane.</param>
        /// <param name="lastW">Ostatnio wynegocjowana szerokość; -1 = nic jeszcze nie wysłano.</param>
        public static bool ShouldApplyResize(bool minimized, int w, int h, int lastW, int lastH)
        {
            if (minimized) return false;
            if (w < RdpUtils.MinDim || h < RdpUtils.MinDim) return false;
            return w != lastW || h != lastH;
        }

        public static (int Width, int Height) ScaleResolution(int width, int height, int effectiveScale)
        {
            if (width < RdpUtils.MinDim || height < RdpUtils.MinDim) return (0, 0);

            double mult = ResolutionMultiplier(effectiveScale);
            if (mult > 1.0)
                mult = Math.Min(mult, Math.Min(RdpUtils.MaxDim / (double)width, RdpUtils.MaxDim / (double)height));
            if (mult <= 1.0) return (RdpUtils.NormalizeDim(width), RdpUtils.NormalizeDim(height));

            return (RdpUtils.NormalizeDim((int)Math.Round(width * mult)),
                    RdpUtils.NormalizeDim((int)Math.Round(height * mult)));
        }
    }
}
