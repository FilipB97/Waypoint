import Foundation

/// Lista serwerów na dysku: `~/Library/Application Support/Waypoint/servers.json`, w tym samym
/// formacie co `%APPDATA%\RdpManager\servers.json` wersji Windows (tablica obiektów `ServerInfo`).
///
/// Zapis jest atomowy, a poprzednia wersja pliku zostaje jako `.bak`. Plik, którego nie da się
/// odczytać, NIE jest nadpisywany pustą listą — zostaje odłożony obok jako `.corrupt-<czas>`,
/// żeby jedna literówka przy ręcznej edycji nie kasowała wszystkich serwerów.
public struct ServerStore: Sendable {
    public let directory: URL
    public var fileURL: URL { directory.appendingPathComponent("servers.json") }

    public init(directory: URL) { self.directory = directory }

    /// Katalog danych aplikacji użytkownika.
    public static var defaultDirectory: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Library/Application Support")
        return base.appendingPathComponent("Waypoint", isDirectory: true)
    }

    public enum LoadResult: Equatable, Sendable {
        case ok([Server])
        /// Pliku nie było — pierwsze uruchomienie.
        case missing
        /// Plik był, ale nieczytelny; odłożony pod podaną ścieżkę, a lista startuje pusta.
        case corrupt(preservedAs: String, servers: [Server])
    }

    public func load() -> LoadResult {
        let fm = FileManager.default
        guard fm.fileExists(atPath: fileURL.path) else {
            // Zostało tylko .bak (np. przerwany zapis) — weź je.
            if let bak = try? decode(Data(contentsOf: backupURL)) { return .ok(bak) }
            return .missing
        }
        do {
            return .ok(try decode(Data(contentsOf: fileURL)))
        } catch {
            let stamp = Int(Date().timeIntervalSince1970)
            let preserved = directory.appendingPathComponent("servers.json.corrupt-\(stamp)")
            try? fm.moveItem(at: fileURL, to: preserved)
            let fallback = (try? decode(Data(contentsOf: backupURL))) ?? []
            return .corrupt(preservedAs: preserved.path, servers: fallback)
        }
    }

    public func save(_ servers: [Server]) throws {
        let fm = FileManager.default
        try fm.createDirectory(at: directory, withIntermediateDirectories: true)
        if fm.fileExists(atPath: fileURL.path) {
            try? fm.removeItem(at: backupURL)
            try? fm.copyItem(at: fileURL, to: backupURL)
        }
        try Self.encode(servers).write(to: fileURL, options: .atomic)
    }

    private var backupURL: URL { directory.appendingPathComponent("servers.json.bak") }

    private func decode(_ data: Data) throws -> [Server] {
        try JSONDecoder().decode([Server].self, from: data)
    }

    public static func encode(_ servers: [Server]) throws -> Data {
        let e = JSONEncoder()
        e.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        return try e.encode(servers)
    }
}
