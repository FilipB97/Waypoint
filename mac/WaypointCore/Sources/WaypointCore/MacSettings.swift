import Foundation

/// Ustawienia wersji na macOS (`settings.json` obok listy serwerów). Nazwy pól jak w AppSettings wersji
/// Windows tam, gdzie znaczenie jest to samo (TerminalFontSize, ConfirmCloseConnected).
public struct MacSettings: Codable, Equatable, Sendable {
    public var terminalFontSize: Int = 13
    public var confirmCloseConnected: Bool = true
    /// Sonda TCP host:port w tle → kropki dostępności na liście (domyślnie jak w Windows).
    public var reachabilityEnabled: Bool = true
    public var reachabilityIntervalSec: Int = 30
    public var probeTimeoutSeconds: Int = 2
    /// Opóźnienie (ms) obok kropki — domyślnie wyłączone, jak w Windows.
    public var showLatency: Bool = false
    /// Dziennik połączeń (connections.log) — tylko metadane, nigdy hasła.
    public var connectionLogEnabled: Bool = true
    /// Zwinięte grupy listy (po nazwie grupy).
    public var collapsedGroups: [String] = []
    /// Ostatnio używane serwery (id), najnowszy pierwszy.
    public var recentIds: [String] = []
    /// Sprawdzanie nowej wersji przy starcie (jak CheckUpdates w Windows).
    public var checkUpdates: Bool = true

    public init() {}

    enum CodingKeys: String, CodingKey {
        case terminalFontSize = "TerminalFontSize", confirmCloseConnected = "ConfirmCloseConnected"
        case reachabilityEnabled = "ReachabilityEnabled", reachabilityIntervalSec = "ReachabilityIntervalSec"
        case probeTimeoutSeconds = "ProbeTimeoutSeconds", showLatency = "ShowLatency"
        case connectionLogEnabled = "ConnectionLogEnabled", collapsedGroups = "CollapsedGroups"
        case recentIds = "RecentIds", checkUpdates = "CheckUpdates"
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        terminalFontSize = (try? c.decode(Int.self, forKey: .terminalFontSize)) ?? 13
        confirmCloseConnected = (try? c.decode(Bool.self, forKey: .confirmCloseConnected)) ?? true
        terminalFontSize = Self.clampFont(terminalFontSize)
        reachabilityEnabled = (try? c.decode(Bool.self, forKey: .reachabilityEnabled)) ?? true
        reachabilityIntervalSec = Self.clampInterval((try? c.decode(Int.self, forKey: .reachabilityIntervalSec)) ?? 30)
        probeTimeoutSeconds = Self.clampTimeout((try? c.decode(Int.self, forKey: .probeTimeoutSeconds)) ?? 2)
        showLatency = (try? c.decode(Bool.self, forKey: .showLatency)) ?? false
        connectionLogEnabled = (try? c.decode(Bool.self, forKey: .connectionLogEnabled)) ?? true
        collapsedGroups = (try? c.decode([String].self, forKey: .collapsedGroups)) ?? []
        recentIds = (try? c.decode([String].self, forKey: .recentIds)) ?? []
        checkUpdates = (try? c.decode(Bool.self, forKey: .checkUpdates)) ?? true
    }

    /// Zakresy jak w Windows: interwał sondy 5–3600 s, limit czasu 1–60 s.
    public static func clampInterval(_ v: Int) -> Int { min(3600, max(5, v)) }
    public static func clampTimeout(_ v: Int) -> Int { min(60, max(1, v)) }

    /// Przenosi serwer na początek „ostatnich", przycinając listę do `max` (jak RecordRecent w Windows).
    public mutating func recordRecent(_ id: String, max: Int = 15) {
        guard !id.isEmpty else { return }
        recentIds.removeAll { $0 == id }
        recentIds.insert(id, at: 0)
        if recentIds.count > max { recentIds.removeLast(recentIds.count - max) }
    }

    /// Ten sam zakres co w Windows (8–24).
    public static func clampFont(_ v: Int) -> Int { min(24, max(8, v)) }

    public static func load(from dir: URL) -> MacSettings {
        guard let d = try? Data(contentsOf: dir.appendingPathComponent("settings.json")),
              let s = try? JSONDecoder().decode(MacSettings.self, from: d) else { return MacSettings() }
        return s
    }

    public func save(to dir: URL) throws {
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let e = JSONEncoder()
        e.outputFormatting = [.prettyPrinted, .sortedKeys]
        try e.encode(self).write(to: dir.appendingPathComponent("settings.json"), options: .atomic)
    }
}
