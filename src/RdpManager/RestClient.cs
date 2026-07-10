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

        /// <summary>Migawka tego, co FAKTYCZNIE wyszło na drut (finalny URL i body po podstawieniu
        /// {{zmiennych}}, nagłówki razem z dogenerowanymi przy wysyłce) — odpowiednik konsoli Postmana,
        /// do diagnozy „w Postmanie działa, tu 401". Wypełniana też przy błędzie transportu (widać, co
        /// by wyszło); null w <see cref="SentHeaders"/> = Build się nie powiódł (np. zły URL).
        /// Tylko w pamięci — nie trafia do historii/JSON (Authorization zawiera sekret).</summary>
        public string SentMethod = "";
        public string SentUrl = "";
        public string SentBody = "";
        public List<KeyValuePair<string, string>> SentHeaders;
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

            // Migawka „Wysłane" zbierana PRZED wysyłką i doklejana do KAŻDEGO wyniku (także błędu
            // transportu) — bez niej nie da się porównać żądania 1:1 z działającym (konsola Postmana).
            string sentMethod = "", sentUrl = "", sentBody = "";
            List<KeyValuePair<string, string>> sentHeaders = null;
            RestResponse WithSent(RestResponse r)
            {
                r.SentMethod = sentMethod; r.SentUrl = sentUrl; r.SentBody = sentBody; r.SentHeaders = sentHeaders;
                return r;
            }

            try
            {
                using (var msg = Build(req, authSecret, vars, out sentBody))
                {
                    sentMethod = msg.Method.Method;
                    sentUrl = msg.RequestUri.OriginalString;
                    sentHeaders = SnapshotHeaders(msg);

                    // ResponseHeadersRead (nie ResponseContentRead): sprawdzamy Content-Length ZANIM zaczniemy
                    // czytać ciało — deklarowany-zbyt-duży rozmiar odrzucamy bez pobierania ani bajta.
                    using (var resp = await Http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct))
                    {
                        long? declared = resp.Content.Headers.ContentLength;
                        if (declared.HasValue && declared.Value > MaxResponseBytes)
                        {
                            sw.Stop();
                            return WithSent(TooLargeResponse(sw.ElapsedMilliseconds, declared));
                        }

                        byte[] bytes;
                        using (var stream = await resp.Content.ReadAsStreamAsync(ct))
                            bytes = await ReadBoundedAsync(stream, MaxResponseBytes, ct);
                        sw.Stop();

                        // Content-Length brakujący/kłamliwy (chunked, proxy) — limit egzekwowany też PODCZAS czytania.
                        if (bytes == null) return WithSent(TooLargeResponse(sw.ElapsedMilliseconds, null));

                        string body = Encoding.UTF8.GetString(bytes);
                        var headers = new List<KeyValuePair<string, string>>();
                        foreach (var h in resp.Headers) headers.Add(new KeyValuePair<string, string>(h.Key, string.Join(", ", h.Value)));
                        foreach (var h in resp.Content.Headers) headers.Add(new KeyValuePair<string, string>(h.Key, string.Join(", ", h.Value)));

                        return WithSent(new RestResponse
                        {
                            Ok = true,
                            Status = (int)resp.StatusCode,
                            ReasonPhrase = resp.ReasonPhrase ?? "",
                            ElapsedMs = sw.ElapsedMilliseconds,
                            Size = bytes.LongLength,
                            Body = body,
                            ContentType = resp.Content.Headers.ContentType?.ToString() ?? "",
                            Headers = headers
                        });
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                sw.Stop();
                return WithSent(new RestResponse { Ok = false, ElapsedMs = sw.ElapsedMilliseconds, Error = "Timeout" });
            }
            catch (Exception ex)
            {
                sw.Stop();
                return WithSent(new RestResponse { Ok = false, ElapsedMs = sw.ElapsedMilliseconds, Error = (ex.InnerException ?? ex).Message });
            }
        }

        /// <summary>Zrzut nagłówków, które wyjdą na drut: nagłówki żądania + treści. Odczyt ContentLength
        /// WYMUSZA jego policzenie (przed wysyłką nie występuje jeszcze w enumeracji), a Host — który
        /// HttpClient dokłada dopiero przy wysyłce — jest uzupełniany z URI, gdy nie ustawiono go jawnie.
        /// Publiczne dla testów.</summary>
        public static List<KeyValuePair<string, string>> SnapshotHeaders(HttpRequestMessage msg)
        {
            var list = new List<KeyValuePair<string, string>>();
            if (msg.Headers.Host == null && msg.RequestUri != null && msg.RequestUri.IsAbsoluteUri)
                list.Add(new KeyValuePair<string, string>("Host", msg.RequestUri.Host));
            foreach (var h in msg.Headers) list.Add(new KeyValuePair<string, string>(h.Key, string.Join(", ", h.Value)));
            if (msg.Content != null)
            {
                _ = msg.Content.Headers.ContentLength;   // getter liczy długość i zapisuje ją jako nagłówek
                foreach (var h in msg.Content.Headers) list.Add(new KeyValuePair<string, string>(h.Key, string.Join(", ", h.Value)));
            }
            return list;
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

        /// <summary>Buduje gotowe do wysyłki <see cref="HttpRequestMessage"/>. <paramref name="sentBody"/> =
        /// finalna treść po podstawieniu zmiennych i zakodowaniu (pusta, gdy żądanie nie niesie body) —
        /// do migawki „Wysłane". Publiczne dla testów.</summary>
        public static HttpRequestMessage Build(RestRequest req, string authSecret, IReadOnlyDictionary<string, string> vars, out string sentBody)
        {
            sentBody = "";
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
                // podstaw i wyślij treść tak jak jest — ale znormalizuj końce linii do LF: WPF TextBox wymusza
                // CRLF, co po cichu psuło body liczone bajt po bajcie (podpisy/HMAC, formaty wrażliwe na \n).
                string content = !isForm ? NormalizeNewlines(Subst(req.Body, vars))
                                : hasFields ? BuildFormBodyFromFields(req.FormFields, vars)
                                : BuildFormBody(req.Body, vars);
                sentBody = content;
                msg.Content = new StringContent(content, Encoding.UTF8);
                if (!string.IsNullOrWhiteSpace(contentType))
                {
                    try
                    {
                        var mt = MediaTypeHeaderValue.Parse(Subst(contentType, vars));
                        // Wysyłamy bajty UTF-8; gdy użytkownik nie podał charsetu, dopisz go dla surowego body,
                        // inaczej serwer domyślający się latin-1 przekłamie znaki spoza ASCII (objaw „zmienione znaki").
                        if (!isForm && string.IsNullOrEmpty(mt.CharSet)) mt.CharSet = "utf-8";
                        msg.Content.Headers.ContentType = mt;
                    }
                    catch { /* zła wartość → zostaje domyślny */ }
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
            // Kontrakt: Build oczekuje TYPU JUŻ ROZWIĄZANEGO (0=brak, 1=Bearer, 2=Basic). Dziedziczenie
            // (3=Inherit) musi rozwiązać wołający (RestConsole.ResolveEffectiveAuth) — tu 3 = brak nagłówka.
            string secret = Subst(authSecret, vars);
            if (req.AuthType == 1 && !string.IsNullOrEmpty(secret))
                msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            else if (req.AuthType == 2)
                msg.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(Subst(req.AuthUsername ?? "", vars) + ":" + (secret ?? ""))));

            AddDefaultHeaders(msg);
            return msg;
        }

        private static readonly string UserAgent =
            "Waypoint/" + (typeof(RestClient).Assembly.GetName().Version?.ToString(3) ?? "1.0");

        /// <summary>Dokłada nagłówki, które Postman dogenerowuje przy wysyłce (a więc nie ma ich w eksporcie,
        /// stąd „brak standardowych nagłówków" po imporcie): Accept i User-Agent, gdy użytkownik ich nie ustawił.
        /// Nie nadpisuje jawnych nagłówków. Publiczne dla testów.</summary>
        public static void AddDefaultHeaders(HttpRequestMessage msg)
        {
            if (!msg.Headers.Contains("Accept")) msg.Headers.TryAddWithoutValidation("Accept", "*/*");
            if (!msg.Headers.Contains("User-Agent")) msg.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
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

        // Normalizuje końce linii do LF. Publiczne dla testów.
        public static string NormalizeNewlines(string s)
            => string.IsNullOrEmpty(s) ? (s ?? "") : s.Replace("\r\n", "\n").Replace("\r", "\n");

        // Jeden wzorzec zmiennej {{klucz}} dla podstawiania i walidacji (kolorowanie pól w konsoli).
        private static readonly Regex VarRx = new Regex(@"\{\{\s*([^{}\s]+)\s*\}\}", RegexOptions.Compiled);

        // Podstawia {{klucz}} wartościami zmiennych (nieznane {{x}} zostają bez zmian). Publiczne dla testów.
        public static string Subst(string s, IReadOnlyDictionary<string, string> vars)
        {
            if (string.IsNullOrEmpty(s) || vars == null || vars.Count == 0) return s ?? "";
            return VarRx.Replace(s, m => vars.TryGetValue(m.Groups[1].Value, out var v) ? (v ?? "") : m.Value);
        }

        /// <summary>Czy tekst zawiera jakąkolwiek zmienną {{x}}? (sygnalizacja w UI)</summary>
        public static bool HasVars(string s) => !string.IsNullOrEmpty(s) && VarRx.IsMatch(s);

        /// <summary>Nieznane {{zmienne}} w CAŁYM żądaniu (URL, parametry, nagłówki, treść/pola formularza,
        /// auth) względem słownika — takie idą na drut DOSŁOWNIE jako {{x}}, co zwykle kończy się 401/400
        /// (np. literalny {{client_secret}} w body tokenowym przy złym aktywnym środowisku). Skanuje to samo
        /// body, które wyśle <see cref="Build"/> (tabela pól vs surowy tekst; GET/HEAD bez body).
        /// Publiczne dla testów; UI pokazuje ostrzeżenie w zakładce „Wysłane".</summary>
        public static List<string> MissingVarsInRequest(RestRequest req, string authSecret, IReadOnlyDictionary<string, string> vars)
        {
            var missing = new List<string>();
            WalkRequestStrings(req, authSecret, s =>
            {
                foreach (var k in MissingVars(s, vars))
                    if (!missing.Contains(k)) missing.Add(k);
            });
            return missing;
        }

        /// <summary>ZNANE zmienne {{x}} o PUSTEJ wartości użyte w żądaniu — podstawienie „udaje się", ale
        /// wstawia pustkę. Klasyka po imporcie środowiska Postmana (zmienne typu „secret" są czyszczone)
        /// albo zanim skrypt tokenowy pierwszy raz zapisze token. Podstępne, bo kolor pola mówi
        /// „znaleziona" (niebieski), a przy pustym sekrecie Bearer nagłówek Authorization jest po cichu
        /// pomijany. Publiczne dla testów; UI pokazuje ostrzeżenie w zakładce „Wysłane".</summary>
        public static List<string> EmptyVarsInRequest(RestRequest req, string authSecret, IReadOnlyDictionary<string, string> vars)
        {
            var empty = new List<string>();
            if (vars == null || vars.Count == 0) return empty;
            WalkRequestStrings(req, authSecret, s =>
            {
                if (string.IsNullOrEmpty(s)) return;
                foreach (Match m in VarRx.Matches(s))
                {
                    string k = m.Groups[1].Value;
                    if (vars.TryGetValue(k, out var v) && string.IsNullOrEmpty(v) && !empty.Contains(k)) empty.Add(k);
                }
            });
            return empty;
        }

        // Odwiedza wszystkie pola tekstowe żądania, które faktycznie pójdą na drut — lustrzane odbicie
        // decyzji Build (tylko włączone wiersze; tabela pól vs surowy tekst; GET/HEAD bez body; auth).
        // Wspólne dla MissingVarsInRequest i EmptyVarsInRequest, żeby oba audyty nie mogły się rozjechać.
        private static void WalkRequestStrings(RestRequest req, string authSecret, Action<string> visit)
        {
            visit(req.Url);
            foreach (var p in req.QueryParams) if (p.Enabled) { visit(p.Key); visit(p.Value); }
            foreach (var h in req.Headers) if (h.Enabled) { visit(h.Key); visit(h.Value); }

            string m = (req.Method ?? "GET").Trim().ToUpperInvariant();
            if (m != "GET" && m != "HEAD")
            {
                string contentType = req.Headers
                    .Where(h => h.Enabled && string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                    .Select(h => h.Value).FirstOrDefault();
                if (string.IsNullOrWhiteSpace(contentType)) contentType = req.BodyContentType;
                bool isForm = IsFormUrlEncoded(contentType);
                bool hasFields = isForm && req.FormFields.Any(f => f.Enabled && !string.IsNullOrWhiteSpace(f.Key));
                if (hasFields) { foreach (var f in req.FormFields) if (f.Enabled) { visit(f.Key); visit(f.Value); } }
                else visit(req.Body);
            }

            visit(req.AuthUsername);
            visit(authSecret);
        }

        /// <summary>Nazwy zmiennych {{x}} z tekstu, których NIE ma w słowniku (null słownik = wszystkie
        /// nieznane). Pusta lista = wszystko znane albo brak zmiennych. Publiczne dla testów i UI.</summary>
        public static List<string> MissingVars(string s, IReadOnlyDictionary<string, string> vars)
        {
            var missing = new List<string>();
            if (string.IsNullOrEmpty(s)) return missing;
            foreach (Match m in VarRx.Matches(s))
            {
                string k = m.Groups[1].Value;
                if ((vars == null || !vars.ContainsKey(k)) && !missing.Contains(k)) missing.Add(k);
            }
            return missing;
        }

        /// <summary>Buduje docelowy URI: podstawia {{zmienne}}, dokleja włączone parametry zapytania
        /// i uzupełnia schemat (https) gdy brak. Publiczne dla testów.
        /// Parametry z TABELI są kodowane (EscapeDataString) — komórka trzyma wartość ODKODOWANĄ, kodujemy
        /// raz. Ścieżka/query wpisane wprost w polu URL idą DOSŁOWNIE (odpowiedzialność użytkownika):
        /// DangerousDisablePathAndQueryCanonicalization wyłącza kanonikalizację System.Uri, która inaczej
        /// dekodowała %7E→~, zwijała /../, przekłamywała znaki spoza ASCII i ucinała wszystko po # jako
        /// fragment — stąd zgłoszone „zmienione znaki w żądaniu".</summary>
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

            return new Uri(url, new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
        }
    }
}
