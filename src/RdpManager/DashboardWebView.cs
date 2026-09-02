using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RdpManager.Core;

namespace RdpManager
{
    /// <summary>
    /// Host pulpitu: WebView2 ze stroną z <see cref="DashboardHtml"/>. Strona ładuje się RAZ, a każde
    /// odświeżenie to jedna wiadomość z danymi i tokenami motywu — bez przeładowania i bez migotania.
    ///
    /// Kolory NIE są wpisane w stronę. Czytamy je z żywych zasobów WPF przy każdym renderze, więc
    /// presety palety i własny kolor akcentu działają na pulpicie dokładnie tak, jak w reszcie okna
    /// (w WPF robi to DynamicResource; tutaj musi to zrobić ta klasa).
    ///
    /// Gdy WebView2 nie wystartuje (brak środowiska uruchomieniowego, zablokowany katalog danych),
    /// pulpit pokazuje komunikat zamiast pustego prostokąta — aplikacja działa dalej.
    /// </summary>
    public sealed class DashboardWebView : Grid
    {
        private readonly WebView2 _web = new WebView2();
        private readonly TextBlock _fallback;
        private bool _ready;
        private bool _failed;
        private string _pending;      // render zamówiony, zanim strona zdążyła się załadować

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public DashboardWebView()
        {
            _web.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            Children.Add(_web);

            _fallback = new TextBlock
            {
                Visibility = Visibility.Collapsed,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(2, 12, 0, 0)
            };
            Children.Add(_fallback);
        }

        /// <summary>Renderuje pulpit. Bezpieczne do wołania przed inicjalizacją — zapamiętuje ostatni stan.</summary>
        public async void Render(DashboardModel model, IReadOnlyDictionary<string, object> strings)
        {
            if (_failed) return;
            string payload = JsonSerializer.Serialize(new
            {
                model,
                strings,
                theme = ReadTheme()
            }, Json);

            _pending = payload;
            if (!_ready) { await InitAsync(); return; }
            Post();
        }

        private void Post()
        {
            if (_pending == null) return;
            try { _web.CoreWebView2?.PostWebMessageAsJson(_pending); } catch { /* strona znika przy zamykaniu */ }
        }

        private async Task InitAsync()
        {
            if (_ready || _failed) return;
            try
            {
                if (!_web.IsLoaded)
                {
                    var tcs = new TaskCompletionSource<bool>();
                    RoutedEventHandler h = null;
                    h = (s, e) => { _web.Loaded -= h; tcs.TrySetResult(true); };
                    _web.Loaded += h;
                    await tcs.Task;
                }

                // Ten sam katalog danych co terminale — obok exe może być tylko do odczytu.
                var env = await CoreWebView2Environment.CreateAsync(null,
                    Path.Combine(SettingsStore.Dir, "webview2"));
                await _web.EnsureCoreWebView2Async(env);

                var s2 = _web.CoreWebView2.Settings;
                s2.AreDefaultContextMenusEnabled = false;
                s2.AreDevToolsEnabled = false;
                s2.IsStatusBarEnabled = false;
                s2.IsZoomControlEnabled = false;

                _web.CoreWebView2.NavigationCompleted += (s, e) => { _ready = true; Post(); };
                _web.CoreWebView2.NavigateToString(DashboardHtml.Page);
            }
            catch (Exception ex)
            {
                _failed = true;
                _web.Visibility = Visibility.Collapsed;
                _fallback.Foreground = Res("TextTer");
                _fallback.Text = string.Format(LocalizationManager.S("S.dash.nowebview"), ex.Message);
                _fallback.Visibility = Visibility.Visible;
            }
        }

        /// <summary>
        /// Tokeny motywu z ŻYWEJ palety. Nazwy kluczy odpowiadają tym z Palette.*.xaml, więc zmiana
        /// palety albo presetu przenosi się na pulpit bez dotykania strony.
        /// </summary>
        private Dictionary<string, object> ReadTheme() => new Dictionary<string, object>
        {
            ["prim"] = Hex("TextPrim"),
            ["sec"] = Hex("TextSec"),
            ["ter"] = Hex("TextTer"),
            ["panel"] = Hex("Panel"),
            ["border"] = Rgba("Border"),
            ["accent"] = Hex("Accent"),
            ["accentSoft"] = Rgba("AccentSoft"),
            ["track"] = Rgba("Elevated"),
            ["cOnline"] = Hex("Online"),
            ["cIdle"] = Hex("Idle"),
            ["cOffline"] = Hex("Offline"),
            ["proto"] = new Dictionary<string, string>
            {
                ["ProtoRdp"] = Hex("ProtoRdp"),
                ["ProtoSsh"] = Hex("ProtoSsh"),
                ["ProtoSftp"] = Hex("ProtoSftp"),
                ["ProtoRest"] = Hex("ProtoRest"),
                ["ProtoWeb"] = Hex("ProtoWeb"),
                ["ProtoTelnet"] = Hex("ProtoTelnet")
            }
        };

        private static Color ColorOf(string key)
            => (Application.Current?.TryFindResource(key) as SolidColorBrush)?.Color ?? Colors.Gray;

        private static string Hex(string key)
        {
            var c = ColorOf(key);
            return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }

        // Klucze półprzezroczyste (obramowanie, tinty) muszą zachować kanał alfa — inaczej hairline
        // zrobiłby się pełną, jasną kreską na ciemnym panelu.
        private static string Rgba(string key)
        {
            var c = ColorOf(key);
            return $"rgba({c.R},{c.G},{c.B},{(c.A / 255.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)})";
        }

        private static Brush Res(string key)
            => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }
}
