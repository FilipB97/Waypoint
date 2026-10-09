import AppKit
import UniformTypeIdentifiers
import WaypointCore

/// Przenosiny: import z innych menedżerów połączeń i eksport profilu (dla Windows albo innego Maca).
extension AppModel {
    func importExternal(_ source: ExternalImport.Source) {
        let panel = NSOpenPanel()
        panel.title = L("migr.title." + source.rawValue)
        panel.message = L("migr.hint." + source.rawValue)
        panel.allowedContentTypes = source == .rdcMan ? [UTType(filenameExtension: "rdg") ?? .xml, .xml] : [.xml, .data]
        if source == .fileZilla {   // FileZilla na Macu trzyma listę w ~/.config/filezilla
            let dir = URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent(".config/filezilla")
            if FileManager.default.fileExists(atPath: dir.path) { panel.directoryURL = dir }
        }
        guard panel.runModal() == .OK, let url = panel.url else { return }
        importExternal(source, from: url)
    }

    /// Import z pliku (wspólne dla okna wyboru i testu dymnego). Duplikaty host:port są pomijane.
    @discardableResult
    func importExternal(_ source: ExternalImport.Source, from url: URL) -> (added: Int, skipped: Int, passwords: Int)? {
        let title = L("migr.title." + source.rawValue)
        let result: ExternalImport.Result
        do { result = try ExternalImport.parse(Data(contentsOf: url), as: source) }
        catch ExternalImport.Failure.empty { alert = AppAlert(title: title, message: L("import.err.empty")); return nil }
        catch { alert = AppAlert(title: title, message: L("migr.err.format")); return nil }
        let m = ExternalImport.merge(servers, result)
        servers = m.servers
        persist()
        var saved = 0
        for (id, pw) in m.passwords {
            let name = servers.first { $0.id == id }?.displayName ?? id
            if Keychain.save(pw, for: id, label: "Waypoint — \(name)") { saved += 1 }
        }
        var msg = String(format: L("migr.done"), m.added)
        if m.skipped > 0 { msg += "\n" + String(format: L("migr.dup"), m.skipped) }
        if result.unsupported > 0 { msg += "\n" + String(format: L("migr.unsupported"), result.unsupported) }
        if saved > 0 { msg += "\n" + String(format: L("migr.passwords"), saved) }
        alert = AppAlert(title: title, message: msg)
        return (m.added, m.skipped, saved)
    }

    func exportProfile() {
        let panel = NSSavePanel()
        panel.title = L("export.title")
        panel.message = L("export.msg")
        panel.nameFieldStringValue = ProfileExport.fileName()
        panel.allowedContentTypes = [.json]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        exportProfile(to: url)
    }

    @discardableResult
    func exportProfile(to url: URL) -> Bool {
        do {
            try ProfileExport.serialize(servers: servers, profiles: profiles).write(to: url, options: .atomic)
            toast = String(format: L("export.done"), servers.count)
            return true
        } catch {
            alert = AppAlert(title: L("export.title"), message: error.localizedDescription)
            return false
        }
    }
}

/// Wake-on-LAN i sprawdzanie aktualizacji.
extension AppModel {
    func wake(_ s: Server) {
        guard let mac = WakeOnLan.parseMac(s.macAddress) else {
            alert = AppAlert(title: L("wol.title"), message: String(format: L("wol.badmac"), s.macAddress))
            return
        }
        if let err = WakeOnLan.send(mac) { alert = AppAlert(title: L("wol.title"), message: err) }
        else { toast = String(format: L("wol.sent"), s.displayName) }
    }

    static var currentVersion: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.0.0"
    }

    /// Przy starcie (gdy włączone) cicho; z menu — zawsze z odpowiedzią.
    func checkForUpdates(manual: Bool) {
        if !manual && (!settings.checkUpdates || ProcessInfo.processInfo.environment["WAYPOINT_SMOKE_DIR"] != nil) { return }
        var req = URLRequest(url: UpdateCheck.latestURL, timeoutInterval: 8)
        req.setValue("Waypoint-mac", forHTTPHeaderField: "User-Agent")
        Task {
            let data = try? await URLSession.shared.data(for: req).0
            let release = data.flatMap(UpdateCheck.parseRelease)
            if let r = UpdateCheck.update(from: release, current: Self.currentVersion) {
                alert = AppAlert(title: L("upd.title"), message: String(format: L("upd.available"), r.versionText, Self.currentVersion),
                                 actionTitle: L("upd.download"),
                                 action: { NSWorkspace.shared.open(r.pageURL ?? r.macZipURL!) })
            } else if manual {
                alert = AppAlert(title: L("upd.title"),
                                 message: data == nil ? L("upd.failed") : String(format: L("upd.current"), Self.currentVersion))
            }
        }
    }
}
