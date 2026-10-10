import Foundation

/// Klient REST — modele w formacie `rest.json` / `environments.json` wersji Windows (PascalCase), więc
/// kolekcje przenoszą się między systemami. Sekrety uwierzytelniania (token, hasło Basic) nigdy nie są
/// w JSON — na Macu leżą w Pęku kluczy (konta `rest:<Id>`, `restfolder:<Id>`, `restcoll:<Id wpisu>`).
struct DynKey: CodingKey {
    var stringValue: String
    var intValue: Int? { nil }
    init(_ s: String) { stringValue = s }
    init?(stringValue: String) { self.stringValue = stringValue }
    init?(intValue: Int) { nil }
}

/// Wspólne dla modeli REST: pobieranie pól z wartością domyślną i przechowywanie nieznanych pól.
extension KeyedDecodingContainer where K == DynKey {
    func str(_ k: String, _ d: String = "") -> String { ((try? decodeIfPresent(String.self, forKey: DynKey(k))) ?? nil) ?? d }
    func int(_ k: String, _ d: Int) -> Int { ((try? decodeIfPresent(Int.self, forKey: DynKey(k))) ?? nil) ?? d }
    func bool(_ k: String, _ d: Bool) -> Bool { ((try? decodeIfPresent(Bool.self, forKey: DynKey(k))) ?? nil) ?? d }
    func list<T: Decodable>(_ k: String) -> [T] { ((try? decodeIfPresent([T].self, forKey: DynKey(k))) ?? nil) ?? [] }
    func extra(except known: Set<String>) -> [String: JSONValue] {
        var e: [String: JSONValue] = [:]
        for k in allKeys where !known.contains(k.stringValue) {
            if let v = try? decode(JSONValue.self, forKey: k) { e[k.stringValue] = v }
        }
        return e
    }
}

extension KeyedEncodingContainer where K == DynKey {
    mutating func put<T: Encodable>(_ v: T, _ k: String) throws { try encode(v, forKey: DynKey(k)) }
    mutating func putExtra(_ e: [String: JSONValue], except known: Set<String>) throws {
        for (k, v) in e where !known.contains(k) { try encode(v, forKey: DynKey(k)) }
    }
}

public struct RestKeyValue: Codable, Equatable, Sendable, Identifiable {
    public var id = UUID()
    public var enabled = true
    public var key = ""
    public var value = ""
    public init(key: String = "", value: String = "", enabled: Bool = true) { self.key = key; self.value = value; self.enabled = enabled }
    public init(from d: Decoder) throws {
        let c = try d.container(keyedBy: DynKey.self)
        enabled = c.bool("Enabled", true); key = c.str("Key"); value = c.str("Value")
    }
    public func encode(to e: Encoder) throws {
        var c = e.container(keyedBy: DynKey.self)
        try c.put(enabled, "Enabled"); try c.put(key, "Key"); try c.put(value, "Value")
    }
    public static func == (a: Self, b: Self) -> Bool { a.enabled == b.enabled && a.key == b.key && a.value == b.value }
}

/// Rodzaj uwierzytelniania — te same liczby co w Windows.
public enum RestAuthType: Int, CaseIterable, Sendable { case none = 0, bearer = 1, basic = 2, inherit = 3 }

public struct RestRequest: Codable, Equatable, Sendable, Identifiable {
    public var id = Server.newId()
    public var name = ""
    public var method = "GET"
    public var url = ""
    public var queryParams: [RestKeyValue] = []
    public var headers: [RestKeyValue] = []
    public var body = ""
    public var bodyContentType = "application/json"
    public var formFields: [RestKeyValue] = []
    public var authType = RestAuthType.inherit.rawValue
    public var authUsername = ""
    public var folderId = ""
    public var preScript = ""
    public var testScript = ""
    public var extra: [String: JSONValue] = [:]

    public init(name: String = "", method: String = "GET", url: String = "") { self.name = name; self.method = method; self.url = url }

    public var keychainAccount: String { "rest:" + id }

    static let known: Set<String> = ["Id", "Name", "Method", "Url", "QueryParams", "Headers", "Body", "BodyContentType",
                                     "FormFields", "AuthType", "AuthUsername", "FolderId", "PreScript", "TestScript"]
    public init(from d: Decoder) throws {
        let c = try d.container(keyedBy: DynKey.self)
        id = c.str("Id"); if id.isEmpty { id = Server.newId() }
        name = c.str("Name"); method = c.str("Method", "GET"); url = c.str("Url")
        queryParams = c.list("QueryParams"); headers = c.list("Headers"); body = c.str("Body")
        bodyContentType = c.str("BodyContentType", "application/json"); formFields = c.list("FormFields")
        authType = c.int("AuthType", 3); authUsername = c.str("AuthUsername"); folderId = c.str("FolderId")
        preScript = c.str("PreScript"); testScript = c.str("TestScript")
        extra = c.extra(except: Self.known)
    }
    public func encode(to e: Encoder) throws {
        var c = e.container(keyedBy: DynKey.self)
        try c.putExtra(extra, except: Self.known)
        try c.put(id, "Id"); try c.put(name, "Name"); try c.put(method, "Method"); try c.put(url, "Url")
        try c.put(queryParams, "QueryParams"); try c.put(headers, "Headers"); try c.put(body, "Body")
        try c.put(bodyContentType, "BodyContentType"); try c.put(formFields, "FormFields")
        try c.put(authType, "AuthType"); try c.put(authUsername, "AuthUsername"); try c.put(folderId, "FolderId")
        try c.put(preScript, "PreScript"); try c.put(testScript, "TestScript")
    }
}

public struct RestFolder: Codable, Equatable, Sendable, Identifiable {
    public var id = Server.newId()
    public var name = ""
    public var parentId = ""
    public var authType = RestAuthType.inherit.rawValue
    public var authUsername = ""
    public init(name: String = "", parentId: String = "") { self.name = name; self.parentId = parentId }
    public var keychainAccount: String { "restfolder:" + id }
    public init(from d: Decoder) throws {
        let c = try d.container(keyedBy: DynKey.self)
        id = c.str("Id"); if id.isEmpty { id = Server.newId() }
        name = c.str("Name"); parentId = c.str("ParentId"); authType = c.int("AuthType", 3); authUsername = c.str("AuthUsername")
    }
    public func encode(to e: Encoder) throws {
        var c = e.container(keyedBy: DynKey.self)
        try c.put(id, "Id"); try c.put(name, "Name"); try c.put(parentId, "ParentId")
        try c.put(authType, "AuthType"); try c.put(authUsername, "AuthUsername")
    }
}

public struct RestVariable: Codable, Equatable, Sendable, Identifiable {
    public var id = UUID()
    public var key = ""
    public var value = ""
    public init(key: String = "", value: String = "") { self.key = key; self.value = value }
    public init(from d: Decoder) throws {
        let c = try d.container(keyedBy: DynKey.self)
        key = c.str("Key"); value = c.str("Value")
    }
    public func encode(to e: Encoder) throws {
        var c = e.container(keyedBy: DynKey.self)
        try c.put(key, "Key"); try c.put(value, "Value")
    }
    public static func == (a: Self, b: Self) -> Bool { a.key == b.key && a.value == b.value }
}

public struct RestEnvironment: Codable, Equatable, Sendable, Identifiable {
    public var id = Server.newId()
    public var name = ""
    public var variables: [RestVariable] = []
    public var extra: [String: JSONValue] = [:]
    public init(name: String = "", variables: [RestVariable] = []) { self.name = name; self.variables = variables }
    static let known: Set<String> = ["Id", "Name", "Variables"]
    public init(from d: Decoder) throws {
        let c = try d.container(keyedBy: DynKey.self)
        id = c.str("Id"); if id.isEmpty { id = Server.newId() }
        name = c.str("Name"); variables = c.list("Variables"); extra = c.extra(except: Self.known)
    }
    public func encode(to e: Encoder) throws {
        var c = e.container(keyedBy: DynKey.self)
        try c.putExtra(extra, except: Self.known)
        try c.put(id, "Id"); try c.put(name, "Name"); try c.put(variables, "Variables")
    }

    /// Słownik do podstawiania {{zmiennych}} (późniejszy wpis o tym samym kluczu wygrywa).
    public var dictionary: [String: String] {
        var d: [String: String] = [:]
        for v in variables where !v.key.trimmingCharacters(in: .whitespaces).isEmpty { d[v.key] = v.value }
        return d
    }
}

public struct RestHistoryEntry: Codable, Equatable, Sendable {
    public var method = "", url = "", whenIso = ""
    public var status = 0
    public var elapsedMs = 0
    public init(method: String, url: String, status: Int, elapsedMs: Int, whenIso: String) {
        self.method = method; self.url = url; self.status = status; self.elapsedMs = elapsedMs; self.whenIso = whenIso
    }
    public init(from d: Decoder) throws {
        let c = try d.container(keyedBy: DynKey.self)
        method = c.str("Method"); url = c.str("Url"); status = c.int("Status", 0); elapsedMs = c.int("ElapsedMs", 0); whenIso = c.str("WhenIso")
    }
    public func encode(to e: Encoder) throws {
        var c = e.container(keyedBy: DynKey.self)
        try c.put(method, "Method"); try c.put(url, "Url"); try c.put(status, "Status")
        try c.put(elapsedMs, "ElapsedMs"); try c.put(whenIso, "WhenIso")
    }
}

/// Kolekcja jednego wpisu REST na liście („wpis = jedno API").
public struct RestCollection: Codable, Equatable, Sendable {
    public static let currentSchemaVersion = 2
    public var schemaVersion = 0
    public var baseUrl = ""
    public var folders: [RestFolder] = []
    public var requests: [RestRequest] = []
    public var environments: [RestEnvironment] = []
    public var activeEnvironmentId = ""
    public var history: [RestHistoryEntry] = []
    public var authType = 0
    public var authUsername = ""
    public var extra: [String: JSONValue] = [:]
    public init() {}

    static let known: Set<String> = ["SchemaVersion", "BaseUrl", "Folders", "Requests", "Environments", "ActiveEnvironmentId",
                                     "History", "AuthType", "AuthUsername"]
    public init(from d: Decoder) throws {
        let c = try d.container(keyedBy: DynKey.self)
        schemaVersion = c.int("SchemaVersion", 0); baseUrl = c.str("BaseUrl"); folders = c.list("Folders")
        requests = c.list("Requests"); environments = c.list("Environments"); activeEnvironmentId = c.str("ActiveEnvironmentId")
        history = c.list("History"); authType = c.int("AuthType", 0); authUsername = c.str("AuthUsername")
        extra = c.extra(except: Self.known)
    }
    public func encode(to e: Encoder) throws {
        var c = e.container(keyedBy: DynKey.self)
        try c.putExtra(extra, except: Self.known)
        try c.put(Self.currentSchemaVersion, "SchemaVersion"); try c.put(baseUrl, "BaseUrl"); try c.put(folders, "Folders")
        try c.put(requests, "Requests"); try c.put(environments, "Environments"); try c.put(activeEnvironmentId, "ActiveEnvironmentId")
        try c.put(history, "History"); try c.put(authType, "AuthType"); try c.put(authUsername, "AuthUsername")
    }

    /// Dziedziczone uwierzytelnianie (jak „Inherit auth from parent" w Postmanie): pierwszy jawny poziom
    /// od folderu `startFolderId` w górę; na końcu korzeń kolekcji. Cykl ParentId nie zawiesza pętli.
    /// Zwraca typ, login i konto sekretu w Pęku kluczy (`collectionAccount` dla korzenia).
    public func resolveAuth(from startFolderId: String, collectionAccount: String) -> (type: Int, username: String, account: String) {
        var folder = folders.first { $0.id == startFolderId }
        var seen = Set<String>()
        while let f = folder {
            if f.authType != RestAuthType.inherit.rawValue { return (f.authType, f.authUsername, f.keychainAccount) }
            guard seen.insert(f.id).inserted else { break }
            folder = folders.first { $0.id == f.parentId }
        }
        return (authType, authUsername, collectionAccount)
    }

    /// Dopisuje wpis historii (najnowszy pierwszy, najwyżej 50 — jak w Windows).
    public mutating func record(_ h: RestHistoryEntry, max: Int = 50) {
        history.insert(h, at: 0)
        if history.count > max { history.removeLast(history.count - max) }
    }
}

/// `rest.json`: kolekcje po Id wpisu (serwera) — zapis atomowy z `.bak`.
public struct RestStore: Sendable {
    public let directory: URL
    public var fileURL: URL { directory.appendingPathComponent("rest.json") }
    public init(directory: URL) { self.directory = directory }

    public func loadAll() -> [String: RestCollection] {
        for u in [fileURL, directory.appendingPathComponent("rest.json.bak")] {
            if let d = try? Data(contentsOf: u), let m = try? JSONDecoder().decode([String: RestCollection].self, from: d) { return m }
        }
        return [:]
    }

    public func collection(for serverId: String) -> RestCollection { loadAll()[serverId] ?? RestCollection() }

    public func put(_ c: RestCollection, for serverId: String) throws {
        var all = loadAll()
        all[serverId] = c
        try save(all)
    }

    public func remove(_ serverId: String) throws {
        var all = loadAll()
        if all.removeValue(forKey: serverId) != nil { try save(all) }
    }

    func save(_ all: [String: RestCollection]) throws {
        try JSONFile.write(all, to: fileURL)
    }
}

/// `environments.json` — środowiska wspólne dla wszystkich kolekcji, aktywne Id w `rest-active-env`.
/// Przy braku pliku jednorazowa migracja ze starych kolekcji (dedup po Id), jak w Windows.
public struct EnvironmentStore: Sendable {
    public let directory: URL
    public init(directory: URL) { self.directory = directory }
    var fileURL: URL { directory.appendingPathComponent("environments.json") }
    var activeURL: URL { directory.appendingPathComponent("rest-active-env") }

    public func load() -> [RestEnvironment] {
        guard FileManager.default.fileExists(atPath: fileURL.path) else {
            var seen = Set<String>()
            let migrated = RestStore(directory: directory).loadAll().values.flatMap(\.environments).filter { seen.insert($0.id).inserted }
            try? save(migrated)
            return migrated
        }
        for u in [fileURL, directory.appendingPathComponent("environments.json.bak")] {
            if let d = try? Data(contentsOf: u), let l = try? JSONDecoder().decode([RestEnvironment].self, from: d) { return l }
        }
        return []
    }

    public func save(_ list: [RestEnvironment]) throws { try JSONFile.write(list, to: fileURL) }

    public var activeId: String {
        get { ((try? String(contentsOf: activeURL, encoding: .utf8)) ?? "").trimmingCharacters(in: .whitespacesAndNewlines) }
        nonmutating set { try? newValue.write(to: activeURL, atomically: true, encoding: .utf8) }
    }
}

enum JSONFile {
    /// Zapis atomowy z kopią poprzedniej wersji jako `.bak`.
    static func write<T: Encodable>(_ v: T, to url: URL) throws {
        let fm = FileManager.default
        try fm.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        let bak = url.appendingPathExtension("bak")
        if fm.fileExists(atPath: url.path) { try? fm.removeItem(at: bak); try? fm.copyItem(at: url, to: bak) }
        let e = JSONEncoder()
        e.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        try e.encode(v).write(to: url, options: .atomic)
    }
}
