import Foundation

/// Ustawienia wersji na macOS (`settings.json` obok listy serwerów). Nazwy pól jak w AppSettings wersji
/// Windows tam, gdzie znaczenie jest to samo (TerminalFontSize, ConfirmCloseConnected).
public struct MacSettings: Codable, Equatable, Sendable {
    public var terminalFontSize: Int = 13
    public var confirmCloseConnected: Bool = true

    public init() {}

    enum CodingKeys: String, CodingKey {
        case terminalFontSize = "TerminalFontSize", confirmCloseConnected = "ConfirmCloseConnected"
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        terminalFontSize = (try? c.decode(Int.self, forKey: .terminalFontSize)) ?? 13
        confirmCloseConnected = (try? c.decode(Bool.self, forKey: .confirmCloseConnected)) ?? true
        terminalFontSize = Self.clampFont(terminalFontSize)
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
