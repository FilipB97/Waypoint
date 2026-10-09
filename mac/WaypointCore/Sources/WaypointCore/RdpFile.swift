import Foundation

/// Ustawienia RDP serwera. Na Macu to widok na pola wersji Windows przechowywane w `Server.extra`
/// (te same nazwy JSON co `ServerInfo`), więc edycja na Macu zmienia dokładnie te pola, które czyta
/// wersja Windows — a pozostałe zostają nietknięte.
public extension Server {
    private func extraBool(_ k: String, _ def: Bool) -> Bool {
        if case .bool(let b)? = extra[k] { return b }
        return def
    }
    private func extraInt(_ k: String, _ def: Int) -> Int {
        if case .number(let n)? = extra[k] { return Int(n) }
        return def
    }
    private func extraString(_ k: String) -> String {
        if case .string(let s)? = extra[k] { return s }
        return ""
    }

    var rdpRedirectClipboard: Bool {
        get { extraBool("RedirectClipboard", true) }
        set { extra["RedirectClipboard"] = .bool(newValue) }
    }
    var rdpRedirectDrives: Bool {
        get { extraBool("RedirectDrives", false) }
        set { extra["RedirectDrives"] = .bool(newValue) }
    }
    var rdpRedirectPrinters: Bool {
        get { extraBool("RedirectPrinters", false) }
        set { extra["RedirectPrinters"] = .bool(newValue) }
    }
    /// 0 = dźwięk lokalnie, 1 = bez dźwięku, 2 = na serwerze.
    var rdpAudioMode: Int {
        get { extraInt("AudioMode", 0) }
        set { extra["AudioMode"] = .number(Double(min(max(newValue, 0), 2))) }
    }
    /// 0 = nie sprawdzaj certyfikatu, 1 = wymagaj, 2 = ostrzegaj (domyślnie).
    var rdpAuthenticationLevel: Int {
        get { extraInt("AuthenticationLevel", 2) }
        set { extra["AuthenticationLevel"] = .number(Double(min(max(newValue, 0), 2))) }
    }
    var rdpUseAllMonitors: Bool {
        get { extraBool("UseAllMonitors", false) }
        set { extra["UseAllMonitors"] = .bool(newValue) }
    }
    var rdpAdminSession: Bool {
        get { extraBool("AdminSession", false) }
        set { extra["AdminSession"] = .bool(newValue) }
    }
    var rdpRemoteAppProgram: String {
        get { extraString("RemoteAppProgram") }
        set { extra["RemoteAppProgram"] = .string(newValue) }
    }
    var rdpRemoteAppArgs: String {
        get { extraString("RemoteAppArgs") }
        set { extra["RemoteAppArgs"] = .string(newValue) }
    }
    var rdpGatewayHostname: String {
        get { extraString("GatewayHostname") }
        set { extra["GatewayHostname"] = .string(newValue) }
    }
    /// 0 = bez bramy, 1 = zawsze przez bramę, 2 = wykryj.
    var rdpGatewayUsageMethod: Int {
        get { extraInt("GatewayUsageMethod", 0) }
        set { extra["GatewayUsageMethod"] = .number(Double(min(max(newValue, 0), 2))) }
    }
    /// FTPS: zgoda użytkownika na certyfikat, którego nie da się zweryfikować (samopodpisany). Pole tylko
    /// z wersji na Macu — Windows ma własne przypinanie certyfikatów, a to pole zachowa jako nieznane.
    var ftpAcceptInvalidCertificate: Bool {
        get { extraBool("MacFtpAcceptInvalidCertificate", false) }
        set { extra["MacFtpAcceptInvalidCertificate"] = .bool(newValue) }
    }

    /// Logowanie bieżącym kontem Windows — na Macu nie ma sensu, ale pole szanujemy przy eksporcie.
    var rdpUseWindowsAccount: Bool { extraBool("UseWindowsAccount", false) }
}

/// Plik `.rdp` (format mstsc: `klucz:typ:wartość`) — port `RdpFile.cs` z wersji Windows. Na Macu
/// połączenie RDP otwiera aplikacja Microsoft **Windows App**, która przyjmuje właśnie taki plik.
public enum RdpFile {
    public static func serialize(_ s: Server) -> String {
        var lines: [String] = []
        var addr = s.host
        if s.port != 0 && s.port != 3389 {
            // IPv6 z portem musi być w nawiasach, inaczej dwukropki adresu mieszają się z portem.
            if addr.contains(":") && !addr.hasPrefix("[") { addr = "[\(addr)]" }
            addr += ":\(s.port)"
        }
        lines.append("full address:s:\(addr)")

        if !s.rdpUseWindowsAccount && !s.username.isEmpty {
            lines.append("username:s:" + (s.domain.isEmpty ? s.username : "\(s.domain)\\\(s.username)"))
        }
        lines.append("redirectclipboard:i:\(s.rdpRedirectClipboard ? 1 : 0)")
        lines.append("redirectprinters:i:\(s.rdpRedirectPrinters ? 1 : 0)")
        lines.append("drivestoredirect:s:\(s.rdpRedirectDrives ? "*" : "")")
        lines.append("audiomode:i:\(min(max(s.rdpAudioMode, 0), 2))")
        lines.append("authentication level:i:\(min(max(s.rdpAuthenticationLevel, 0), 2))")
        lines.append("use multimon:i:\(s.rdpUseAllMonitors ? 1 : 0)")
        lines.append("administrative session:i:\(s.rdpAdminSession ? 1 : 0)")
        // Rozdzielczość podąża za oknem Windows App (zmiana rozmiaru okna = zmiana pulpitu).
        lines.append("dynamic resolution:i:1")

        let app = s.rdpRemoteAppProgram.trimmingCharacters(in: .whitespaces)
        if !app.isEmpty {
            lines.append("remoteapplicationmode:i:1")
            lines.append("remoteapplicationprogram:s:\(app)")
            let args = s.rdpRemoteAppArgs.trimmingCharacters(in: .whitespaces)
            if !args.isEmpty { lines.append("remoteapplicationcmdline:s:\(args)") }
            let name = s.name.trimmingCharacters(in: .whitespaces)
            lines.append("remoteapplicationname:s:\(name.isEmpty ? app : name)")
        }

        let gw = s.rdpGatewayHostname.trimmingCharacters(in: .whitespaces)
        if !gw.isEmpty {
            lines.append("gatewayhostname:s:\(gw)")
            lines.append("gatewayusagemethod:i:\(s.rdpGatewayUsageMethod == 0 ? 1 : s.rdpGatewayUsageMethod)")
        }
        return lines.joined(separator: "\r\n") + "\r\n"
    }

    /// Zawartość pliku `.rdp` → klucz (małe litery) → wartość.
    public static func parseRaw(_ content: String) -> [String: String] {
        var map: [String: String] = [:]
        for raw in content.replacingOccurrences(of: "\r\n", with: "\n").split(separator: "\n") {
            let line = raw.trimmingCharacters(in: .whitespaces)
            guard let c1 = line.firstIndex(of: ":") else { continue }
            let rest = line[line.index(after: c1)...]
            guard let c2 = rest.firstIndex(of: ":") else { continue }
            let key = line[..<c1].trimmingCharacters(in: .whitespaces).lowercased()
            if key.isEmpty { continue }
            map[key] = String(rest[rest.index(after: c2)...])
        }
        return map
    }

    /// Serwer z pliku `.rdp` (np. pobranego z portalu firmy).
    public static func parse(_ content: String) -> Server {
        let m = parseRaw(content)
        var s = Server(proto: .rdp)
        if let addr = m["full address"]?.trimmingCharacters(in: .whitespaces), !addr.isEmpty {
            let (h, p) = splitHostPort(addr)
            s.host = h
            if let p, (1...65535).contains(p) { s.port = p }
        }
        if let u = m["username"]?.trimmingCharacters(in: .whitespaces), !u.isEmpty {
            let (d, n) = splitDomainUser(u)
            s.username = n
            if !d.isEmpty { s.domain = d }
        }
        if let d = m["domain"]?.trimmingCharacters(in: .whitespaces), !d.isEmpty { s.domain = d }
        if let v = int(m, "redirectclipboard") { s.rdpRedirectClipboard = v != 0 }
        if let v = int(m, "redirectprinters") { s.rdpRedirectPrinters = v != 0 }
        if let v = m["drivestoredirect"] { s.rdpRedirectDrives = !v.trimmingCharacters(in: .whitespaces).isEmpty }
        if let v = int(m, "audiomode") { s.rdpAudioMode = v }
        if let v = int(m, "authentication level") { s.rdpAuthenticationLevel = v }
        if let v = int(m, "use multimon") { s.rdpUseAllMonitors = v != 0 }
        if let v = int(m, "administrative session") { s.rdpAdminSession = v != 0 }
        if let v = int(m, "remoteapplicationmode"), v != 0 {
            if let p = m["remoteapplicationprogram"]?.trimmingCharacters(in: .whitespaces), !p.isEmpty { s.rdpRemoteAppProgram = p }
            if let a = m["remoteapplicationcmdline"]?.trimmingCharacters(in: .whitespaces), !a.isEmpty { s.rdpRemoteAppArgs = a }
        }
        if let g = m["gatewayhostname"]?.trimmingCharacters(in: .whitespaces), !g.isEmpty { s.rdpGatewayHostname = g }
        if let v = int(m, "gatewayusagemethod") { s.rdpGatewayUsageMethod = v }
        s.name = s.host
        return s
    }

    static func splitHostPort(_ addr: String) -> (String, Int?) {
        if addr.hasPrefix("["), let end = addr.firstIndex(of: "]") {
            let h = String(addr[addr.index(after: addr.startIndex)..<end])
            let after = addr[addr.index(after: end)...]
            if after.hasPrefix(":"), let p = Int(after.dropFirst()) { return (h, p) }
            return (h, nil)
        }
        let colons = addr.filter { $0 == ":" }.count
        if colons == 1, let i = addr.lastIndex(of: ":"), let p = Int(addr[addr.index(after: i)...]) {
            return (String(addr[..<i]), p)
        }
        return (addr, nil)
    }

    static func splitDomainUser(_ v: String) -> (String, String) {
        if let i = v.firstIndex(of: "\\"), i > v.startIndex { return (String(v[..<i]), String(v[v.index(after: i)...])) }
        if let i = v.firstIndex(of: "@"), i > v.startIndex { return (String(v[v.index(after: i)...]), String(v[..<i])) }
        return ("", v)
    }

    private static func int(_ m: [String: String], _ k: String) -> Int? {
        m[k].flatMap { Int($0.trimmingCharacters(in: .whitespaces)) }
    }

    /// Nazwa pliku z nazwy serwera — Windows App pokazuje ją jako tytuł okna połączenia.
    public static func fileName(for s: Server) -> String {
        let bad = CharacterSet(charactersIn: "/\\:*?\"<>|").union(.controlCharacters)
        let cleaned = s.displayName.unicodeScalars.map { bad.contains($0) ? "_" : String($0) }.joined()
            .trimmingCharacters(in: .whitespacesAndNewlines)
        return (cleaned.isEmpty ? "Waypoint" : String(cleaned.prefix(80))) + ".rdp"
    }
}
