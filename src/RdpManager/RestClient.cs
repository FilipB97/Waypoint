using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
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
        /// <summary>Twardy limit rozmiaru odpowiedzi — bez niego serwer (złośliwy albo po prostu zwracający
        /// coś nieoczekiwanego) mógłby wymusić bufor rosnący bez ograniczeń (OOM). Publiczne dla testów.</summary>
        public const long MaxResponseBytes = 20L * 1024 * 1024;

        private static readonly HttpClient Http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All
        })
        { Timeout = TimeSpan.FromSeconds(60) };

        /// <summary>Wysyła żądanie. <paramref name="authSecret"/> = token (Bearer) lub hasło (Basic) z Credential Manager.
        /// <paramref name="vars"/> = zmienne aktywnego środowiska podstawiane jako {{klucz}} (null = brak).</summary>
        public static async Task<RestResponse> SendAsync(RestRequest req, string authSecret, IReadOnlyDictionary<string, string> vars, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using (var msg = Build(req, authSecret, vars))
                // ResponseHeadersRead (nie ResponseContentRead): sprawdzamy Content-Length ZANIM zaczniemy
                // czytać ciało — deklarowany-zbyt-duży rozmiar odrzucamy bez pobierania ani bajta.
                using (var resp = await Http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    long? declared = resp.Content.Headers.ContentLength;
                    if (declared.HasValue && declared.Value > MaxResponseBytes)
                    {
                        sw.Stop();
                        return TooLargeResponse(sw.ElapsedMilliseconds, declared);
                    }

                    byte[] bytes;
                    using (var stream = await resp.Content.ReadAsStreamAsync(ct))
                        bytes = await ReadBoundedAsync(stream, MaxResponseBytes, ct);
                    sw.Stop();

                    // Content-Length brakujący/kłamliwy (chunked, proxy) — limit egzekwowany też PODCZAS czytania.
                    if (bytes == null) return TooLargeResponse(sw.ElapsedMilliseconds, null);

                    string body = Encoding.UTF8.GetString(bytes);
                    var headers = new List<KeyValuePair<string, string>>();
                    foreach (var h in resp.Headers) headers.Add(new KeyValuePair<string, string>(h.Key, string.Join(", ", h.Value)));
                    foreach (var h in resp.Content.Headers) headers.Add(new KeyValuePair<string, string>(h.Key, string.Join(", ", h.Value)));

                    return new RestResponse
                    {
                        Ok = true,
                        Status = (int)resp.StatusCode,
                        ReasonPhrase = resp.ReasonPhrase ?? "",
                        ElapsedMs = sw.ElapsedMilliseconds,
                        Size = bytes.LongLength,
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

        private static RestResponse TooLargeResponse(long elapsedMs, long? declaredBytes) => new RestResponse
        {
            Ok = false,
            ElapsedMs = elapsedMs,
            Error = declaredBytes.HasValue
                ? $"Response too large ({FormatMb(declaredBytes.Value)}) — limit is {FormatMb(MaxResponseBytes)}. Not displayed."
                : $"Response exceeded the {FormatMb(MaxResponseBytes)} limit while downloading. Not displayed."
        };

        private static string FormatMb(long bytes) => (bytes / 1048576.0).ToString("0.#") + " MB";

        /// <summary>Czyta strumień do bufora w pamięci, przerywając (null) jeśli przekroczy <paramref name="maxBytes"/>
        /// — bez tego bufor rósłby bez ograniczeń dla odpowiedzi bez znanego/wiarygodnego Content-Length
        /// (chunked, proxy kłamiący o rozmiarze). Publiczne dla testów.</summary>
        public static async Task<byte[]> ReadBoundedAsync(Stream stream, long maxBytes, CancellationToken ct)
        {
            using (var buffer = new MemoryStream())
            {
                byte[] chunk = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(chunk, 0, chunk.Length, ct)) > 0)
                {
                    buffer.Write(chunk, 0, read);
                    if (buffer.Length > maxBytes) return null;
                }
                return buffer.ToArray();
            }
        }

        private static HttpRequestMessage Build(RestRequest req, string authSecret, IReadOnlyDictionary<string, string> vars)
        {
            var msg = new HttpRequestMessage(new HttpMethod((req.Method ?? "GET").Trim().ToUpperInvariant()), BuildRequestUri(req, vars));

            // Content-Type z nagłówków ma pierwszeństwo nad polem body.
            string contentType = req.Headers
                .Where(h => h.Enabled && string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                .Select(h => h.Value).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(contentType)) contentType = req.BodyContentType;

            bool isForm = IsFormUrlEncoded(contentType);
            bool hasFields = isForm && req.FormFields.Any(f => f.Enabled && !string.IsNullOrWhiteSpace(f.Key));
            // Body tylko dla metod, które go niosą, i gdy niepuste (tabela pól LUB surowy tekst dla form; surowy tekst dla reszty typów).
            bool hasBody = (msg.Method != HttpMethod.Get && msg.Method != HttpMethod.Head)
                           && (hasFields || !string.IsNullOrEmpty(req.Body));

            if (hasBody)
            {
                // Dla form-urlencoded podstawiamy i kodujemy każdą wartość osobno (jak parametry zapytania) —
                // inaczej sekret zawierający +,/,= rozjechałby się. Tabela pól (edytor) ma pierwszeństwo nad
                // starszym surowym tekstem (kompatybilność z żądaniami zapisanymi przed edytorem). Inne typy:
                // podstaw i wyślij treść tak jak jest.
                string content = !isForm ? Subst(req.Body, vars)
                                : hasFields ? BuildFormBodyFromFields(req.FormFields, vars)
                                : BuildFormBody(req.Body, vars);
                msg.Content = new StringContent(content, Encoding.UTF8);
                if (!string.IsNullOrWhiteSpace(contentType))
                {
                    try { msg.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(Subst(contentType, vars)); } catch { /* zła wartość → zostaje domyślny */ }
                }
            }

            foreach (var h in req.Headers.Where(h => h.Enabled && !string.IsNullOrWhiteSpace(h.Key)))
            {
                if (string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)) continue;   // ustawione na treści
                string val = Subst(h.Value, vars);
                if (!msg.Headers.TryAddWithoutValidation(h.Key, val))
                    msg.Content?.Headers.TryAddWithoutValidation(h.Key, val);
            }

            // Uwierzytelnianie z zakładki Auth nadpisuje ewentualny ręczny nagłówek Authorization.
            string secret = Subst(authSecret, vars);
            if (req.AuthType == 1 && !string.IsNullOrEmpty(secret))
                msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            else if (req.AuthType == 2)
                msg.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(Subst(req.AuthUsername ?? "", vars) + ":" + (secret ?? ""))));

            return msg;
        }

        private static bool IsFormUrlEncoded(string contentType)
            => !string.IsNullOrEmpty(contentType)
               && contentType.IndexOf("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Buduje treść application/x-www-form-urlencoded z tabeli pól (edytor klucz/wartość):
        /// pomija wyłączone/puste-klucz, podstawia {{zmienne}} i koduje każdą część PO podstawieniu.
        /// Publiczne dla testów.</summary>
        public static string BuildFormBodyFromFields(List<RestKeyValue> fields, IReadOnlyDictionary<string, string> vars)
        {
            if (fields == null) return "";
            var parts = new List<string>();
            foreach (var f in fields)
            {
                if (!f.Enabled || string.IsNullOrWhiteSpace(f.Key)) continue;
                parts.Add(Uri.EscapeDataString(Subst(f.Key, vars)) + "=" + Uri.EscapeDataString(Subst(f.Value ?? "", vars)));
            }
            return string.Join("&", parts);
        }

        /// <summary>Buduje treść application/x-www-form-urlencoded: dzieli szablon „k=v&amp;k=v", podstawia
        /// {{zmienne}} i koduje każdą część PO podstawieniu (wartość z {{var}} może mieć +,/,= — musi być
        /// zakodowana, jak parametry zapytania). Publiczne dla testów.</summary>
        public static string BuildFormBody(string body, IReadOnlyDictionary<string, string> vars)
        {
            if (string.IsNullOrEmpty(body)) return "";
            var parts = new List<string>();
            foreach (var seg in body.Split('&'))
            {
                if (seg.Length == 0) continue;
                int eq = seg.IndexOf('=');
                if (eq < 0) { parts.Add(Uri.EscapeDataString(Subst(seg, vars))); continue; }
                string k = Uri.EscapeDataString(Subst(seg.Substring(0, eq), vars));
                string v = Uri.EscapeDataString(Subst(seg.Substring(eq + 1), vars));
                parts.Add(k + "=" + v);
            }
            return string.Join("&", parts);
        }

        // Podstawia {{klucz}} wartościami zmiennych (nieznane {{x}} zostają bez zmian). Publiczne dla testów.
        public static string Subst(string s, IReadOnlyDictionary<string, string> vars)
        {
            if (string.IsNullOrEmpty(s) || vars == null || vars.Count == 0) return s ?? "";
            return Regex.Replace(s, @"\{\{\s*([^{}\s]+)\s*\}\}",
                m => vars.TryGetValue(m.Groups[1].Value, out var v) ? (v ?? "") : m.Value);
        }

        /// <summary>Buduje docelowy URI: podstawia {{zmienne}}, dokleja włączone parametry zapytania
        /// i uzupełnia schemat (https) gdy brak. Publiczne dla testów.</summary>
        public static Uri BuildRequestUri(RestRequest req, IReadOnlyDictionary<string, string> vars = null)
        {
            string url = Subst((req.Url ?? "").Trim(), vars);
            if (url.Length > 0 && !url.Contains("://")) url = "https://" + url;

            var qp = req.QueryParams
                .Where(p => p.Enabled && !string.IsNullOrWhiteSpace(p.Key))
                .Select(p => Uri.EscapeDataString(Subst(p.Key, vars)) + "=" + Uri.EscapeDataString(Subst(p.Value ?? "", vars)))
                .ToList();
            if (qp.Count > 0)
                url += (url.Contains("?") ? "&" : "?") + string.Join("&", qp);

            return new Uri(url, UriKind.Absolute);
        }
    }
}
