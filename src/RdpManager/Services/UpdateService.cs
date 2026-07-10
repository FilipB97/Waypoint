using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace RdpManager.Services
{
    /// <summary>
    /// Aktualizacje aplikacji + karta „O aplikacji": ciche/ręczne sprawdzanie wydań GitHub, pobieranie
    /// z ponawianiem, weryfikacja wydawcy (Authenticode) i restart przez „--apply-update", oraz render
    /// karty aktualizacji i historii zmian w Ustawieniach. Wyniesione 1:1 z MainWindow (PR 1 planu
    /// docs/REFACTOR-MAINWINDOW.md, wzorzec „back-reference move-method") — bez zmian logiki; handlery
    /// z XAML zostają w MainWindow jako 1-linijkowe shimy.
    /// </summary>
    internal sealed class UpdateService
    {
        private readonly MainWindow _owner;

        private DispatcherTimer _updateTimer;   // cykliczne sprawdzanie aktualizacji (co 6 h)
        private bool _updateChecking;           // trwa sprawdzanie — blokuje nakładające się żądania (timer + przycisk)
        private Core.UpdateCheck.ReleaseInfo _update;      // dostępna nowsza wersja (z URL assetu .exe); null gdy brak
        private bool _updating;                            // trwa auto-aktualizacja — pomiń potwierdzenie zamknięcia
        private readonly string _prevRunVersion;           // wersja z poprzedniego startu (do wykrycia „właśnie zaktualizowano")

        private static string L(string key) => LocalizationManager.S(key);

        /// <param name="prevRunVersion">Wersja z poprzedniego startu — Window_Loaded łapie ją z ustawień,
        /// ZANIM nadpisze LastRunVersion bieżącą wersją.</param>
        public UpdateService(MainWindow owner, string prevRunVersion)
        {
            _owner = owner;
            _prevRunVersion = prevRunVersion ?? "";
        }

        /// <summary>Trwa auto-aktualizacja — Window_Closing pomija potwierdzenie zamknięcia.</summary>
        internal bool IsUpdating => _updating;

        /// <summary>Start z Window_Loaded: ciche sprawdzenie przy starcie + cykliczny timer.</summary>
        internal void Start()
        {
            CheckForUpdatesAsync();
            // Cyklicznie w tle (co 6 h), nie tylko przy starcie — długo działające okno też złapie nowe wydanie.
            _updateTimer = new DispatcherTimer(DispatcherPriority.Background, _owner.Dispatcher) { Interval = TimeSpan.FromHours(6) };
            _updateTimer.Tick += (s, a) => CheckForUpdatesAsync();
            if (_owner._settings.CheckUpdates) _updateTimer.Start();
        }

        /// <summary>Po zapisie ustawień: cykliczne sprawdzanie wg tego samego przełącznika co start.</summary>
        internal void ApplyCheckUpdatesSetting()
        {
            if (_updateTimer != null) { if (_owner._settings.CheckUpdates) _updateTimer.Start(); else _updateTimer.Stop(); }
        }

        /// <summary>Karta aktualizacji + historia zmian przy wejściu w Ustawienia (LoadSettingsForm).</summary>
        internal void RefreshAboutCard()
        {
            if (_update == null) SetAboutUpToDate(L("S.update.uptodate"));    // stan domyślny karty aktualizacji
            else ShowAboutUpdateAvailable(_update.Version, _update.ExeSize);  // gdy sprawdzenie w tle już coś znalazło
            BuildChangelog();       // od razu fallback z Changelog.cs
            LoadChangelogAsync();   // w tle podmień na realne wydania z GitHuba
        }

        // ---------- Sprawdzanie i instalacja ----------

        internal static Version CurrentVersion()
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }

        // Automatyczne sprawdzenie (start aplikacji + cykliczny timer). Bramkowane ustawieniem, bez UI błędów.
        // Bez sieci / rate limitu / złego JSON-a — po prostu nic się nie pokazuje.
        private async void CheckForUpdatesAsync()
        {
            if (!_owner._settings.CheckUpdates || _updateChecking) return;   // bez nakładających się sprawdzeń
            _updateChecking = true;
            try
            {
                var info = await FetchLatestReleaseAsync();
                if (info == null) return;
                var current = CurrentVersion();
                if (Core.UpdateCheck.IsNewer(info.Version, current))
                {
                    _update = info;
                    _owner.UpdateBtn.Content = string.Format(L("S.update.available"), info.Version);
                    _owner.UpdateBtn.Visibility = Visibility.Visible;
                    ShowAboutUpdateAvailable(info.Version, info.ExeSize);
                }
                else if (Core.UpdateCheck.ParseTag(_prevRunVersion) is Version prev && prev < current
                         && !string.IsNullOrWhiteSpace(info.Notes))
                {
                    // Wersja wzrosła od ostatniego startu → właśnie zaktualizowano: pokaż „co nowego" (raz).
                    new ReleaseNotesWindow(string.Format(L("S.update.whatsnew"), current),
                        current, info.Notes, info.HtmlUrl, confirm: false) { Owner = _owner }.ShowDialog();
                }
            }
            catch { /* offline / proxy / rate limit — sprawdzimy przy kolejnym cyklu */ }
            finally { _updateChecking = false; }
        }

        // Pobiera najnowsze wydanie z GitHuba (bez UI/bramek). Rzuca przy błędzie sieci; null gdy brak/parsowanie.
        private async Task<Core.UpdateCheck.ReleaseInfo> FetchLatestReleaseAsync()
        {
            using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(6) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Waypoint");
                string json = await http.GetStringAsync(
                    "https://api.github.com/repos/FilipB97/Waypoint/releases/latest");
                return Core.UpdateCheck.ParseRelease(json);
            }
        }

        // Ręczne sprawdzenie z Ustawień — bez bramki CheckUpdates, z informacją zwrotną w etykiecie statusu.
        internal async void CheckUpdatesNow_Click(object sender, RoutedEventArgs e)
        {
            if (_updateChecking) return;
            _updateChecking = true;
            if (_owner.AboutUpdateTitle != null) _owner.AboutUpdateTitle.Text = L("S.update.checking");   // transient stan w karcie
            try
            {
                var info = await FetchLatestReleaseAsync();
                var current = CurrentVersion();
                if (info == null)   // pobrano, ale nie dało się odczytać wydania → to NIE „aktualne", tylko błąd
                {
                    SetAboutUpToDate(L("S.update.checkfailed"), error: true);
                }
                else if (Core.UpdateCheck.IsNewer(info.Version, current))
                {
                    _update = info;
                    _owner.UpdateBtn.Content = string.Format(L("S.update.available"), info.Version);
                    _owner.UpdateBtn.Visibility = Visibility.Visible;
                    ShowAboutUpdateAvailable(info.Version, info.ExeSize);
                }
                else
                {
                    SetAboutUpToDate(L("S.update.uptodate"));
                }
            }
            catch
            {
                SetAboutUpToDate(L("S.update.checkfailed"), error: true);
            }
            finally { _updateChecking = false; }
        }

        // Dozwolone hosty pobrania aktualizacji: GitHub i jego CDN assetów. Adres bierzemy z JSON-a wydania,
        // więc przed pobraniem i uruchomieniem pliku wymuszamy https + zaufany host. Przy niepodpisanym
        // buildzie werdykt CodeSign to CurrentUnsigned („akceptowalny"), więc to jedyna twarda kontrola
        // POCHODZENIA pliku — bez niej podmieniony adres (np. http:// albo obcy host) zostałby pobrany.
        internal static bool IsTrustedDownloadUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
            if (u.Scheme != Uri.UriSchemeHttps) return false;
            string h = u.Host.ToLowerInvariant();
            return h == "github.com" || h.EndsWith(".github.com", StringComparison.Ordinal)
                || h == "githubusercontent.com" || h.EndsWith(".githubusercontent.com", StringComparison.Ordinal);
        }

        internal async void Update_Click(object sender, RoutedEventArgs e)
        {
            // Brak assetu .exe w release, albo adres spoza zaufanych hostów GitHuba → nie pobieramy w apce;
            // otwórz stronę wydania w przeglądarce (użytkownik pobierze ręcznie z zaufanego źródła).
            if (_update == null || string.IsNullOrEmpty(_update.ExeUrl) || !IsTrustedDownloadUrl(_update.ExeUrl))
            {
                OpenReleasePage();
                return;
            }

            // Pokaż changelog i potwierdzenie PRZED aktualizacją (zamiast zwykłego MessageBox).
            var notesDlg = new ReleaseNotesWindow(string.Format(L("S.update.newtitle"), _update.Version),
                _update.Version, _update.Notes, _update.HtmlUrl, confirm: true) { Owner = _owner };
            if (notesDlg.ShowDialog() != true) return;

            string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "Waypoint-update-" + _update.Version + ".exe");
            string label = _owner.UpdateBtn.Content as string;
            _owner.UpdateBtn.IsEnabled = false;
            try
            {
                await DownloadFileAsync(_update.ExeUrl, temp, _update.ExeSize);
                if (!IsValidExe(temp, _update.ExeSize)) throw new Exception("plik pobrany niepoprawnie");
            }
            catch (Exception ex)
            {
                _owner.UpdateBtn.IsEnabled = true;
                _owner.UpdateBtn.Content = label;
                // Pobranie w apce padło (np. 504 z proxy/CDN dla dużego pliku) — zaproponuj pobranie w
                // przeglądarce (radzi sobie z dużymi plikami / wznawianiem lepiej niż nasz strumień).
                var res = MessageBox.Show(L("S.update.faildl") + "\n" + ex.Message + "\n\n" + L("S.update.openbrowserq"),
                    L("S.update.title"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res == MessageBoxResult.Yes) OpenReleasePage();
                return;
            }

            // Weryfikacja wydawcy (Authenticode „publisher pinning"): pobrany plik musi być podpisany
            // tym samym certyfikatem, co bieżąca aplikacja. Odrzucamy podmieniony/niepodpisany plik.
            var verdict = Core.CodeSign.VerifyPublisher(temp, Environment.ProcessPath);
            if (!Core.CodeSign.IsAcceptable(verdict))
            {
                try { System.IO.File.Delete(temp); } catch { }
                _owner.UpdateBtn.IsEnabled = true;
                _owner.UpdateBtn.Content = label;
                MessageBox.Show(L("S.update.badsig"), L("S.update.title"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Migawka ustawień PRZED podmianą exe (z pamięci — źródło prawdy); po restarcie na nową wersję
            // ConsumeUpdateSnapshot (w Window_Loaded) przywróci je, nawet jeśli settings.json ucierpi w trakcie.
            SettingsStore.SnapshotForUpdate(_owner._settings);

            // Uruchom pobrany exe jako „installer": poczeka aż ten proces zniknie, podmieni plik docelowy i wystartuje go.
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(temp)
                {
                    UseShellExecute = false,
                    Arguments = "--apply-update \"" + Environment.ProcessPath + "\" "
                                + System.Diagnostics.Process.GetCurrentProcess().Id
                });
            }
            catch (Exception ex)
            {
                _owner.UpdateBtn.IsEnabled = true;
                _owner.UpdateBtn.Content = label;
                MessageBox.Show(L("S.update.faildl") + "\n" + ex.Message, L("S.update.title"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _updating = true;   // Window_Closing pominie potwierdzenie i zapisze otwarte sesje do przywrócenia
            _owner.Close();
        }

        private void OpenReleasePage()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    _update?.HtmlUrl ?? "https://github.com/FilipB97/Waypoint/releases/latest") { UseShellExecute = true });
            }
            catch { /* brak przeglądarki — ignoruj */ }
        }

        // Pobiera plik z ponawianiem przy błędach przejściowych (504/502/503/timeout — częste dla dużego
        // assetu przez firmowy proxy/CDN GitHuba). Po wyczerpaniu prób rzuca ostatni wyjątek (wołający
        // proponuje pobranie w przeglądarce).
        private async Task DownloadFileAsync(string url, string dest, long knownSize)
        {
            const int attempts = 3;
            for (int attempt = 1; ; attempt++)
            {
                try { await DownloadOnceAsync(url, dest, knownSize); return; }
                catch (Exception ex) when (attempt < attempts && IsTransientDownloadError(ex))
                {
                    _owner.UpdateBtn.Content = string.Format(L("S.update.retrying"), attempt, attempts);
                    await Task.Delay(1500 * attempt);
                }
            }
        }

        // Błąd przejściowy = timeout albo 408/429/5xx (brama/proxy) lub błąd sieci bez kodu — warto ponowić.
        private static bool IsTransientDownloadError(Exception ex)
        {
            if (ex is TaskCanceledException || ex is OperationCanceledException) return true;   // timeout HttpClient
            if (ex is System.IO.IOException) return true;
            if (ex is System.Net.Http.HttpRequestException hre)
            {
                if (hre.StatusCode == null) return true;
                int c = (int)hre.StatusCode.Value;
                return c == 408 || c == 429 || c == 500 || c == 502 || c == 503 || c == 504;
            }
            return false;
        }

        // Pobiera plik strumieniowo z paskiem % na przycisku aktualizacji (kontynuacje async wracają na wątek UI).
        private async Task DownloadOnceAsync(string url, string dest, long knownSize)
        {
            using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Waypoint");
                using (var resp = await http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    long total = resp.Content.Headers.ContentLength ?? knownSize;
                    using (var src = await resp.Content.ReadAsStreamAsync())
                    using (var fs = new System.IO.FileStream(dest, System.IO.FileMode.Create,
                                        System.IO.FileAccess.Write, System.IO.FileShare.None))
                    {
                        var buf = new byte[81920];
                        long done = 0; int read, lastPct = -1;
                        while ((read = await src.ReadAsync(buf, 0, buf.Length)) > 0)
                        {
                            await fs.WriteAsync(buf, 0, read);
                            done += read;
                            if (total > 0)
                            {
                                int pct = (int)(done * 100 / total);
                                if (pct != lastPct) { lastPct = pct; _owner.UpdateBtn.Content = string.Format(L("S.update.downloading"), pct); }
                            }
                        }
                    }
                }
            }
        }

        // Zabezpieczenie przed podmianą na uszkodzony/częściowy plik: sensowny rozmiar + nagłówek PE „MZ".
        private static bool IsValidExe(string path, long expectedSize)
        {
            try
            {
                var fi = new System.IO.FileInfo(path);
                if (!fi.Exists || fi.Length < 1_000_000) return false;
                if (expectedSize > 0 && fi.Length != expectedSize) return false;
                using (var fs = System.IO.File.OpenRead(path))
                    return fs.ReadByte() == 'M' && fs.ReadByte() == 'Z';
            }
            catch { return false; }
        }

        // ---------- Karta „O aplikacji": stan aktualizacji + historia zmian ----------

        // Karta aktualizacji w „O aplikacji": stan „dostępna" (tytuł + rozmiar + akcent + przycisk instalacji).
        private void ShowAboutUpdateAvailable(object version, long sizeBytes)
        {
            if (_owner.AboutUpdateCard == null) return;
            _owner.AboutUpdateIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowDownload24;
            _owner.AboutUpdateIcon.Foreground = _owner.Res("Accent");
            _owner.AboutUpdateTitle.Text = string.Format(L("S.about.updateavailable"), version);
            _owner.AboutUpdateSub.Text = string.Format(L("S.about.updateready"), (sizeBytes / 1048576.0).ToString("0.0") + " MB");
            _owner.AboutUpdateSub.Visibility = sizeBytes > 0 ? Visibility.Visible : Visibility.Collapsed;
            _owner.AboutUpdateInstall.Visibility = Visibility.Visible;
            _owner.AboutUpdateCard.BorderBrush = _owner.Res("Accent");
            _owner.AboutUpdateCard.Background = _owner.Res("AccentSoft");
        }

        // Karta aktualizacji: stan „masz najnowszą" (albo błąd sprawdzania) — bez przycisku instalacji.
        private void SetAboutUpToDate(string title, bool error = false)
        {
            if (_owner.AboutUpdateCard == null) return;
            _owner.AboutUpdateIcon.Symbol = error ? Wpf.Ui.Controls.SymbolRegular.Warning24 : Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24;
            _owner.AboutUpdateIcon.Foreground = error ? _owner.Res("Danger") : _owner.Res("TextSec");
            _owner.AboutUpdateTitle.Text = title;
            _owner.AboutUpdateSub.Visibility = Visibility.Collapsed;
            _owner.AboutUpdateInstall.Visibility = Visibility.Collapsed;
            _owner.AboutUpdateCard.BorderBrush = _owner.Res("Border");
            _owner.AboutUpdateCard.Background = _owner.Res("Panel");
        }

        // Historia zmian (Compass §4.11): z wydań GitHub (jeśli pobrana), inaczej kurowany fallback z Changelog.cs.
        private System.Collections.Generic.List<ChangelogEntry> _changelog;
        private bool _changelogLoading;

        // Pobiera wydania z GitHuba i buduje historię zmian z realnych notatek (raz na sesję; przy błędzie
        // zostaje fallback z Changelog.cs). Wołane przy wejściu w Ustawienia.
        private async void LoadChangelogAsync()
        {
            if (_changelog != null || _changelogLoading) return;
            _changelogLoading = true;
            try
            {
                string json;
                using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(6) })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("Waypoint");
                    json = await http.GetStringAsync("https://api.github.com/repos/FilipB97/Waypoint/releases?per_page=10");
                }
                var releases = Core.UpdateCheck.ParseReleaseList(json);
                var entries = new System.Collections.Generic.List<ChangelogEntry>();
                foreach (var r in releases)
                {
                    var items = Changelog.ParseNotes(r.Notes);
                    if (items.Count == 0) continue;   // pomiń wydania bez punktowanych notatek
                    entries.Add(new ChangelogEntry { Version = r.Version, Date = r.Date, Latest = entries.Count == 0, Items = items });
                }
                if (entries.Count > 0) { _changelog = entries; BuildChangelog(); }
            }
            catch { /* offline / rate limit — zostaje fallback */ }
            finally { _changelogLoading = false; }
        }

        private void BuildChangelog()
        {
            _owner.ChangelogList.Children.Clear();
            var culture = new System.Globalization.CultureInfo(_owner._settings != null && _owner._settings.Language == "en" ? "en-US" : "pl-PL");
            var source = _changelog != null && _changelog.Count > 0 ? _changelog : (System.Collections.Generic.IReadOnlyList<ChangelogEntry>)Changelog.Entries;
            bool first = true;
            foreach (var entry in source)
            {
                if (!first)
                    _owner.ChangelogList.Children.Add(new Border { Height = 1, Background = _owner.Res("Border"), Margin = new Thickness(0, 12, 0, 12) });
                first = false;

                var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                // Lewa kolumna: wersja (+ badge NOWA) + data
                var left = new StackPanel();
                var verRow = new StackPanel { Orientation = Orientation.Horizontal };
                verRow.Children.Add(new TextBlock
                {
                    Text = entry.Version, Foreground = _owner.Res("TextPrim"), FontWeight = FontWeights.SemiBold,
                    FontFamily = (FontFamily)_owner.TryFindResource("Mono"), FontSize = (double)_owner.TryFindResource("FontBody"), VerticalAlignment = VerticalAlignment.Center
                });
                if (entry.Latest)
                    verRow.Children.Add(MakePill(L("S.chg.latest"), _owner.Res("Accent")));
                left.Children.Add(verRow);
                string dateText = System.DateTime.TryParse(entry.Date, out var dt) ? dt.ToString("d MMM yyyy", culture) : entry.Date;
                left.Children.Add(new TextBlock { Text = dateText, Foreground = _owner.Res("TextTer"),
                    FontFamily = (FontFamily)_owner.TryFindResource("Mono"), FontSize = (double)_owner.TryFindResource("FontCaption"), Margin = new Thickness(0, 3, 0, 0) });
                Grid.SetColumn(left, 0);
                grid.Children.Add(left);

                // Prawa kolumna: pozycje z etykietą rodzaju
                var items = new StackPanel();
                foreach (var it in entry.Items)
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
                    row.Children.Add(MakePill(ChangeKindLabel(it.Kind), ChangeKindBrush(it.Kind), leadingMargin: false));
                    row.Children.Add(new TextBlock
                    {
                        Text = it.Text, Foreground = _owner.Res("TextSec"), FontSize = (double)_owner.TryFindResource("FontSmall"),
                        TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0)
                    });
                    items.Children.Add(row);
                }
                Grid.SetColumn(items, 1);
                grid.Children.Add(items);
                _owner.ChangelogList.Children.Add(grid);
            }
        }

        private string ChangeKindLabel(ChangeKind k)
            => k == ChangeKind.New ? L("S.chg.new") : k == ChangeKind.Change ? L("S.chg.change") : L("S.chg.fix");
        // Kolory badge'y jak w mockupie: NOWE=zielony, ZMIANA=jaśniejszy niebieski (AccentBright), POPRAWKA=bursztyn.
        private Brush ChangeKindBrush(ChangeKind k)
            => k == ChangeKind.New ? _owner.Res("Online") : k == ChangeKind.Change ? _owner.Res("AccentBright") : _owner.Res("Idle");

        // Mała pigułka (badge) — kolorowe tło z alfą + tekst w kolorze; do etykiet rodzaju zmiany / „NOWA".
        private FrameworkElement MakePill(string text, Brush color, bool leadingMargin = true)
        {
            var c = (color as SolidColorBrush)?.Color ?? System.Windows.Media.Colors.Gray;
            return new Border
            {
                CornerRadius = new CornerRadius(5),   // .clk/.cltag mockupu: radius 5, tło ~14–16% koloru
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x26, c.R, c.G, c.B)),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = leadingMargin ? new Thickness(8, 0, 0, 0) : new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = text, Foreground = color, FontSize = (double)_owner.TryFindResource("FontMicro") + 1.5, FontWeight = FontWeights.Bold }
            };
        }

        // „Co nowego": pobierz najnowsze wydanie i pokaż jego changelog; przy braku sieci — otwórz stronę wydań.
        internal async void AboutWhatsNew_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var info = await FetchLatestReleaseAsync();
                if (info != null && !string.IsNullOrWhiteSpace(info.Notes))
                    new ReleaseNotesWindow(string.Format(L("S.update.newtitle"), info.Version),
                        info.Version, info.Notes, info.HtmlUrl, confirm: false) { Owner = _owner }.ShowDialog();
                else
                    MainWindow.OpenUrl("https://github.com/FilipB97/Waypoint/releases");
            }
            catch { MainWindow.OpenUrl("https://github.com/FilipB97/Waypoint/releases"); }
        }
    }
}
