import Foundation

/// Snippet komendy — ten sam format co `snippets.json` w wersji Windows (Id, Name, Command, SendEnter),
/// więc plik można przenieść między maszynami.
public struct CommandSnippet: Codable, Identifiable, Equatable, Sendable {
    public var id: String
    public var name: String
    /// Treść; może być wielowierszowa (każdy wiersz jak osobno wpisany).
    public var command: String
    /// Czy dopisać Enter. Komendę niebezpieczną albo szkielet do uzupełnienia lepiej tylko WPISAĆ.
    public var sendEnter: Bool

    public init(id: String = Server.newId(), name: String = "", command: String = "", sendEnter: Bool = true) {
        self.id = id; self.name = name; self.command = command; self.sendEnter = sendEnter
    }

    enum CodingKeys: String, CodingKey { case id = "Id", name = "Name", command = "Command", sendEnter = "SendEnter" }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = (try? c.decode(String.self, forKey: .id)) ?? ""
        name = (try? c.decode(String.self, forKey: .name)) ?? ""
        command = (try? c.decode(String.self, forKey: .command)) ?? ""
        sendEnter = (try? c.decode(Bool.self, forKey: .sendEnter)) ?? true
    }

    public var displayName: String { name.trimmingCharacters(in: .whitespaces).isEmpty ? SnippetStore.firstLine(command) : name }
}

/// `snippets.json` obok listy serwerów. Wpisy bez treści są odsiewane (plik bywa edytowany ręcznie —
/// pusty wpis zająłby skrót ⌘⇧1…9 i „wysyłał" nic).
public struct SnippetStore: Sendable {
    public let directory: URL
    public var fileURL: URL { directory.appendingPathComponent("snippets.json") }
    public init(directory: URL) { self.directory = directory }

    public func load() -> [CommandSnippet] {
        guard let data = try? Data(contentsOf: fileURL),
              let list = try? JSONDecoder().decode([CommandSnippet].self, from: data) else { return [] }
        return Self.sanitize(list)
    }

    public func save(_ list: [CommandSnippet]) throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let e = JSONEncoder()
        e.outputFormatting = [.prettyPrinted, .withoutEscapingSlashes]
        try e.encode(Self.sanitize(list)).write(to: fileURL, options: .atomic)
    }

    public static func sanitize(_ list: [CommandSnippet]) -> [CommandSnippet] {
        list.compactMap { s in
            guard !s.command.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return nil }
            var r = s
            if r.id.trimmingCharacters(in: .whitespaces).isEmpty { r.id = Server.newId() }
            if r.name.trimmingCharacters(in: .whitespaces).isEmpty { r.name = firstLine(r.command) }
            return r
        }
    }

    /// Pierwszy wiersz komendy, przycięty — nazwa zastępcza.
    public static func firstLine(_ command: String) -> String {
        let s = command.replacingOccurrences(of: "\r", with: "\n").split(separator: "\n", omittingEmptySubsequences: false)
            .first.map(String.init)?.trimmingCharacters(in: .whitespaces) ?? ""
        return s.count <= 48 ? s : String(s.prefix(47)) + "…"
    }
}

/// Podstawianie zmiennych serwera w snippecie (port `SnippetVars.cs`): `ssh {user}@{host}` → `ssh root@10.0.0.5`.
/// Zachowawczo, bo tekst idzie do powłoki: `awk '{print $1}'` zostaje (to nie nazwa zmiennej), `${host}`
/// należy do powłoki, `{{host}}` daje dosłowne `{host}`, nieznana nazwa zostaje dosłownie. Celowo NIE ma
/// zmiennej z hasłem — trafiłoby do historii powłoki.
public enum SnippetVars {
    public static let names = ["host", "port", "user", "name", "group", "domain", "protocol"]

    public static func values(_ s: Server?) -> [String: String] {
        guard let s else { return Dictionary(uniqueKeysWithValues: names.map { ($0, "") }) }
        return ["host": s.host, "port": String(s.port), "user": s.username, "name": s.name, "group": s.group,
                "domain": s.domain, "protocol": s.protocolName.lowercased()]
    }

    public static func expand(_ command: String, server: Server?) -> String { expand(command, values: values(server)) }

    public static func expand(_ command: String, values: [String: String]) -> String {
        let chars = Array(command)
        var out = ""
        var i = 0
        let lowered = Dictionary(values.map { ($0.key.lowercased(), $0.value) }, uniquingKeysWith: { a, _ in a })
        while i < chars.count {
            let c = chars[i]
            if c == "{" && i + 1 < chars.count && chars[i + 1] == "{" { out.append("{"); i += 2; continue }
            if c == "}" && i + 1 < chars.count && chars[i + 1] == "}" { out.append("}"); i += 2; continue }
            guard c == "{", let end = chars[(i + 1)...].firstIndex(of: "}") else { out.append(c); i += 1; continue }
            let token = String(chars[(i + 1)..<end])
            let shellVar = i > 0 && chars[i - 1] == "$"
            if !shellVar, isName(token), let v = lowered[token.lowercased()] {
                out += v
                i = end + 1
            } else {
                out.append(c)
                i += 1
            }
        }
        return out
    }

    private static func isName(_ t: String) -> Bool {
        !t.isEmpty && t.allSatisfy { $0.isLetter || $0.isNumber || $0 == "_" }
    }

    /// Dokładnie to, co poszłoby z klawiatury: Enter w terminalu to CR, więc łamania wierszy → CR.
    public static func keystrokes(_ expanded: String, sendEnter: Bool) -> String {
        var s = expanded.replacingOccurrences(of: "\r\n", with: "\r").replacingOccurrences(of: "\n", with: "\r")
        if sendEnter && !s.hasSuffix("\r") { s += "\r" }
        return s
    }
}

/// Ranking palety poleceń (port `CommandPalette.cs`): dokładne > prefiks > granica słowa > podciąg;
/// remis — wcześniejsze trafienie i krótszy tekst. -1 = brak dopasowania, puste zapytanie = 0.
public enum CommandPalette {
    public static func score(_ haystack: String, _ query: String) -> Int {
        let q = query.trimmingCharacters(in: .whitespaces).lowercased()
        if q.isEmpty { return 0 }
        let h = haystack.lowercased()
        if h == q { return 1000 }
        guard let r = h.range(of: q) else { return -1 }
        let idx = h.distance(from: h.startIndex, to: r.lowerBound)
        let base: Int
        if idx == 0 { base = 800 }
        else if " -_.:/\\".contains(h[h.index(before: r.lowerBound)]) { base = 600 }
        else { base = 400 }
        return base - idx - min(h.count, 200)
    }
}

/// Szybkie połączenie (port `RdpUtils.ParseQuickConnect`): `host`, `host:port`, `user@host`,
/// `DOMENA\user@host:port`, `[::1]:22`.
public enum QuickConnect {
    public struct Target: Equatable, Sendable {
        public var host: String, port: Int, user: String, domain: String
    }

    public static func splitHostPort(_ address: String, defaultPort: Int) -> (String, Int) {
        let host = address.trimmingCharacters(in: .whitespaces)
        if host.hasPrefix("["), let close = host.firstIndex(of: "]"), host.distance(from: host.startIndex, to: close) > 1 {
            let inner = String(host[host.index(after: host.startIndex)..<close])
            let rest = host[host.index(after: close)...]
            if rest.hasPrefix(":"), let p = Int(rest.dropFirst()), (1...65535).contains(p) { return (inner, p) }
            return (inner, defaultPort)
        }
        if let i = host.lastIndex(of: ":"), i > host.startIndex, host.firstIndex(of: ":") == i,
           let p = Int(host[host.index(after: i)...]), (1...65535).contains(p) {
            return (String(host[..<i]), p)
        }
        return (host, defaultPort)
    }

    public static func parse(_ input: String, defaultPort: Int) -> Target {
        var s = input.trimmingCharacters(in: .whitespaces)
        var userPart = ""
        if let at = s.lastIndex(of: "@") {
            userPart = String(s[..<at])
            s = String(s[s.index(after: at)...])
        }
        let (host, port) = splitHostPort(s, defaultPort: defaultPort)
        var user = userPart.trimmingCharacters(in: .whitespaces), domain = ""
        if let bs = user.firstIndex(of: "\\"), bs > user.startIndex {
            domain = String(user[..<bs])
            user = String(user[user.index(after: bs)...])
        }
        return Target(host: host, port: port, user: user, domain: domain)
    }

    /// Serwer tymczasowy (niezapisywany). Na Macu domyślnie SSH; port 3389 albo domena → RDP.
    public static func server(from input: String) -> Server? {
        let t = parse(input, defaultPort: 22)
        guard !t.host.isEmpty, !t.host.contains(where: { $0.isWhitespace }) else { return nil }
        let rdp = t.port == 3389 || !t.domain.isEmpty
        return Server(name: t.host, host: t.host, port: rdp && t.port == 22 ? 3389 : t.port, username: t.user,
                      domain: t.domain, proto: rdp ? .rdp : .ssh)
    }
}
