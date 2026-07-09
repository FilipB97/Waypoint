using System;
using System.Collections.Generic;
using System.Text.Json;
using RdpManager.Models;

namespace RdpManager.Core
{
    /// <summary>
    /// Import kolekcji Postman (schemat v2.1) → <see cref="RestCollection"/> (foldery + żądania:
    /// metoda/URL/parametry/nagłówki/treść/auth). Sekrety (token Bearer / hasło Basic) zwracane osobno
    /// i trafiają do Windows Credential Manager (nigdy do JSON). Auth z poziomu kolekcji i folderu jest
    /// importowany (żądania „Inherit" faktycznie dziedziczą), a domyślne nagłówki kolekcji/folderów są
    /// spłaszczane na każde żądanie. Zmienne kolekcji ({{var}}) trafiają do środowiska.
    /// </summary>
    public static class PostmanImport
    {
        public sealed class Result
        {
            public string Name = "Postman";
            public RestCollection Collection = new RestCollection();
            /// <summary>Klucz = <see cref="RestRequest.AuthCredTarget"/> lub <see cref="RestFolder.AuthCredTarget"/>,
            /// wartość = sekret (token/hasło).</summary>
            public Dictionary<string, string> Secrets = new Dictionary<string, string>();
            /// <summary>Sekret auth CAŁEJ kolekcji (token/hasło). Cel w Credential Manager to
            /// „RdpManager:restcoll:&lt;serverId&gt;", ale serverId powstaje dopiero przy tworzeniu wpisu —
            /// wołający (MainWindow.ImportPostman_Click) zapisuje go, gdy zna już Id.</summary>
            public string CollectionSecret;
            public int RequestCount;
        }

        public static Result Parse(string json)
        {
            var res = new Result();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("item", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("To nie wygląda na kolekcję Postman (brak tablicy 'item').");

            string name = Str(root.TryGetProperty("info", out var info) ? info : default, "name");
            if (!string.IsNullOrWhiteSpace(name)) res.Name = name;

            // Auth na poziomie kolekcji (korzeń dziedziczenia). Sekret bez celu — serverId powstanie później.
            if (root.TryGetProperty("auth", out var cauth))
            {
                var (t, u, sec) = ReadAuth(cauth);
                if (t >= 0) { res.Collection.AuthType = t; res.Collection.AuthUsername = u; }
                if (!string.IsNullOrEmpty(sec)) res.CollectionSecret = sec;
            }

            // Domyślne nagłówki kolekcji (rzadkie w eksporcie, ale bywają) dziedziczą wszystkie żądania.
            WalkItems(items, "", res, ReadHeaders(root));

            // Zmienne kolekcji ({{...}}) → środowisko. Jawne (jak w eksporcie Postmana) — nie na sekrety.
            if (root.TryGetProperty("variable", out var vars) && vars.ValueKind == JsonValueKind.Array)
            {
                var env = new RestEnvironment { Name = res.Name };
                foreach (var v in vars.EnumerateArray())
                {
                    string key = Str(v, "key");
                    if (!string.IsNullOrWhiteSpace(key)) env.Variables.Add(new RestVariable { Key = key, Value = Str(v, "value") ?? "" });
                }
                if (env.Variables.Count > 0)
                {
                    res.Collection.Environments.Add(env);
                    res.Collection.ActiveEnvironmentId = env.Id;
                }
            }
            return res;
        }

        /// <summary>
        /// Import osobnego pliku środowiska Postman (eksport „environment", z tablicą „values") → <see cref="RestEnvironment"/>.
        /// Wartości typu „secret" wczytujemy z PUSTĄ wartością (sekrety nie trafiają do jawnego rest.json — patrz zakładka Auth).
        /// <paramref name="blankedSecretKeys"/> = klucze zmiennych, których wartość wyczyszczono (do ostrzeżenia w UI —
        /// inaczej użytkownik po cichu dostaje pustą zmienną tam, gdzie w Postmanie była realna wartość).
        /// </summary>
        /// <summary>
        /// Czy JSON wygląda na eksport ŚRODOWISKA Postmana (obiekt z „values", bez „item")? Rozróżnia
        /// env od kolekcji przy wspólnej karcie importu „kolekcje i środowiska". false dla nie-JSON.
        /// </summary>
        public static bool LooksLikeEnvironment(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                return root.ValueKind == JsonValueKind.Object
                    && !root.TryGetProperty("item", out _)
                    && root.TryGetProperty("values", out var values)
                    && values.ValueKind == JsonValueKind.Array;
            }
            catch { return false; }
        }

        public static RestEnvironment ParseEnvironment(string json, out List<string> blankedSecretKeys)
        {
            blankedSecretKeys = new List<string>();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("To nie wygląda na środowisko Postman (brak tablicy 'values').");

            var env = new RestEnvironment { Name = Str(root, "name") ?? "Postman" };
            foreach (var v in values.EnumerateArray())
            {
                string key = Str(v, "key");
                if (string.IsNullOrWhiteSpace(key)) continue;
                bool secret = string.Equals(Str(v, "type"), "secret", StringComparison.OrdinalIgnoreCase);
                if (secret) blankedSecretKeys.Add(key);
                env.Variables.Add(new RestVariable { Key = key, Value = secret ? "" : (Str(v, "value") ?? "") });
            }
            return env;
        }

        private static void WalkItems(JsonElement items, string parentFolderId, Result res, List<RestKeyValue> inheritedHeaders)
        {
            foreach (var it in items.EnumerateArray())
            {
                if (it.ValueKind != JsonValueKind.Object) continue;

                if (it.TryGetProperty("item", out var children) && children.ValueKind == JsonValueKind.Array)
                {
                    var folder = new RestFolder { Name = Str(it, "name") ?? "Folder", ParentId = parentFolderId };
                    // Auth folderu (poziom pośredni w dziedziczeniu żądanie → folder → kolekcja).
                    if (it.TryGetProperty("auth", out var fauth))
                    {
                        var (t, u, sec) = ReadAuth(fauth);
                        if (t >= 0) { folder.AuthType = t; folder.AuthUsername = u; }
                        if (!string.IsNullOrEmpty(sec)) res.Secrets[folder.AuthCredTarget] = sec;
                    }
                    res.Collection.Folders.Add(folder);
                    // Nagłówki folderu dziedziczą jego podelementy (kolekcja → folder → podfolder → żądanie).
                    var childHeaders = new List<RestKeyValue>(inheritedHeaders);
                    childHeaders.AddRange(ReadHeaders(it));
                    WalkItems(children, folder.Id, res, childHeaders);
                }
                else if (it.TryGetProperty("request", out var reqEl))
                {
                    var req = ParseRequest(reqEl, Str(it, "name"), parentFolderId, res);
                    MergeDefaultHeaders(req, inheritedHeaders);
                    ParseEvents(it, req);
                    res.Collection.Requests.Add(req);
                    res.RequestCount++;
                }
            }
        }

        private static RestRequest ParseRequest(JsonElement reqEl, string name, string folderId, Result res)
        {
            var req = new RestRequest { Name = string.IsNullOrWhiteSpace(name) ? "Request" : name, FolderId = folderId };
            if (reqEl.ValueKind == JsonValueKind.String) { req.Url = reqEl.GetString(); return req; }   // skrócona forma

            req.Method = (Str(reqEl, "method") ?? "GET").ToUpperInvariant();

            req.Headers.AddRange(ReadHeaders(reqEl));

            if (reqEl.TryGetProperty("url", out var url)) ParseUrl(url, req);
            if (reqEl.TryGetProperty("body", out var body)) ParseBody(body, req);
            if (reqEl.TryGetProperty("auth", out var auth))
            {
                var (t, u, sec) = ReadAuth(auth);
                if (t >= 0) { req.AuthType = t; req.AuthUsername = u; }   // t==-1 (inherit/nieznany) → zostaw domyślny 3
                if (!string.IsNullOrEmpty(sec)) res.Secrets[req.AuthCredTarget] = sec;
            }

            return req;
        }

        // Czyta tablicę „header" elementu (żądanie/folder/kolekcja) na listę par klucz/wartość.
        private static List<RestKeyValue> ReadHeaders(JsonElement el)
        {
            var list = new List<RestKeyValue>();
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("header", out var hs) && hs.ValueKind == JsonValueKind.Array)
                foreach (var h in hs.EnumerateArray())
                    list.Add(new RestKeyValue { Enabled = !Bool(h, "disabled"), Key = Str(h, "key") ?? "", Value = Str(h, "value") ?? "" });
            return list;
        }

        // Dokłada domyślne nagłówki (odziedziczone z kolekcji/folderów) do żądania — tylko te, których żądanie
        // samo nie ma (dopasowanie po nazwie, bez wielkości liter; nagłówek żądania wygrywa).
        private static void MergeDefaultHeaders(RestRequest req, List<RestKeyValue> defaults)
        {
            if (defaults == null || defaults.Count == 0) return;
            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in req.Headers) if (!string.IsNullOrWhiteSpace(h.Key)) have.Add(h.Key);
            foreach (var d in defaults)
                if (!string.IsNullOrWhiteSpace(d.Key) && have.Add(d.Key))
                    req.Headers.Add(new RestKeyValue { Enabled = d.Enabled, Key = d.Key, Value = d.Value });
        }

        private static void ParseUrl(JsonElement url, RestRequest req)
        {
            if (url.ValueKind == JsonValueKind.String) { req.Url = url.GetString() ?? ""; return; }

            string raw = Str(url, "raw") ?? "";
            var query = new List<RestKeyValue>();
            if (url.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.Array)
                foreach (var p in q.EnumerateArray())
                    query.Add(new RestKeyValue { Enabled = !Bool(p, "disabled"), Key = Str(p, "key") ?? "", Value = Str(p, "value") ?? "" });

            // Gdy mamy parametry osobno, URL trzymamy bez query (unikamy duplikacji); inaczej zostawiamy raw w całości.
            if (query.Count > 0) { req.Url = StripQuery(raw); req.QueryParams.AddRange(query); }
            else req.Url = raw;
        }

        private static void ParseBody(JsonElement body, RestRequest req)
        {
            string mode = Str(body, "mode");
            if (mode == "raw")
            {
                req.Body = Str(body, "raw") ?? "";
                string lang = null;
                if (body.TryGetProperty("options", out var opt) && opt.ValueKind == JsonValueKind.Object
                    && opt.TryGetProperty("raw", out var rawOpt))
                    lang = Str(rawOpt, "language");
                req.BodyContentType = lang == "json" ? "application/json"
                                    : lang == "xml" ? "application/xml"
                                    : lang == "html" ? "text/html"
                                    : ContentTypeFromHeaders(req) ?? "text/plain";
            }
            else if (mode == "urlencoded" && body.TryGetProperty("urlencoded", out var ue) && ue.ValueKind == JsonValueKind.Array)
            {
                // Pola trafiają do FormFields (tabela klucz/wartość — edytor) SUROWO (bez EscapeDataString),
                // inaczej placeholdery {{var}} zamieniłyby się w %7B%7Bvar%7D%7D i podstawianie by nie działało.
                // Kodowanie robi klient przy wysyłce, PO podstawieniu zmiennych. Body (starszy surowy format,
                // pomija wyłączone) trzymany dla kompatybilności wstecznej — RestClient preferuje FormFields.
                var parts = new List<string>();
                foreach (var kv in ue.EnumerateArray())
                {
                    bool enabled = !Bool(kv, "disabled");
                    string k = Str(kv, "key") ?? "", v = Str(kv, "value") ?? "";
                    req.FormFields.Add(new RestKeyValue { Enabled = enabled, Key = k, Value = v });
                    if (enabled) parts.Add(k + "=" + v);
                }
                req.Body = string.Join("&", parts);
                req.BodyContentType = "application/x-www-form-urlencoded";
            }
            // formdata / file → pomijamy (brak odwzorowania w modelu v1)
        }

        // Czyta blok Postman „auth" na (typ, login, sekret). Wspólne dla żądania/folderu/kolekcji.
        // typ: -1 = inherit/nieznany (nie zmieniaj domyślnego), 0 = brak, 1 = Bearer, 2 = Basic.
        private static (int type, string username, string secret) ReadAuth(JsonElement auth)
        {
            switch (Str(auth, "type"))
            {
                case "bearer": return (1, "", ArrVal(auth, "bearer", "token"));
                case "basic":  return (2, ArrVal(auth, "basic", "username") ?? "", ArrVal(auth, "basic", "password"));
                case "noauth": return (0, "", null);
                default:       return (-1, "", null);   // inherit / apikey / oauth2 / brak — zostaw domyślny typ
            }
        }

        // Skrypty pre-request/test z poziomu elementu Postmana (event[] jest siostrą "request", nie jej częścią).
        // "listen" == "prerequest"/"test"; linie script.exec[] łączone \n. Kod, nie sekret — trafia wprost do JSON.
        private static void ParseEvents(JsonElement item, RestRequest req)
        {
            if (!item.TryGetProperty("event", out var events) || events.ValueKind != JsonValueKind.Array) return;
            foreach (var ev in events.EnumerateArray())
            {
                string listen = Str(ev, "listen");
                if (listen != "prerequest" && listen != "test") continue;
                if (!ev.TryGetProperty("script", out var script)) continue;
                string code = JoinExec(script);
                if (string.IsNullOrEmpty(code)) continue;
                if (listen == "prerequest") req.PreScript = code;
                else req.TestScript = code;
            }
        }

        private static string JoinExec(JsonElement script)
        {
            if (!script.TryGetProperty("exec", out var exec) || exec.ValueKind != JsonValueKind.Array) return "";
            var lines = new List<string>();
            foreach (var l in exec.EnumerateArray())
                if (l.ValueKind == JsonValueKind.String) lines.Add(l.GetString() ?? "");
            return string.Join("\n", lines);
        }

        // ---------- pomocnicze ----------

        private static string Str(JsonElement e, string prop)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static bool Bool(JsonElement e, string prop)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(prop, out var v)) return false;
            return v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && v.GetString() == "true");
        }

        // Znajduje w tablicy Postmana (np. "bearer"/"basic") element {key,value} o danym kluczu i zwraca jego value.
        private static string ArrVal(JsonElement obj, string arrName, string key)
        {
            if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(arrName, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var el in arr.EnumerateArray())
                    if (Str(el, "key") == key) return Str(el, "value");
            return null;
        }

        private static string StripQuery(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            int i = url.IndexOf('?');
            return i >= 0 ? url.Substring(0, i) : url;
        }

        private static string ContentTypeFromHeaders(RestRequest req)
        {
            foreach (var h in req.Headers)
                if (h.Enabled && string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                    return h.Value;
            return null;
        }
    }
}
