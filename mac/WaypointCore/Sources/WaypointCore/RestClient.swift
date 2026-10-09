import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

/// Budowanie i wysyłka żądań REST — port `RestClient` z Windows: {{zmienne}}, parametry z tabeli
/// (kodowane raz), treść JSON/tekst albo formularz, nagłówki, Bearer/Basic, Accept i User-Agent.
public enum RestBuild {
    /// Gotowe żądanie (to, co pójdzie na drut) — widoczne w zakładce „Wysłane".
    public struct Prepared: Equatable, Sendable {
        public var method: String
        public var url: String
        public var headers: [(String, String)]
        public var body: String?

        public static func == (a: Self, b: Self) -> Bool {
            a.method == b.method && a.url == b.url && a.body == b.body
                && a.headers.map { $0.0 + ":" + $0.1 } == b.headers.map { $0.0 + ":" + $0.1 }
        }

        public func header(_ name: String) -> String? {
            headers.first { $0.0.caseInsensitiveCompare(name) == .orderedSame }?.1
        }
    }

    static let varPattern = try! NSRegularExpression(pattern: #"\{\{\s*([^{}\s]+)\s*\}\}"#)

    /// Podstawia {{klucz}} wartościami; nieznane zostają jak są (widać, czego brakuje).
    public static func subst(_ s: String, _ vars: [String: String]) -> String {
        guard !s.isEmpty, !vars.isEmpty else { return s }
        let ns = s as NSString
        var out = ""
        var last = 0
        for m in varPattern.matches(in: s, range: NSRange(location: 0, length: ns.length)) {
            out += ns.substring(with: NSRange(location: last, length: m.range.location - last))
            let key = ns.substring(with: m.range(at: 1))
            out += vars[key] ?? ns.substring(with: m.range)
            last = m.range.location + m.range.length
        }
        return out + ns.substring(from: last)
    }

    public static func variables(in s: String) -> [String] {
        let ns = s as NSString
        var seen: [String] = []
        for m in varPattern.matches(in: s, range: NSRange(location: 0, length: ns.length)) {
            let k = ns.substring(with: m.range(at: 1))
            if !seen.contains(k) { seen.append(k) }
        }
        return seen
    }

    /// Kodowanie jak `Uri.EscapeDataString` (RFC 3986: tylko litery, cyfry i -._~ zostają).
    public static func escape(_ s: String) -> String {
        var allowed = CharacterSet(charactersIn: "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~")
        allowed.remove(charactersIn: "")
        return s.addingPercentEncoding(withAllowedCharacters: allowed) ?? s
    }

    /// URL: zmienne, brakujący schemat → https://, włączone parametry z tabeli doklejone. To, co wpisano
    /// w polu URL, idzie dosłownie (jak w Windows po wyłączeniu kanonikalizacji).
    public static func url(_ r: RestRequest, _ vars: [String: String]) -> String {
        var u = subst(r.url.trimmingCharacters(in: .whitespaces), vars)
        if !u.isEmpty && !u.contains("://") { u = "https://" + u }
        let qp = r.queryParams.filter { $0.enabled && !$0.key.trimmingCharacters(in: .whitespaces).isEmpty }
            .map { escape(subst($0.key, vars)) + "=" + escape(subst($0.value, vars)) }
        if !qp.isEmpty { u += (u.contains("?") ? "&" : "?") + qp.joined(separator: "&") }
        return u
    }

    static func contentType(_ r: RestRequest) -> String {
        let h = r.headers.first { $0.enabled && $0.key.caseInsensitiveCompare("Content-Type") == .orderedSame }?.value ?? ""
        return h.trimmingCharacters(in: .whitespaces).isEmpty ? r.bodyContentType : h
    }

    static func isForm(_ ct: String) -> Bool { ct.lowercased().contains("x-www-form-urlencoded") }

    public static func formBody(fields: [RestKeyValue], _ vars: [String: String]) -> String {
        fields.filter { $0.enabled && !$0.key.trimmingCharacters(in: .whitespaces).isEmpty }
            .map { escape(subst($0.key, vars)) + "=" + escape(subst($0.value, vars)) }.joined(separator: "&")
    }

    /// Surowy tekst `a=1&b=2` (starszy format) — każda część kodowana po podstawieniu.
    public static func formBody(raw: String, _ vars: [String: String]) -> String {
        raw.split(separator: "&", omittingEmptySubsequences: true).map { seg -> String in
            guard let eq = seg.firstIndex(of: "=") else { return escape(subst(String(seg), vars)) }
            return escape(subst(String(seg[..<eq]), vars)) + "=" + escape(subst(String(seg[seg.index(after: eq)...]), vars))
        }.joined(separator: "&")
    }

    /// `authType`/`username`/`secret` — już rozwiązane (dziedziczenie po folderze/kolekcji).
    public static func prepare(_ r: RestRequest, vars: [String: String], authType: Int, username: String, secret: String,
                               userAgent: String = "Waypoint-mac") -> Prepared {
        let method = r.method.trimmingCharacters(in: .whitespaces).uppercased().isEmpty ? "GET"
            : r.method.trimmingCharacters(in: .whitespaces).uppercased()
        var headers: [(String, String)] = []
        var body: String?
        let ct = contentType(r)
        let form = isForm(ct)
        let hasFields = form && r.formFields.contains { $0.enabled && !$0.key.trimmingCharacters(in: .whitespaces).isEmpty }
        if method != "GET" && method != "HEAD" && (hasFields || !r.body.isEmpty) {
            body = !form ? subst(r.body, vars).replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n")
                 : hasFields ? formBody(fields: r.formFields, vars) : formBody(raw: r.body, vars)
            if !ct.trimmingCharacters(in: .whitespaces).isEmpty {
                var v = subst(ct, vars)
                if !form && !v.lowercased().contains("charset") { v += "; charset=utf-8" }
                headers.append(("Content-Type", v))
            }
        }
        for h in r.headers where h.enabled && !h.key.trimmingCharacters(in: .whitespaces).isEmpty
            && h.key.caseInsensitiveCompare("Content-Type") != .orderedSame {
            headers.append((h.key, subst(h.value, vars)))
        }
        let sec = subst(secret, vars)
        if authType == RestAuthType.bearer.rawValue && !sec.isEmpty {
            headers.append(("Authorization", "Bearer " + sec))
        } else if authType == RestAuthType.basic.rawValue {
            headers.append(("Authorization", "Basic " + Data((subst(username, vars) + ":" + sec).utf8).base64EncodedString()))
        }
        if !headers.contains(where: { $0.0.caseInsensitiveCompare("Accept") == .orderedSame }) { headers.append(("Accept", "*/*")) }
        if !headers.contains(where: { $0.0.caseInsensitiveCompare("User-Agent") == .orderedSame }) { headers.append(("User-Agent", userAgent)) }
        return Prepared(method: method, url: url(r, vars), headers: headers, body: body)
    }

    /// Zmienne użyte w polach, które pójdą na drut, a których nie ma w środowisku / które są puste.
    public static func audit(_ r: RestRequest, secret: String, username: String, vars: [String: String]) -> (missing: [String], empty: [String]) {
        var texts = [r.url, username, secret]
        for p in r.queryParams where p.enabled { texts += [p.key, p.value] }
        for h in r.headers where h.enabled { texts += [h.key, h.value] }
        let m = r.method.uppercased()
        if m != "GET" && m != "HEAD" {
            let form = isForm(contentType(r))
            if form && r.formFields.contains(where: { $0.enabled && !$0.key.isEmpty }) {
                for f in r.formFields where f.enabled { texts += [f.key, f.value] }
            } else { texts.append(r.body) }
        }
        var missing: [String] = [], empty: [String] = []
        for t in texts {
            for k in variables(in: t) {
                if let v = vars[k] { if v.isEmpty && !empty.contains(k) { empty.append(k) } }
                else if !missing.contains(k) { missing.append(k) }
            }
        }
        return (missing, empty)
    }
}

public struct RestResponse: Sendable {
    public var ok = false
    public var status = 0
    public var reason = ""
    public var elapsedMs = 0
    public var size = 0
    public var body = ""
    public var contentType = ""
    public var headers: [(String, String)] = []
    public var error = ""
    public var sent: RestBuild.Prepared?
    public init() {}

    public func header(_ name: String) -> String {
        headers.first { $0.0.caseInsensitiveCompare(name) == .orderedSame }?.1 ?? ""
    }

    /// Treść sformatowana, jeśli to JSON (wcięcia 2 spacje); inaczej bez zmian.
    public var prettyBody: String { RestHTTP.pretty(body) }
}

public enum RestHTTP {
    public static let maxResponseBytes = 20 * 1024 * 1024

    public static func send(_ p: RestBuild.Prepared, timeout: TimeInterval = 60) async -> RestResponse {
        var r = RestResponse()
        r.sent = p
        guard let url = URL(string: p.url), url.scheme != nil, url.host != nil else {
            r.error = "Bad URL: \(p.url)"
            return r
        }
        var req = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalAndRemoteCacheData, timeoutInterval: timeout)
        req.httpMethod = p.method
        for (k, v) in p.headers { req.addValue(v, forHTTPHeaderField: k) }
        if let b = p.body { req.httpBody = Data(b.utf8) }
        let start = Date()
        do {
            let (data, resp) = try await URLSession.shared.data(for: req)
            r.elapsedMs = Int(Date().timeIntervalSince(start) * 1000)
            guard let http = resp as? HTTPURLResponse else { r.error = "No HTTP response"; return r }
            r.status = http.statusCode
            r.reason = HTTPURLResponse.localizedString(forStatusCode: http.statusCode)
            r.headers = http.allHeaderFields.compactMap { k, v in (k as? String).map { ($0, "\(v)") } }
                .sorted { $0.0.lowercased() < $1.0.lowercased() }
            r.contentType = r.header("Content-Type")
            r.size = data.count
            if data.count > maxResponseBytes {
                r.error = String(format: "Response too large (%.1f MB) — limit is 20 MB. Not displayed.", Double(data.count) / 1_048_576)
                return r
            }
            r.body = decode(data, contentType: r.contentType)
            r.ok = true
        } catch {
            r.elapsedMs = Int(Date().timeIntervalSince(start) * 1000)
            r.error = (error as NSError).code == NSURLErrorTimedOut ? "Timeout" : error.localizedDescription
        }
        return r
    }

    static func decode(_ data: Data, contentType: String) -> String {
        let lower = contentType.lowercased()
        if let r = lower.range(of: "charset=") {
            let cs = lower[r.upperBound...].split(separator: ";").first.map { $0.trimmingCharacters(in: CharacterSet(charactersIn: "\" ")) } ?? ""
            let map: [String: String.Encoding] = [
                "utf-8": .utf8, "utf8": .utf8, "iso-8859-1": .isoLatin1, "latin1": .isoLatin1, "iso-8859-2": .isoLatin2,
                "windows-1250": .windowsCP1250, "windows-1252": .windowsCP1252, "us-ascii": .ascii, "utf-16": .utf16,
            ]
            if let enc = map[cs], let s = String(data: data, encoding: enc) { return s }
        }
        return String(decoding: data, as: UTF8.self)
    }

    public static func pretty(_ s: String) -> String {
        let t = s.trimmingCharacters(in: .whitespacesAndNewlines)
        guard t.hasPrefix("{") || t.hasPrefix("["), let d = t.data(using: .utf8),
              let o = try? JSONSerialization.jsonObject(with: d, options: [.fragmentsAllowed]),
              let out = try? JSONSerialization.data(withJSONObject: o, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]),
              let str = String(data: out, encoding: .utf8) else { return s }
        return str
    }
}

/// Import kolekcji i środowisk Postmana (schemat v2.1) — port `PostmanImport` z Windows: foldery, żądania,
/// parametry, nagłówki (domyślne nagłówki kolekcji/folderów spłaszczone na żądania), treść raw/urlencoded,
/// Bearer/Basic na każdym poziomie, skrypty pre-request/test, zmienne kolekcji → środowisko.
public enum PostmanImport {
    public struct Result: Sendable {
        public var name = "Postman"
        public var collection = RestCollection()
        /// Konto w Pęku kluczy (rest:<id> / restfolder:<id>) → sekret.
        public var secrets: [String: String] = [:]
        /// Sekret auth całej kolekcji (konto znane dopiero po utworzeniu wpisu).
        public var collectionSecret: String?
        public var requestCount = 0
    }

    public enum Failure: Error { case notCollection, notEnvironment }

    public static func parse(_ data: Data) throws -> Result {
        guard let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any], let items = root["item"] as? [Any] else {
            throw Failure.notCollection
        }
        var r = Result()
        if let n = (root["info"] as? [String: Any])?["name"] as? String, !n.trimmingCharacters(in: .whitespaces).isEmpty { r.name = n }
        if let a = root["auth"] as? [String: Any] {
            let (t, u, s) = readAuth(a)
            if t >= 0 { r.collection.authType = t; r.collection.authUsername = u }
            if let s, !s.isEmpty { r.collectionSecret = s }
        }
        walk(items, folderId: "", inherited: headers(root), into: &r)
        if let vars = root["variable"] as? [[String: Any]] {
            var env = RestEnvironment(name: r.name)
            for v in vars {
                guard let k = v["key"] as? String, !k.trimmingCharacters(in: .whitespaces).isEmpty else { continue }
                env.variables.append(RestVariable(key: k, value: v["value"] as? String ?? ""))
            }
            if !env.variables.isEmpty { r.collection.environments.append(env); r.collection.activeEnvironmentId = env.id }
        }
        return r
    }

    public static func looksLikeEnvironment(_ data: Data) -> Bool {
        guard let o = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return false }
        return o["item"] == nil && o["values"] is [Any]
    }

    /// Środowisko Postmana; wartości typu „secret" są czyszczone (lista kluczy do ostrzeżenia).
    public static func parseEnvironment(_ data: Data) throws -> (env: RestEnvironment, blankedSecrets: [String]) {
        guard let o = try? JSONSerialization.jsonObject(with: data) as? [String: Any], let values = o["values"] as? [[String: Any]] else {
            throw Failure.notEnvironment
        }
        var env = RestEnvironment(name: o["name"] as? String ?? "Postman")
        var blanked: [String] = []
        for v in values {
            guard let k = v["key"] as? String, !k.trimmingCharacters(in: .whitespaces).isEmpty else { continue }
            let secret = (v["type"] as? String)?.lowercased() == "secret"
            if secret { blanked.append(k) }
            env.variables.append(RestVariable(key: k, value: secret ? "" : (v["value"] as? String ?? "")))
        }
        return (env, blanked)
    }

    static func walk(_ items: [Any], folderId: String, inherited: [RestKeyValue], into r: inout Result) {
        for case let it as [String: Any] in items {
            if let children = it["item"] as? [Any] {
                var f = RestFolder(name: it["name"] as? String ?? "Folder", parentId: folderId)
                if let a = it["auth"] as? [String: Any] {
                    let (t, u, s) = readAuth(a)
                    if t >= 0 { f.authType = t; f.authUsername = u }
                    if let s, !s.isEmpty { r.secrets[f.keychainAccount] = s }
                }
                r.collection.folders.append(f)
                walk(children, folderId: f.id, inherited: inherited + headers(it), into: &r)
            } else if let reqEl = it["request"] {
                var req = request(reqEl, name: it["name"] as? String ?? "", folderId: folderId, into: &r)
                let have = Set(req.headers.map { $0.key.lowercased() }.filter { !$0.isEmpty })
                var added = have
                for d in inherited where !d.key.isEmpty && added.insert(d.key.lowercased()).inserted { req.headers.append(d) }
                events(it, &req)
                r.collection.requests.append(req)
                r.requestCount += 1
            }
        }
    }

    static func request(_ el: Any, name: String, folderId: String, into r: inout Result) -> RestRequest {
        var req = RestRequest(name: name.trimmingCharacters(in: .whitespaces).isEmpty ? "Request" : name)
        req.folderId = folderId
        if let s = el as? String { req.url = s; return req }
        guard let o = el as? [String: Any] else { return req }
        req.method = (o["method"] as? String ?? "GET").uppercased()
        req.headers = headers(o)
        if let u = o["url"] as? String { req.url = u }
        else if let u = o["url"] as? [String: Any] {
            let raw = u["raw"] as? String ?? ""
            let q = (u["query"] as? [[String: Any]] ?? []).map {
                RestKeyValue(key: $0["key"] as? String ?? "", value: $0["value"] as? String ?? "", enabled: !flag($0["disabled"]))
            }
            if q.isEmpty { req.url = raw } else { req.url = raw.split(separator: "?", maxSplits: 1).first.map(String.init) ?? ""; req.queryParams = q }
        }
        if let b = o["body"] as? [String: Any] {
            switch b["mode"] as? String {
            case "raw":
                req.body = b["raw"] as? String ?? ""
                let lang = ((b["options"] as? [String: Any])?["raw"] as? [String: Any])?["language"] as? String
                req.bodyContentType = lang == "json" ? "application/json" : lang == "xml" ? "application/xml"
                    : lang == "html" ? "text/html"
                    : req.headers.first { $0.enabled && $0.key.lowercased() == "content-type" }?.value ?? "text/plain"
            case "urlencoded":
                // Surowo (bez kodowania) — inaczej {{var}} zamieniłoby się w %7B%7Bvar%7D%7D.
                let fields = (b["urlencoded"] as? [[String: Any]] ?? []).map {
                    RestKeyValue(key: $0["key"] as? String ?? "", value: $0["value"] as? String ?? "", enabled: !flag($0["disabled"]))
                }
                req.formFields = fields
                req.body = fields.filter(\.enabled).map { $0.key + "=" + $0.value }.joined(separator: "&")
                req.bodyContentType = "application/x-www-form-urlencoded"
            default: break   // formdata / file — bez odwzorowania
            }
        }
        if let a = o["auth"] as? [String: Any] {
            let (t, u, s) = readAuth(a)
            if t >= 0 { req.authType = t; req.authUsername = u }
            if let s, !s.isEmpty { r.secrets[req.keychainAccount] = s }
        }
        return req
    }

    static func headers(_ o: [String: Any]) -> [RestKeyValue] {
        (o["header"] as? [[String: Any]] ?? []).map {
            RestKeyValue(key: $0["key"] as? String ?? "", value: $0["value"] as? String ?? "", enabled: !flag($0["disabled"]))
        }
    }

    static func events(_ it: [String: Any], _ req: inout RestRequest) {
        for ev in it["event"] as? [[String: Any]] ?? [] {
            let code = ((ev["script"] as? [String: Any])?["exec"] as? [Any] ?? []).compactMap { $0 as? String }.joined(separator: "\n")
            guard !code.isEmpty else { continue }
            switch ev["listen"] as? String {
            case "prerequest": req.preScript = code
            case "test": req.testScript = code
            default: break
            }
        }
    }

    /// (typ, login, sekret); typ -1 = dziedzicz/nieobsługiwany (zostaw domyślny).
    static func readAuth(_ a: [String: Any]) -> (Int, String, String?) {
        func val(_ arr: String, _ key: String) -> String? {
            (a[arr] as? [[String: Any]])?.first { $0["key"] as? String == key }?["value"] as? String
        }
        switch a["type"] as? String {
        case "bearer": return (1, "", val("bearer", "token"))
        case "basic": return (2, val("basic", "username") ?? "", val("basic", "password"))
        case "noauth": return (0, "", nil)
        default: return (-1, "", nil)
        }
    }

    static func flag(_ v: Any?) -> Bool { (v as? Bool) == true || (v as? String) == "true" }
}
