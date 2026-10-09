import Foundation

/// Współdzielony profil poświadczeń — ten sam `credprofiles.json` co w Windows (Id, Name, Domain,
/// Username). Jeden login dla wielu serwerów; serwer wskazuje profil przez `CredentialProfileId`.
/// Hasło nigdy tu nie trafia: na Macu jest w Pęku kluczy pod kontem `profile:<Id>`.
public struct CredentialProfile: Codable, Identifiable, Equatable, Sendable {
    public var id: String
    public var name: String
    public var domain: String
    public var username: String
    /// Pola nowszej wersji — zachowane przy odczycie i zapisie (jak `Server.extra`).
    public var extra: [String: JSONValue]

    public init(id: String = Server.newId(), name: String = "", domain: String = "", username: String = "") {
        self.id = id; self.name = name; self.domain = domain; self.username = username; self.extra = [:]
    }

    /// Nazwa na listach: nazwa, a gdy pusta — login.
    public var displayName: String {
        let n = name.trimmingCharacters(in: .whitespaces)
        return n.isEmpty ? login : n
    }

    /// `DOMENA\user` albo `user`.
    public var login: String { domain.isEmpty ? username : domain + "\\" + username }

    /// Konto w Pęku kluczy.
    public var keychainAccount: String { "profile:" + id }

    private struct Key: CodingKey {
        var stringValue: String
        var intValue: Int? { nil }
        init(_ s: String) { stringValue = s }
        init?(stringValue: String) { self.stringValue = stringValue }
        init?(intValue: Int) { nil }
    }
    private static let known: Set<String> = ["Id", "Name", "Domain", "Username"]

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: Key.self)
        func str(_ k: String) -> String { ((try? c.decodeIfPresent(String.self, forKey: Key(k))) ?? nil) ?? "" }
        id = str("Id")
        if id.isEmpty { id = Server.newId() }
        name = str("Name"); domain = str("Domain"); username = str("Username")
        extra = [:]
        for k in c.allKeys where !Self.known.contains(k.stringValue) {
            if let v = try? c.decode(JSONValue.self, forKey: k) { extra[k.stringValue] = v }
        }
    }

    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: Key.self)
        for (k, v) in extra where !Self.known.contains(k) { try c.encode(v, forKey: Key(k)) }
        try c.encode(id, forKey: Key("Id"))
        try c.encode(name, forKey: Key("Name"))
        try c.encode(domain, forKey: Key("Domain"))
        try c.encode(username, forKey: Key("Username"))
    }
}

/// `credprofiles.json` obok listy serwerów; zapis atomowy z kopią `.bak` (jak w Windows).
public struct CredentialProfileStore: Sendable {
    public let directory: URL
    public var fileURL: URL { directory.appendingPathComponent("credprofiles.json") }
    public init(directory: URL) { self.directory = directory }

    public func load() -> [CredentialProfile] {
        for url in [fileURL, directory.appendingPathComponent("credprofiles.json.bak")] {
            if let d = try? Data(contentsOf: url), let list = try? Self.decode(d) { return list }
        }
        return []
    }

    public func save(_ list: [CredentialProfile]) throws {
        let fm = FileManager.default
        try fm.createDirectory(at: directory, withIntermediateDirectories: true)
        let bak = directory.appendingPathComponent("credprofiles.json.bak")
        if fm.fileExists(atPath: fileURL.path) {
            try? fm.removeItem(at: bak)
            try? fm.copyItem(at: fileURL, to: bak)
        }
        let e = JSONEncoder()
        e.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        try e.encode(list).write(to: fileURL, options: .atomic)
    }

    public static func decode(_ data: Data) throws -> [CredentialProfile] {
        try JSONDecoder().decode([CredentialProfile].self, from: data)
    }

    /// Dołącza profile z pliku Windows: ten sam Id podmienia wpis, nowe dochodzą na końcu.
    public static func merge(_ existing: [CredentialProfile], _ imported: [CredentialProfile]) -> (list: [CredentialProfile], added: Int, updated: Int) {
        var list = existing
        var added = 0, updated = 0
        for p in imported {
            if let i = list.firstIndex(where: { $0.id == p.id }) { list[i] = p; updated += 1 }
            else { list.append(p); added += 1 }
        }
        return (list, added, updated)
    }
}

extension Server {
    /// Id profilu poświadczeń (pole Windows `CredentialProfileId`); pusty = własny login serwera.
    public var credentialProfileId: String {
        get { if case .string(let s)? = extra["CredentialProfileId"] { return s } else { return "" } }
        set {
            let v = newValue.trimmingCharacters(in: .whitespaces)
            if v.isEmpty { extra.removeValue(forKey: "CredentialProfileId") } else { extra["CredentialProfileId"] = .string(v) }
        }
    }

    /// Konto w Pęku kluczy, z którego bierze się hasło logowania: profilu, gdy serwer go wskazuje,
    /// inaczej własne (id serwera). Działa na serwerze już rozwiązanym przez `Credentials.resolve`.
    public var keychainAccount: String {
        credentialProfileId.isEmpty ? id : "profile:" + credentialProfileId
    }
}

/// Rozwiązanie loginu przy łączeniu — odpowiednik `ConnectIdentity` / `Eff*` z Windows.
public enum Credentials {
    /// Kopia serwera z loginem i domeną z profilu (jeśli wskazany profil istnieje). Wskazanie
    /// nieistniejącego profilu (usunięty) jest zdejmowane — łączymy własnym loginem serwera.
    public static func resolve(_ s: Server, profiles: [CredentialProfile]) -> Server {
        var c = s
        guard !s.credentialProfileId.isEmpty else { return c }
        guard let p = profiles.first(where: { $0.id == s.credentialProfileId }) else {
            c.credentialProfileId = ""
            return c
        }
        c.username = p.username
        c.domain = p.domain
        return c
    }

    /// „Połącz jako…": jednorazowy login zamiast profilu i loginu serwera.
    public static func connectAs(_ s: Server, user: String, domain: String) -> Server {
        var c = s
        c.credentialProfileId = ""
        c.username = user.trimmingCharacters(in: .whitespaces)
        c.domain = domain.trimmingCharacters(in: .whitespaces)
        return c
    }

    /// `DOMENA\user` albo `user@domena` → (user, domena); bez domeny — domena pusta.
    public static func splitLogin(_ text: String) -> (user: String, domain: String) {
        let t = text.trimmingCharacters(in: .whitespaces)
        if let i = t.firstIndex(of: "\\") {
            return (String(t[t.index(after: i)...]), String(t[..<i]))
        }
        return (t, "")
    }

    /// Usunięcie profilu: serwery, które go wskazywały, wracają do własnego loginu.
    public static func detach(_ servers: [Server], profileId: String) -> (servers: [Server], changed: Int) {
        var changed = 0
        let list = servers.map { s -> Server in
            guard s.credentialProfileId == profileId else { return s }
            var c = s
            c.credentialProfileId = ""
            changed += 1
            return c
        }
        return (list, changed)
    }
}

/// Generator sekretów — port `PasswordGen` z Windows: hasła z wybranych klas znaków (co najmniej jeden
/// znak z każdej), tokeny hex, GUID. Losowość kryptograficzna (`SystemRandomNumberGenerator`), bez
/// obciążenia modulo (`Int.random(in:)`).
public enum PasswordGen {
    public static let upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ"
    public static let lower = "abcdefghijklmnopqrstuvwxyz"
    public static let digits = "0123456789"
    public static let symbols = "!@#$%^&*()-_=+[]{};:,.?/"
    /// Znaki mylące wizualnie (O/0, I/l/1 …) — opcjonalnie wykluczane.
    public static let ambiguous = "O0oIl1|`'\"{}[]()/\\;:.,"

    public struct Options: Equatable, Sendable {
        public var length = 20
        public var upper = true, lower = true, digits = true, symbols = true
        public var excludeAmbiguous = false
        public init() {}
    }

    static func filter(_ set: String, _ exclude: Bool) -> String {
        exclude ? String(set.filter { !ambiguous.contains($0) }) : set
    }

    static func classes(_ o: Options) -> [String] {
        var c: [String] = []
        if o.upper { c.append(filter(upper, o.excludeAmbiguous)) }
        if o.lower { c.append(filter(lower, o.excludeAmbiguous)) }
        if o.digits { c.append(filter(digits, o.excludeAmbiguous)) }
        if o.symbols { c.append(filter(symbols, o.excludeAmbiguous)) }
        return c.filter { !$0.isEmpty }
    }

    public static func pool(_ o: Options) -> String { classes(o).joined() }

    public static func password(_ o: Options) -> String {
        var rng = SystemRandomNumberGenerator()
        let cls = classes(o).map(Array.init)
        let all = cls.flatMap { $0 }
        guard o.length > 0, !all.isEmpty else { return "" }
        var chars: [Character] = []
        for c in cls where chars.count < o.length { chars.append(c[Int.random(in: 0..<c.count, using: &rng)]) }
        while chars.count < o.length { chars.append(all[Int.random(in: 0..<all.count, using: &rng)]) }
        chars.shuffle(using: &rng)   // Fisher–Yates na tym samym generatorze
        return String(chars)
    }

    public static func hexToken(bytes: Int) -> String {
        guard bytes > 0 else { return "" }
        var rng = SystemRandomNumberGenerator()
        return (0..<bytes).map { _ in String(format: "%02x", UInt8.random(in: 0...255, using: &rng)) }.joined()
    }

    public static func guid() -> String { UUID().uuidString.lowercased() }

    /// Przybliżona entropia w bitach: długość · log2(rozmiar puli).
    public static func entropyBits(length: Int, poolSize: Int) -> Double {
        guard length > 0, poolSize > 1 else { return 0 }
        return Double(length) * log2(Double(poolSize))
    }
}

extension Server {
    /// Login do podpowiedzi w „Połącz jako…": `DOMENA\user` albo `user`.
    public var loginText: String { domain.isEmpty ? username : domain + "\\" + username }
}
