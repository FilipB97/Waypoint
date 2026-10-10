import Foundation
#if canImport(FoundationXML)
import FoundationXML
#endif

/// Import list połączeń z innych menedżerów — port `ExternalImport` z Windows: mRemoteNG (confCons.xml),
/// RDCMan (.rdg), Devolutions RDM (eksport XML) i FileZilla (sitemanager.xml). Hasła tylko z FileZilli
/// (base64, gdy nie ma hasła głównego) — pozostałe programy szyfrują je własnym kluczem.
public enum ExternalImport {
    public enum Source: String, CaseIterable, Sendable { case mRemoteNG, rdcMan, rdm, fileZilla }

    public struct Result: Sendable {
        public var servers: [Server] = []
        /// Wpisy pominięte, bo protokół nie ma odpowiednika (np. VNC w mRemoteNG, S3 w FileZilli).
        public var unsupported = 0
        /// Hasła do Pęku kluczy (id serwera → hasło).
        public var passwords: [String: String] = [:]
    }

    public enum Failure: Error { case notXML, empty }

    public static func parse(_ data: Data, as source: Source) throws -> Result {
        guard let doc = try? XMLDocument(data: data, options: []), let root = doc.rootElement() else { throw Failure.notXML }
        var r = Result()
        switch source {
        case .mRemoteNG: mrng(root, path: "", into: &r)
        case .rdcMan: if let file = child(root, "file") { rdg(file, path: "", user: "", domain: "", into: &r) }
        case .rdm: rdm(root, into: &r)
        case .fileZilla: if let servers = child(root, "Servers") { fz(servers, path: "", into: &r) }
        }
        if r.servers.isEmpty && r.unsupported == 0 { throw Failure.empty }
        return r
    }

    /// Dołączenie do listy z pominięciem duplikatów host:port (jak w Windows). Hasła tylko dla dodanych.
    public static func merge(_ existing: [Server], _ r: Result) -> (servers: [Server], added: Int, skipped: Int, passwords: [String: String]) {
        var seen = Set(existing.map { ($0.host + ":" + String($0.port)).lowercased() })
        var list = existing
        var added = 0, skipped = 0
        var pw: [String: String] = [:]
        for s in r.servers {
            guard seen.insert((s.host + ":" + String(s.port)).lowercased()).inserted else { skipped += 1; continue }
            list.append(s)
            added += 1
            if let p = r.passwords[s.id] { pw[s.id] = p }
        }
        return (list, added, skipped, pw)
    }

    // MARK: mRemoteNG

    static func mrng(_ parent: XMLElement, path: String, into r: inout Result) {
        for node in elements(parent) where node.localName?.caseInsensitiveCompare("Node") == .orderedSame {
            let type = attr(node, "Type")
            let name = attr(node, "Name")
            if type.caseInsensitiveCompare("Container") == .orderedSame {
                mrng(node, path: join(path, name), into: &r)
                continue
            }
            guard type.caseInsensitiveCompare("Connection") == .orderedSame else { continue }
            let p = attr(node, "Protocol").uppercased()
            let ssh = p == "SSH2" || p == "SSH1"
            guard ssh || p == "RDP" else { r.unsupported += 1; continue }
            let host = attr(node, "Hostname")
            guard !host.isEmpty else { continue }
            let def = ssh ? 22 : 3389
            let port = validPort(attr(node, "Port")) ?? def
            r.servers.append(Server(name: name.isEmpty ? host : name, host: host, port: port,
                                    username: attr(node, "Username"), domain: ssh ? "" : attr(node, "Domain"),
                                    proto: ssh ? .ssh : .rdp, group: path.isEmpty ? "mRemoteNG" : path))
        }
    }

    // MARK: RDCMan

    static func rdg(_ parent: XMLElement, path: String, user: String, domain: String, into r: inout Result) {
        var user = user, domain = domain
        if let c = child(parent, "logonCredentials") {
            if let u = child(c, "userName") { user = text(u) }
            if let d = child(c, "domain") { domain = text(d) }
        }
        for server in elements(parent, "server") {
            let props = child(server, "properties")
            // Starsze schematy trzymają <name> bezpośrednio pod <server>.
            let address = (props.flatMap { child($0, "name") } ?? child(server, "name")).map(text) ?? ""
            guard !address.isEmpty else { continue }
            let (host, port) = splitHostPort(address, defaultPort: 3389)
            let display = (props.flatMap { child($0, "displayName") } ?? child(server, "displayName")).map(text) ?? ""
            var su = user, sd = domain
            if let c = child(server, "logonCredentials") {
                if let u = child(c, "userName") { su = text(u) }
                if let d = child(c, "domain") { sd = text(d) }
            }
            r.servers.append(Server(name: display.isEmpty ? host : display, host: host, port: port,
                                    username: su, domain: sd, proto: .rdp, group: path.isEmpty ? "RDCMan" : path))
        }
        for g in elements(parent, "group") {
            let name = child(g, "properties").flatMap { child($0, "name") }.map(text) ?? ""
            rdg(g, path: join(path, name), user: user, domain: domain, into: &r)
        }
    }

    // MARK: Remote Desktop Manager

    static func rdm(_ root: XMLElement, into r: inout Result) {
        for node in descendants(root) where node.localName?.caseInsensitiveCompare("Connection") == .orderedSame {
            let type = elem(node, "ConnectionType").uppercased()
            if type.isEmpty || type == "GROUP" { continue }
            let proto: RemoteProtocol
            switch type {
            case "RDPCONFIGURED": proto = .rdp
            case "SSHSHELL": proto = .ssh
            case "TELNET": proto = .telnet
            case "WEBBROWSER": proto = .http
            default: r.unsupported += 1; continue
            }
            var host = firstNonEmpty(elem(node, "Host"), elem(node, "HostName"), elem(node, "Url"))
            guard !host.isEmpty else { continue }
            var port = proto.defaultPort
            if proto != .http {   // dla WWW cały adres zostaje w Host
                (host, port) = splitHostPort(host, defaultPort: port)
                if let p = validPort(elem(node, "Port")) { port = p }
            }
            let name = elem(node, "Name")
            let raw = elem(node, "Group")
            let parts = raw.split(whereSeparator: { $0 == "\\" || $0 == "/" }).map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
            r.servers.append(Server(name: name.isEmpty ? host : name, host: host, port: port,
                                    username: firstNonEmpty(elem(node, "Username"), elem(node, "UserName")),
                                    domain: proto == .rdp ? elem(node, "Domain") : "", proto: proto,
                                    group: parts.isEmpty ? "RDM" : parts.joined(separator: " / ")))
        }
    }

    // MARK: FileZilla

    static func fz(_ parent: XMLElement, path: String, into r: inout Result) {
        for node in elements(parent) {
            let n = node.localName ?? ""
            if n.caseInsensitiveCompare("Folder") == .orderedSame {
                // Nazwa folderu to bezpośredni tekst (zawartość mieszana: nazwa + zagnieżdżone <Server>).
                let fname = (node.children ?? []).filter { $0.kind == .text }.compactMap(\.stringValue).joined()
                    .trimmingCharacters(in: .whitespacesAndNewlines)
                fz(node, path: join(path, fname), into: &r)
                continue
            }
            guard n.caseInsensitiveCompare("Server") == .orderedSame else { continue }
            let host = elem(node, "Host")
            guard !host.isEmpty else { continue }
            let proto: RemoteProtocol, enc: Int, def: Int
            switch Int(elem(node, "Protocol")) ?? 0 {
            case 0: (proto, enc, def) = (.ftp, 3, 21)    // FTP → Auto (FTPS, jeśli dostępne)
            case 1: (proto, enc, def) = (.sftp, 0, 22)
            case 4: (proto, enc, def) = (.ftp, 1, 990)   // FTPS niejawne
            case 5: (proto, enc, def) = (.ftp, 0, 21)    // FTPES jawne
            case 6: (proto, enc, def) = (.ftp, 2, 21)    // zwykły FTP
            default: r.unsupported += 1; continue        // HTTP, S3, …
            }
            let anon = Int(elem(node, "Logontype")) == 0
            let name = elem(node, "Name")
            var s = Server(name: name.isEmpty ? host : name, host: host, port: validPort(elem(node, "Port")) ?? def,
                           username: anon ? "" : elem(node, "User"), proto: proto,
                           privateKeyPath: proto == .sftp ? elem(node, "Keyfile") : "",
                           ftpEncryption: proto == .ftp ? enc : 0, ftpAnonymous: proto == .ftp && anon,
                           group: path.isEmpty ? "FileZilla" : path)
            if s.proto == .sftp { s.ftpEncryption = 0 }
            // Hasło base64(UTF-8); „crypt" = chronione hasłem głównym FileZilli — pomijamy.
            if !anon, let pass = elements(node).first(where: { $0.localName?.caseInsensitiveCompare("Pass") == .orderedSame }),
               attr(pass, "encoding").caseInsensitiveCompare("base64") == .orderedSame,
               let d = Data(base64Encoded: text(pass)), let pw = String(data: d, encoding: .utf8), !pw.isEmpty {
                r.passwords[s.id] = pw
            }
            r.servers.append(s)
        }
    }

    // MARK: Pomocnicze

    /// `host`, `host:port`, `[v6]:port`, goły IPv6 (bez portu) — jak `RdpUtils.SplitHostPort`.
    public static func splitHostPort(_ address: String, defaultPort: Int) -> (String, Int) {
        let a = address.trimmingCharacters(in: .whitespaces)
        if a.hasPrefix("["), let close = a.firstIndex(of: "]") {
            let host = String(a[a.index(after: a.startIndex)..<close])
            let rest = a[a.index(after: close)...]
            if rest.hasPrefix(":"), let p = validPort(String(rest.dropFirst())) { return (host, p) }
            return (host, defaultPort)
        }
        let colons = a.filter { $0 == ":" }.count
        if colons == 1, let i = a.lastIndex(of: ":"), let p = validPort(String(a[a.index(after: i)...])) {
            return (String(a[..<i]), p)
        }
        return (a, defaultPort)
    }

    static func validPort(_ s: String) -> Int? {
        guard let p = Int(s.trimmingCharacters(in: .whitespaces)), (1...65535).contains(p) else { return nil }
        return p
    }

    static func join(_ path: String, _ name: String) -> String { path.isEmpty ? name : path + " / " + name }

    static func elements(_ e: XMLElement, _ name: String? = nil) -> [XMLElement] {
        (e.children ?? []).compactMap { $0 as? XMLElement }.filter { name == nil || $0.localName == name }
    }

    static func descendants(_ e: XMLElement) -> [XMLElement] {
        elements(e).flatMap { [$0] + descendants($0) }
    }

    static func child(_ e: XMLElement, _ name: String) -> XMLElement? {
        elements(e).first { $0.localName?.caseInsensitiveCompare(name) == .orderedSame }
    }

    static func attr(_ e: XMLElement, _ name: String) -> String {
        (e.attribute(forName: name)?.stringValue ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
    }

    static func text(_ e: XMLElement) -> String { (e.stringValue ?? "").trimmingCharacters(in: .whitespacesAndNewlines) }

    static func elem(_ e: XMLElement, _ name: String) -> String { child(e, name).map(text) ?? "" }

    static func firstNonEmpty(_ v: String...) -> String { v.first { !$0.isEmpty } ?? "" }
}
