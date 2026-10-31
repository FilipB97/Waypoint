import AppKit
import WaypointCore

/// Aktualizacja z aplikacji: pobiera `…-mac.zip` z wydania, podmienia Waypoint.app i uruchamia go ponownie.
///
/// Paczka ma podpis ad-hoc (bez Developer ID), więc pobrana przeglądarką dostaje atrybut kwarantanny
/// i Gatekeeper każe ją zatwierdzać w Ustawieniach prywatności. Plik pobrany przez URLSession tego
/// atrybutu nie dostaje (aplikacja nie ma LSFileQuarantineEnabled), a dla pewności i tak go zdejmujemy —
/// nowa wersja uruchamia się bez pytania, jak każda aktualizacja w miejscu.
@MainActor
enum Updater {
    enum Failure: LocalizedError {
        case notWritable(String), translocated, badArchive, wrongBundle, tool(String)
        var errorDescription: String? {
            switch self {
            case .notWritable(let dir): return String(format: L("upd.err.write"), dir)
            case .translocated: return L("upd.err.translocated")
            case .badArchive: return L("upd.err.archive")
            case .wrongBundle: return L("upd.err.bundle")
            case .tool(let m): return m
            }
        }
    }

    /// Czy da się podmienić aplikację w miejscu (nil = tak; inaczej powód).
    static func blocker() -> Failure? {
        let app = Bundle.main.bundleURL
        // Uruchomiona prosto z pobranego folderu: macOS daje ją z losowej ścieżki tylko do odczytu.
        if app.path.contains("/AppTranslocation/") { return .translocated }
        let dir = app.deletingLastPathComponent().path
        return FileManager.default.isWritableFile(atPath: dir) ? nil : .notWritable(dir)
    }

    static func install(_ release: UpdateCheck.Release, progress: @escaping (String) -> Void) async throws {
        if let b = blocker() { throw b }
        guard let zipURL = release.macZipURL else { throw Failure.badArchive }
        progress(String(format: L("upd.downloading"), release.versionText))
        var req = URLRequest(url: zipURL, timeoutInterval: 120)
        req.setValue("Waypoint-mac", forHTTPHeaderField: "User-Agent")
        let (tmpZip, resp) = try await URLSession.shared.download(for: req)
        guard (resp as? HTTPURLResponse)?.statusCode == 200 else { throw Failure.badArchive }

        let work = FileManager.default.temporaryDirectory.appendingPathComponent("waypoint-update-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: work, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: work) }
        let zip = work.appendingPathComponent("update.zip")
        try FileManager.default.moveItem(at: tmpZip, to: zip)
        progress(L("upd.installing"))
        try await run("/usr/bin/ditto", ["-x", "-k", zip.path, work.appendingPathComponent("x").path])

        guard let newApp = try FileManager.default.contentsOfDirectory(at: work.appendingPathComponent("x"),
                                                                       includingPropertiesForKeys: nil)
            .first(where: { $0.pathExtension == "app" }) else { throw Failure.badArchive }
        // Ta sama aplikacja i wersja z wydania — nie podmieniamy niczego innego.
        let info = NSDictionary(contentsOf: newApp.appendingPathComponent("Contents/Info.plist"))
        guard info?["CFBundleIdentifier"] as? String == Bundle.main.bundleIdentifier,
              let v = info?["CFBundleShortVersionString"] as? String,
              UpdateCheck.parseVersion(v) == release.version else { throw Failure.wrongBundle }
        try? await run("/usr/bin/xattr", ["-dr", "com.apple.quarantine", newApp.path])

        let current = Bundle.main.bundleURL
        _ = try FileManager.default.replaceItemAt(current, withItemAt: newApp, backupItemName: nil, options: [])
        relaunch(current)
    }

    /// Nowa wersja startuje, gdy ta się zakończy (pomocniczy proces czeka na koniec bieżącego).
    private static func relaunch(_ app: URL) {
        let pid = ProcessInfo.processInfo.processIdentifier
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/sh")
        p.arguments = ["-c", "while kill -0 \(pid) 2>/dev/null; do sleep 0.2; done; /usr/bin/open \"$1\"", "sh", app.path]
        try? p.run()
        NSApp.terminate(nil)
    }

    private static func run(_ tool: String, _ args: [String]) async throws {
        try await withCheckedThrowingContinuation { (c: CheckedContinuation<Void, Error>) in
            let p = Process()
            p.executableURL = URL(fileURLWithPath: tool)
            p.arguments = args
            let err = Pipe()
            p.standardError = err
            p.terminationHandler = { proc in
                if proc.terminationStatus == 0 { c.resume() }
                else {
                    let msg = String(decoding: err.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
                    c.resume(throwing: Failure.tool("\(tool): \(msg)"))
                }
            }
            do { try p.run() } catch { c.resume(throwing: error) }
        }
    }
}
