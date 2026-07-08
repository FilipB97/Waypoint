using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using RdpManager.Models;

namespace RdpManager
{
    /// <summary>
    /// Konsola REST dla jednego wpisu-API („wpis = jedno API/kolekcja"). PR1: pojedyncze żądanie
    /// (metoda/URL/params/nagłówki/treść/auth) + wysyłka przez <see cref="RestClient"/> i podgląd odpowiedzi.
    /// Sekret auth (token/hasło Basic) trzymany w Credential Manager, nie w rest.json.
    /// Kolekcje/foldery, środowiska i historia — kolejne PR-y (model już to przewiduje).
    /// </summary>
    public partial class RestConsole : UserControl
    {
        private readonly ServerInfo _server;
        private RestCollection _coll;
        private RestRequest _req;
        private ObservableCollection<RestKeyValue> _params;
        private ObservableCollection<RestKeyValue> _headers;
        private CancellationTokenSource _cts;
        private string _rawBody;

        private static readonly string[] Methods = { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" };

        private static string L(string key) => LocalizationManager.S(key);

        public RestConsole(ServerInfo server)
        {
            InitializeComponent();
            _server = server;
            LoadRequest();
            UrlBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) Send_Click(s, e); };
        }

        // ---------- Wczytanie / zapis ----------

        private void LoadRequest()
        {
            _coll = RestStore.For(_server.Id);
            if (string.IsNullOrEmpty(_coll.BaseUrl)) _coll.BaseUrl = _server.Host ?? "";
            if (_coll.Requests.Count == 0)
                _coll.Requests.Add(new RestRequest { Name = "Request 1", Url = _coll.BaseUrl });
            _req = _coll.Requests[0];

            SelectMethod(_req.Method);
            UrlBox.Text = _req.Url;
            _params = new ObservableCollection<RestKeyValue>(_req.QueryParams);
            _headers = new ObservableCollection<RestKeyValue>(_req.Headers);
            ParamsList.ItemsSource = _params;
            HeadersList.ItemsSource = _headers;
            BodyBox.Text = _req.Body;
            SelectContentType(_req.BodyContentType);
            AuthCombo.SelectedIndex = Math.Clamp(_req.AuthType, 0, 2);
            AuthUserBox.Text = _req.AuthUsername;
            if (CredentialStore.TryRead(_req.AuthCredTarget, out var secret))
            {
                if (_req.AuthType == 1) TokenBox.Password = secret;
                else if (_req.AuthType == 2) AuthPassBox.Password = secret;
            }
            UpdateAuthPanels();
        }

        // Zbiera stan formularza do modelu żądania (przed wysyłką i przed zapisem).
        private void ApplyToRequest()
        {
            _req.Method = SelectedMethod();
            _req.Url = (UrlBox.Text ?? "").Trim();
            _req.QueryParams = _params.ToList();
            _req.Headers = _headers.ToList();
            _req.Body = BodyBox.Text ?? "";
            _req.BodyContentType = SelectedContentType();
            _req.AuthType = AuthCombo.SelectedIndex < 0 ? 0 : AuthCombo.SelectedIndex;
            _req.AuthUsername = AuthUserBox.Text ?? "";
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            ApplyToRequest();
            PersistSecret();
            RestStore.Put(_server.Id, _coll);
            SetStatus(L("S.rest.saved"));
        }

        // Sekret auth → Credential Manager (usuwany, gdy brak auth lub pusty).
        private void PersistSecret()
        {
            string secret = SecretFromUi();
            if (_req.AuthType == 0 || string.IsNullOrEmpty(secret))
                CredentialStore.Delete(_req.AuthCredTarget);
            else
                CredentialStore.TrySave(_req.AuthCredTarget, _req.AuthUsername, secret);
        }

        private string SecretFromUi()
            => AuthCombo.SelectedIndex == 1 ? TokenBox.Password
             : AuthCombo.SelectedIndex == 2 ? AuthPassBox.Password
             : "";

        // ---------- Wysyłka ----------

        private async void Send_Click(object sender, RoutedEventArgs e)
        {
            ApplyToRequest();
            if (string.IsNullOrWhiteSpace(_req.Url)) { SetStatus(L("S.rest.needurl"), error: true); return; }

            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            SendBtn.IsEnabled = false;
            SetStatus(L("S.rest.sending"));

            var resp = await RestClient.SendAsync(_req, SecretFromUi(), ct);
            if (ct.IsCancellationRequested) return;   // nowsze żądanie przejęło przycisk

            RenderResponse(resp);
            SendBtn.IsEnabled = true;
            SetStatus("");
        }

        private void RenderResponse(RestResponse r)
        {
            StatusPill.Visibility = Visibility.Visible;
            if (!r.Ok)
            {
                StatusPill.Background = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
                StatusPillText.Text = L("S.rest.err");
                MetaText.Text = string.Format(L("S.rest.error"), r.Error);
                _rawBody = r.Error ?? "";
                ResponseBody.Text = _rawBody;
                ResponseHeaders.ItemsSource = null;
                ResponseTabs.SelectedIndex = 0;
                return;
            }

            StatusPill.Background = PillBrush(r.Status);
            StatusPillText.Text = r.Status + (string.IsNullOrEmpty(r.ReasonPhrase) ? "" : " " + r.ReasonPhrase);
            MetaText.Text = r.ElapsedMs + " ms · " + FormatSize(r.Size);
            _rawBody = r.Body ?? "";
            ResponseBody.Text = FormatBody();
            ResponseHeaders.ItemsSource = r.Headers;
        }

        private string FormatBody()
        {
            if (string.IsNullOrEmpty(_rawBody)) return "";
            if (PrettyToggle.IsChecked != true) return _rawBody;
            string t = _rawBody.TrimStart();
            if (t.Length == 0 || (t[0] != '{' && t[0] != '[')) return _rawBody;   // tylko JSON
            try
            {
                using (var doc = JsonDocument.Parse(_rawBody))
                    return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
            }
            catch { return _rawBody; }
        }

        private void Pretty_Changed(object sender, RoutedEventArgs e)
        {
            if (_rawBody != null) ResponseBody.Text = FormatBody();
        }

        private void CopyBody_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(ResponseBody.Text ?? ""); } catch { }
        }

        // ---------- Wiersze klucz/wartość ----------

        private void AddParam_Click(object sender, RoutedEventArgs e) => _params.Add(new RestKeyValue());
        private void AddHeader_Click(object sender, RoutedEventArgs e) => _headers.Add(new RestKeyValue());

        private void DeleteKv_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is RestKeyValue kv && !_params.Remove(kv)) _headers.Remove(kv);
        }

        // ---------- Metoda / typ treści / auth ----------

        private void Method_Changed(object sender, SelectionChangedEventArgs e)
            => MethodCombo.Foreground = MethodBrush(SelectedMethod());

        private void Auth_Changed(object sender, SelectionChangedEventArgs e) => UpdateAuthPanels();

        private void UpdateAuthPanels()
        {
            int i = AuthCombo.SelectedIndex;
            BearerPanel.Visibility = i == 1 ? Visibility.Visible : Visibility.Collapsed;
            BasicPanel.Visibility = i == 2 ? Visibility.Visible : Visibility.Collapsed;
        }

        private string SelectedMethod() => (MethodCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "GET";

        private void SelectMethod(string method)
        {
            int idx = Array.IndexOf(Methods, (method ?? "GET").ToUpperInvariant());
            MethodCombo.SelectedIndex = idx < 0 ? 0 : idx;
        }

        private string SelectedContentType() => (ContentTypeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "application/json";

        private void SelectContentType(string ct)
        {
            foreach (var obj in ContentTypeCombo.Items)
                if (obj is ComboBoxItem it && string.Equals(it.Content?.ToString(), ct, StringComparison.OrdinalIgnoreCase))
                { ContentTypeCombo.SelectedItem = it; return; }
            ContentTypeCombo.SelectedIndex = 0;
        }

        // ---------- Pomocnicze ----------

        private void SetStatus(string text, bool error = false)
        {
            StatusText.Text = text ?? "";
            StatusText.Foreground = (Brush)(error ? TryFindResource("Danger") : TryFindResource("TextTer")) ?? Brushes.Gray;
        }

        private static SolidColorBrush PillBrush(int status)
        {
            if (status >= 200 && status < 300) return new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43));   // zielony
            if (status >= 300 && status < 400) return new SolidColorBrush(Color.FromRgb(0xF0, 0x9A, 0x1A));   // bursztyn
            if (status >= 400) return new SolidColorBrush(Color.FromRgb(0xD1, 0x3B, 0x3B));                    // czerwony
            return new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
        }

        private static Brush MethodBrush(string method)
        {
            switch (method)
            {
                case "GET": return new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43));
                case "POST": return new SolidColorBrush(Color.FromRgb(0xF0, 0x9A, 0x1A));
                case "PUT": return new SolidColorBrush(Color.FromRgb(0x2B, 0x7C, 0xD3));
                case "PATCH": return new SolidColorBrush(Color.FromRgb(0x8A, 0x4F, 0xC7));
                case "DELETE": return new SolidColorBrush(Color.FromRgb(0xD1, 0x3B, 0x3B));
                default: return new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
            }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024L * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
            return (bytes / 1048576.0).ToString("0.#") + " MB";
        }

        /// <summary>Anuluje żądanie w locie przy zamykaniu sesji.</summary>
        public void DisposeConsole()
        {
            try { _cts?.Cancel(); _cts?.Dispose(); } catch { }
            _cts = null;
        }
    }
}
