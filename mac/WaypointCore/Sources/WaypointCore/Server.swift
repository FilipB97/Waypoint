import Foundation

/// Protokół połączenia. Nazwy (rawValue) są takie same jak w wersji Windows (enum zapisywany po nazwie),
/// więc plik serwerów i eksport profilu są wspólne dla obu aplikacji.
public enum RemoteProtocol: String, CaseIterable, Sendable {
    case rdp = "Rdp", ssh = "Ssh", telnet = "Telnet", serial = "Serial", http = "Http"
    case vnc = "Vnc", sftp = "Sftp", ftp = "Ftp", rest = "Rest"

    public var defaultPort: Int {
        switch self {
        case .rdp: return 3389
        case .ssh, .sftp: return 22
        case .telnet: return 23
        case .ftp: return 21
        case .vnc: return 5900
        case .http, .rest: return 443
        case .serial: return 9600   // w wersji Windows Port = baud
        }
    }

    /// Krótka etykieta na liście.
    public var badge: String {
        switch self {
        case .rdp: return "RDP"
        case .ssh: return "SSH"
        case .telnet: return "Telnet"
        case .serial: return "COM"
        case .http: return "WEB"
        case .vnc: return "VNC"
        case .sftp: return "SFTP"
        case .ftp: return "FTP"
        case .rest: return "REST"
        }
    }

    /// Co ta wersja na Maca umie otworzyć. Pozostałe wpisy są widoczne i edytowalne (żeby import
    /// z Windows niczego nie gubił), ale łączenie pojawi się w kolejnych krokach albo wcale (COM).
    public var supportedOnMac: Bool {
        switch self {
        case .ssh, .sftp, .ftp, .rdp, .telnet, .serial, .vnc, .http: return true
        case .rest: return false   // klient REST — w ostatnim kroku
        }
    }
}

/// Definicja serwera — odpowiednik `ServerInfo` z wersji Windows, w tym samym formacie JSON
/// (nazwy pól PascalCase). Hasła nigdy tu nie trafiają: na Macu są w Pęku kluczy (Keychain).
///
/// Pola, których Mac nie używa (np. ustawienia RemoteApp, bramy RD, przekierowań), są przechowywane
/// w `extra` i zapisywane z powrotem bez zmian — przeniesienie profilu Windows → Mac → Windows nie
/// gubi niczego.
public struct Server: Identifiable, Hashable, Sendable {
    public var id: String
    public var name: String
    public var host: String
    public var port: Int
    public var username: String
    public var domain: String
    /// Nazwa protokołu tak, jak zapisana. Nieznana nazwa (z nowszej wersji) zostaje zachowana.
    public var protocolName: String
    public var privateKeyPath: String
    public var tunnels: [String]
    /// FTP: 0 = jawne FTPS, 1 = niejawne FTPS, 2 = zwykły FTP (jak w wersji Windows).
    public var ftpEncryption: Int
    public var ftpAnonymous: Bool
    public var group: String
    public var tags: [String]
    public var notes: String
    public var avatarColor: String
    public var pinned: Bool
    public var extra: [String: JSONValue]

    public var proto: RemoteProtocol? {
        get { RemoteProtocol(rawValue: protocolName) }
        set { if let v = newValue { protocolName = v.rawValue } }
    }

    public init(id: String = Server.newId(), name: String = "", host: String = "", port: Int? = nil,
                username: String = "", domain: String = "", proto: RemoteProtocol = .ssh,
                privateKeyPath: String = "", tunnels: [String] = [], ftpEncryption: Int = 0,
                ftpAnonymous: Bool = false, group: String = "", tags: [String] = [], notes: String = "",
                avatarColor: String = "", pinned: Bool = false, extra: [String: JSONValue] = [:]) {
        self.id = id
        self.name = name
        self.host = host
        self.port = port ?? proto.defaultPort
        self.username = username
        self.domain = domain
        self.protocolName = proto.rawValue
        self.privateKeyPath = privateKeyPath
        self.tunnels = tunnels
        self.ftpEncryption = ftpEncryption
        self.ftpAnonymous = ftpAnonymous
        self.group = group
        self.tags = tags
        self.notes = notes
        self.avatarColor = avatarColor
        self.pinned = pinned
        self.extra = extra
    }

    /// Ten sam kształt identyfikatora co w C# (`Guid.NewGuid().ToString("N")`).
    public static func newId() -> String {
        UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
    }

    /// Nazwa do wyświetlenia: nazwa, a gdy pusta — host.
    public var displayName: String {
        let n = name.trimmingCharacters(in: .whitespaces)
        return n.isEmpty ? host : n
    }

    /// Inicjały do awatara: pierwsze litery dwóch pierwszych słów („Prod DB" → „PD"),
    /// a dla jednego słowa dwie pierwsze litery („nginx" → „NG").
    public var initials: String {
        // Bez nazwy liczy się sam adres: „10.1.2.3" → „10", a nie „11" z kolejnych oktetów.
        if name.trimmingCharacters(in: .whitespaces).isEmpty {
            let chars = host.filter { $0.isLetter || $0.isNumber }
            return chars.isEmpty ? "?" : String(chars.prefix(2)).uppercased()
        }
        let words = displayName.split(whereSeparator: { !$0.isLetter && !$0.isNumber })
        if words.count >= 2 { return String(words[0].prefix(1) + words[1].prefix(1)).uppercased() }
        if let w = words.first { return String(w.prefix(2)).uppercased() }
        return "?"
    }
}

extension Server: Codable {
    static let knownKeys: Set<String> = [
        "Id", "Name", "Host", "Port", "Username", "Domain", "Protocol", "PrivateKeyPath", "Tunnels",
        "FtpEncryption", "FtpAnonymous", "Group", "Tags", "Notes", "AvatarColor", "Pinned"
    ]

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: AnyKey.self)
        func str(_ k: String) -> String { ((try? c.decodeIfPresent(String.self, forKey: AnyKey(k))) ?? nil) ?? "" }
        func int(_ k: String) -> Int? { (try? c.decodeIfPresent(Int.self, forKey: AnyKey(k))) ?? nil }
        func bool(_ k: String) -> Bool { ((try? c.decodeIfPresent(Bool.self, forKey: AnyKey(k))) ?? nil) ?? false }
        func list(_ k: String) -> [String] { ((try? c.decodeIfPresent([String].self, forKey: AnyKey(k))) ?? nil) ?? [] }

        let idValue = str("Id")
        id = idValue.isEmpty ? Server.newId() : idValue
        name = str("Name")
        host = str("Host")
        let p = str("Protocol")
        // Brak pola = RDP: tak domyślnie zapisuje wersja Windows (wpisy sprzed obsługi SSH).
        protocolName = p.isEmpty ? RemoteProtocol.rdp.rawValue : p
        port = int("Port") ?? (RemoteProtocol(rawValue: protocolName)?.defaultPort ?? 3389)
        username = str("Username")
        domain = str("Domain")
        privateKeyPath = str("PrivateKeyPath")
        tunnels = list("Tunnels")
        ftpEncryption = int("FtpEncryption") ?? 0
        ftpAnonymous = bool("FtpAnonymous")
        group = str("Group")
        tags = list("Tags")
        notes = str("Notes")
        avatarColor = str("AvatarColor")
        pinned = bool("Pinned")

        var rest: [String: JSONValue] = [:]
        for key in c.allKeys where !Server.knownKeys.contains(key.stringValue) {
            rest[key.stringValue] = try c.decode(JSONValue.self, forKey: key)
        }
        extra = rest
    }

    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: AnyKey.self)
        // Najpierw nieznane — znane pola mają pierwszeństwo, gdyby nazwa się powtórzyła.
        for (k, v) in extra where !Server.knownKeys.contains(k) { try c.encode(v, forKey: AnyKey(k)) }
        try c.encode(id, forKey: AnyKey("Id"))
        try c.encode(name, forKey: AnyKey("Name"))
        try c.encode(host, forKey: AnyKey("Host"))
        try c.encode(port, forKey: AnyKey("Port"))
        try c.encode(username, forKey: AnyKey("Username"))
        try c.encode(domain, forKey: AnyKey("Domain"))
        try c.encode(protocolName, forKey: AnyKey("Protocol"))
        try c.encode(privateKeyPath, forKey: AnyKey("PrivateKeyPath"))
        try c.encode(tunnels, forKey: AnyKey("Tunnels"))
        try c.encode(ftpEncryption, forKey: AnyKey("FtpEncryption"))
        try c.encode(ftpAnonymous, forKey: AnyKey("FtpAnonymous"))
        try c.encode(group, forKey: AnyKey("Group"))
        try c.encode(tags, forKey: AnyKey("Tags"))
        try c.encode(notes, forKey: AnyKey("Notes"))
        try c.encode(avatarColor, forKey: AnyKey("AvatarColor"))
        try c.encode(pinned, forKey: AnyKey("Pinned"))
    }
}
