import AppKit
import WaypointCore

/// RDP na Macu: zapisuje plik `.rdp` i otwiera go w aplikacji Microsoft **Windows App** (dawniej
/// „Microsoft Remote Desktop", ten sam identyfikator pakietu). Osadzonego RDP w oknie Waypointa
/// na Macu nie ma — kontrolka mstscax istnieje tylko w Windows.
enum RdpLauncher {
    static let bundleID = "com.microsoft.rdc.macos"
    static let appStoreURL = URL(string: "macappstore://apps.apple.com/app/id1295203466")!

    enum Outcome { case opened, appMissing, failed(String) }

    static var isWindowsAppInstalled: Bool {
        NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID) != nil
    }

    /// Katalog na pliki `.rdp` — pamięć podręczna (nic tu nie jest trwałe, plik powstaje przy każdym łączeniu).
    static var directory: URL {
        let base = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask).first!
        return base.appendingPathComponent("Waypoint/rdp", isDirectory: true)
    }

    @discardableResult
    static func writeFile(for s: Server) throws -> URL {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let url = directory.appendingPathComponent(RdpFile.fileName(for: s))
        try Data(RdpFile.serialize(s).utf8).write(to: url, options: .atomic)
        return url
    }

    static func open(_ s: Server, completion: @escaping (Outcome) -> Void) {
        let file: URL
        do { file = try writeFile(for: s) } catch { completion(.failed(error.localizedDescription)); return }
        guard let app = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID) else {
            completion(.appMissing); return
        }
        NSWorkspace.shared.open([file], withApplicationAt: app, configuration: NSWorkspace.OpenConfiguration()) { _, error in
            DispatchQueue.main.async { completion(error.map { .failed($0.localizedDescription) } ?? .opened) }
        }
    }
}
