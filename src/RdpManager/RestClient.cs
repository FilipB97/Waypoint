using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RdpManager.Models;

namespace RdpManager
{
    /// <summary>Wynik wysłania żądania REST. <see cref="Ok"/>=false oznacza błąd transportu (DNS/timeout/TLS),
    /// a nie kod 4xx/5xx — te przychodzą jako normalna odpowiedź z <see cref="Status"/>.</summary>
    public sealed class RestResponse
    {
        public bool Ok;
        public int Status;
        public string ReasonPhrase = "";
        public long ElapsedMs;
        public long Size;
        public string Body = "";
        public string ContentType = "";
        public List<KeyValuePair<string, string>> Headers = new List<KeyValuePair<string, string>>();
        public string Error = "";
    }

    /// <summary>
    /// Silnik klienta REST: buduje <see cref="HttpRequestMessage"/> z <see cref="RestRequest"/> i wysyła
    /// jednym współdzielonym <see cref="HttpClient"/>. Bez zależności zewnętrznych (jak updater w MainWindow).
    /// </summary>
    public static class RestClient
    {
        private static readonly HttpClient Http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All
        })
        { Timeout = TimeSpan.FromSeconds(60) };

        /// <summary>Wysyła żądanie. <paramref name="authSecret"/> = token (Bearer) lub hasło (Basic) z Credential Manager.</summary>
        public static async Task<RestResponse> SendAsync(RestRequest req, string authSecret, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using (var msg = Build(req, authSecret))
                using (var resp = await Http.SendAsync(msg, HttpCompletionOption.ResponseContentRead, ct))
                {
                    string body = await resp.Content.ReadAsStringAsync(ct);
                    sw.Stop();

                    var headers = new List<KeyValuePair<string, string>>();
                    foreach (var h in resp.Headers) headers.Add(new KeyValuePair<string, string>(h.Key, string.Join(", ", h.Value)));
                    foreach (var h in resp.Content.Headers) headers.Add(new KeyValuePair<string, string>(h.Key, string.Join(", ", h.Value)));

                    return new RestResponse
                    {
                        Ok = true,
                        Status = (int)resp.StatusCode,
                        ReasonPhrase = resp.ReasonPhrase ?? "",
                        ElapsedMs = sw.ElapsedMilliseconds,
                        Size = resp.Content.Headers.ContentLength ?? Encoding.UTF8.GetByteCount(body),
                        Body = body,
                        ContentType = resp.Content.Headers.ContentType?.ToString() ?? "",
                        Headers = headers
                    };
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                sw.Stop();
                return new RestResponse { Ok = false, ElapsedMs = sw.ElapsedMilliseconds, Error = "Timeout" };
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new RestResponse { Ok = false, ElapsedMs = sw.ElapsedMilliseconds, Error = (ex.InnerException ?? ex).Message };
            }
        }

        private static HttpRequestMessage Build(RestRequest req, string authSecret)
        {
            var msg = new HttpRequestMessage(new HttpMethod((req.Method ?? "GET").Trim().ToUpperInvariant()), BuildRequestUri(req));

            // Body tylko dla metod, które go niosą, i gdy niepuste.
            bool hasBody = !string.IsNullOrEmpty(req.Body)
                           && msg.Method != HttpMethod.Get && msg.Method != HttpMethod.Head;

            // Content-Type z nagłówków ma pierwszeństwo nad polem body.
            string contentType = req.Headers
                .Where(h => h.Enabled && string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                .Select(h => h.Value).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(contentType)) contentType = req.BodyContentType;

            if (hasBody)
            {
                msg.Content = new StringContent(req.Body, Encoding.UTF8);
                if (!string.IsNullOrWhiteSpace(contentType))
                {
                    try { msg.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType); } catch { /* zła wartość → zostaje domyślny */ }
                }
            }

            foreach (var h in req.Headers.Where(h => h.Enabled && !string.IsNullOrWhiteSpace(h.Key)))
            {
                if (string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)) continue;   // ustawione na treści
                if (!msg.Headers.TryAddWithoutValidation(h.Key, h.Value))
                    msg.Content?.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }

            // Uwierzytelnianie z zakładki Auth nadpisuje ewentualny ręczny nagłówek Authorization.
            if (req.AuthType == 1 && !string.IsNullOrEmpty(authSecret))
                msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authSecret);
            else if (req.AuthType == 2)
                msg.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes((req.AuthUsername ?? "") + ":" + (authSecret ?? ""))));

            return msg;
        }

        /// <summary>Buduje docelowy URI: dokleja włączone parametry zapytania i uzupełnia schemat (https) gdy brak. Publiczne dla testów.</summary>
        public static Uri BuildRequestUri(RestRequest req)
        {
            string url = (req.Url ?? "").Trim();
            if (url.Length > 0 && !url.Contains("://")) url = "https://" + url;

            var qp = req.QueryParams
                .Where(p => p.Enabled && !string.IsNullOrWhiteSpace(p.Key))
                .Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value ?? ""))
                .ToList();
            if (qp.Count > 0)
                url += (url.Contains("?") ? "&" : "?") + string.Join("&", qp);

            return new Uri(url, UriKind.Absolute);
        }
    }
}
