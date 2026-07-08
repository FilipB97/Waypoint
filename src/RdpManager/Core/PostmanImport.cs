using System;
using System.Collections.Generic;
using System.Text.Json;
using RdpManager.Models;

namespace RdpManager.Core
{
    /// <summary>
    /// Import kolekcji Postman (schemat v2.1) → <see cref="RestCollection"/> (foldery + żądania:
    /// metoda/URL/parametry/nagłówki/treść/auth). Sekrety (token Bearer / hasło Basic) zwracane osobno
    /// i trafiają do Windows Credential Manager (nigdy do JSON). Zmienne kolekcji i auth na poziomie
    /// kolekcji ({{var}}) — poza zakresem PR3 (środowiska = kolejny PR).
    /// </summary>
    public static class PostmanImport
    {
        public sealed class Result
        {
            public string Name = "Postman";
            public RestCollection Collection = new RestCollection();
            /// <summary>Klucz = <see cref="RestRequest.AuthCredTarget"/>, wartość = sekret (token/hasło).</summary>
            public Dictionary<string, string> Secrets = new Dictionary<string, string>();
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

            WalkItems(items, "", res);

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
        /// </summary>
        public static RestEnvironment ParseEnvironment(string json)
        {
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
                env.Variables.Add(new RestVariable { Key = key, Value = secret ? "" : (Str(v, "value") ?? "") });
            }
            return env;
        }

        private static void WalkItems(JsonElement items, string parentFolderId, Result res)
        {
            foreach (var it in items.EnumerateArray())
            {
                if (it.ValueKind != JsonValueKind.Object) continue;

                if (it.TryGetProperty("item", out var children) && children.ValueKind == JsonValueKind.Array)
                {
                    var folder = new RestFolder { Name = Str(it, "name") ?? "Folder", ParentId = parentFolderId };
                    res.Collection.Folders.Add(folder);
                    WalkItems(children, folder.Id, res);
                }
                else if (it.TryGetProperty("request", out var reqEl))
                {
                    res.Collection.Requests.Add(ParseRequest(reqEl, Str(it, "name"), parentFolderId, res));
                    res.RequestCount++;
                }
            }
        }

        private static RestRequest ParseRequest(JsonElement reqEl, string name, string folderId, Result res)
        {
            var req = new RestRequest { Name = string.IsNullOrWhiteSpace(name) ? "Request" : name, FolderId = folderId };
            if (reqEl.ValueKind == JsonValueKind.String) { req.Url = reqEl.GetString(); return req; }   // skrócona forma

            req.Method = (Str(reqEl, "method") ?? "GET").ToUpperInvariant();

            if (reqEl.TryGetProperty("header", out var hs) && hs.ValueKind == JsonValueKind.Array)
                foreach (var h in hs.EnumerateArray())
                    req.Headers.Add(new RestKeyValue { Enabled = !Bool(h, "disabled"), Key = Str(h, "key") ?? "", Value = Str(h, "value") ?? "" });

            if (reqEl.TryGetProperty("url", out var url)) ParseUrl(url, req);
            if (reqEl.TryGetProperty("body", out var body)) ParseBody(body, req);
            if (reqEl.TryGetProperty("auth", out var auth)) ParseAuth(auth, req, res);

            return req;
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
                // Pary klucz=wartość zapisujemy SUROWO (bez EscapeDataString) — inaczej placeholdery {{var}}
                // zamieniłyby się w %7B%7Bvar%7D%7D i podstawianie by nie działało. Kodowanie robi klient
                // przy wysyłce, PO podstawieniu zmiennych (RestClient.BuildFormBody).
                var parts = new List<string>();
                foreach (var kv in ue.EnumerateArray())
                    if (!Bool(kv, "disabled"))
                        parts.Add((Str(kv, "key") ?? "") + "=" + (Str(kv, "value") ?? ""));
                req.Body = string.Join("&", parts);
                req.BodyContentType = "application/x-www-form-urlencoded";
            }
            // formdata / file → pomijamy (brak odwzorowania w modelu v1)
        }

        private static void ParseAuth(JsonElement auth, RestRequest req, Result res)
        {
            switch (Str(auth, "type"))
            {
                case "bearer":
                    req.AuthType = 1;
                    string token = ArrVal(auth, "bearer", "token");
                    if (!string.IsNullOrEmpty(token)) res.Secrets[req.AuthCredTarget] = token;
                    break;
                case "basic":
                    req.AuthType = 2;
                    req.AuthUsername = ArrVal(auth, "basic", "username") ?? "";
                    string pw = ArrVal(auth, "basic", "password");
                    if (!string.IsNullOrEmpty(pw)) res.Secrets[req.AuthCredTarget] = pw;
                    break;
            }
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
