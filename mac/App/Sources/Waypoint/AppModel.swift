import AppKit
import Observation
import UniformTypeIdentifiers
import WaypointCore

/// Stan aplikacji: lista serwerów, wyszukiwanie, zaznaczenie, edytowany wpis i komunikaty.
/// Każda zmiana listy jest od razu zapisywana na dysk (jak w wersji Windows).
@MainActor
@Observable
final class AppModel {
    static let shared = AppModel()

    var servers: [Server] = []
    /// Otwarte karty SSH. Aktywna karta zasłania szczegóły serwera; nil = widać szczegóły zaznaczonego.
    var sessions: [SessionTab] = []
    var activeSessionID: SessionTab.ID?
    var query = ""
    var selection: Server.ID?
    /// Wpis otwarty w edytorze (arkusz). `isNew` rozróżnia „Dodaj" od „Edytuj".
    var editing: Server?
    var editingIsNew = false
    var alert: AppAlert?
    /// Krótki komunikat na dole okna (znika sam), np. „Otwarto w Windows App".
    var toast: String?

    /// WAYPOINT_DATA_DIR podmienia katalog danych — dla testu dymnego w CI i do pracy na kopii listy.
    private let store = ServerStore(directory: ProcessInfo.processInfo.environment["WAYPOINT_DATA_DIR"]
        .map { URL(fileURLWithPath: $0, isDirectory: true) } ?? ServerStore.defaultDirectory)

    var activeSession: SessionTab? { sessions.first { $0.id == activeSessionID } }

    var sections: [ServerList.Section] { ServerList.sections(servers, query: query) }
    var selected: Server? { servers.first { $0.id == selection } }
    var groupNames: [String] { ServerList.groupNames(servers) }

    func load() {
        switch store.load() {
        case .ok(let list): servers = list
        case .missing: servers = []
        case .corrupt(let path, let fallback):
            servers = fallback
            alert = AppAlert(title: L("alert.corrupt.title"), message: String(format: L("alert.corrupt.msg"), path))
        }
    }

    private func persist() {
        do { try store.save(servers) }
        catch { alert = AppAlert(title: L("alert.save.title"), message: error.localizedDescription) }
    }

    // MARK: Edycja

    func beginNew() {
        var s = Server()
        if let g = selected?.group, !g.isEmpty { s.group = g }   // nowy trafia do grupy zaznaczonego
        editing = s
        editingIsNew = true
    }

    func beginEdit(_ s: Server) {
        editing = s
        editingIsNew = false
    }

    func commit(_ s: Server) {
        let clean = ServerValidation.normalized(s)
        if let i = servers.firstIndex(where: { $0.id == clean.id }) { servers[i] = clean }
        else { servers.append(clean) }
        selection = clean.id
        editing = nil
        persist()
    }

    func duplicate(_ s: Server) {
        var copy = s
        copy.id = Server.newId()
        copy.name = String(format: L("list.copyname"), s.displayName)
        copy.pinned = false
        if let i = servers.firstIndex(where: { $0.id == s.id }) { servers.insert(copy, at: i + 1) }
        else { servers.append(copy) }
        selection = copy.id
        persist()
    }

    func togglePin(_ s: Server) {
        guard let i = servers.firstIndex(where: { $0.id == s.id }) else { return }
        servers[i].pinned.toggle()
        persist()
    }

    func delete(_ s: Server) {
        servers.removeAll { $0.id == s.id }
        Keychain.delete(for: s.id)   // hasło usuniętego serwera nie zostaje osierocone w Pęku kluczy
        if selection == s.id { selection = nil }
        persist()
    }

    // MARK: Import z wersji Windows

    func importProfile() {
        let panel = NSOpenPanel()
        panel.title = L("import.title")
        panel.message = L("import.message")
        panel.allowedContentTypes = [.json]
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            let imported = try ProfileImport.parse(Data(contentsOf: url))
            let r = ProfileImport.merge(existing: servers, imported: imported)
            servers = r.servers
            persist()
            alert = AppAlert(title: L("import.done.title"),
                             message: String(format: L("import.done.msg"), r.added, r.updated))
        } catch ProfileImport.Failure.noServers {
            alert = AppAlert(title: L("import.title"), message: L("import.err.empty"))
        } catch {
            alert = AppAlert(title: L("import.title"), message: L("import.err.format"))
        }
    }

    // MARK: Łączenie i karty

    func connect(_ s: Server) {
        switch s.proto {
        case .ssh?:
            add(.terminal(TerminalSession(server: s)))
        case .sftp?:
            openFiles(s)
        case .rdp?:
            RdpLauncher.open(s) { [weak self] outcome in
                guard let self else { return }
                switch outcome {
                case .opened:
                    self.toast = L("rdp.opened")
                case .appMissing:
                    self.alert = AppAlert(title: L("rdp.missing.title"), message: L("rdp.missing.msg"),
                                          actionTitle: L("rdp.missing.store"),
                                          action: { NSWorkspace.shared.open(RdpLauncher.appStoreURL) })
                case .failed(let msg):
                    self.alert = AppAlert(title: s.displayName, message: msg)
                }
            }
        default:
            alert = AppAlert(title: s.displayName, message: L("connect.notyet"))
        }
    }

    // MARK: Pliki .rdp

    func exportRdp(_ s: Server) {
        let panel = NSSavePanel()
        panel.nameFieldStringValue = RdpFile.fileName(for: s)
        panel.allowedContentTypes = [UTType(filenameExtension: "rdp") ?? .data]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do { try Data(RdpFile.serialize(s).utf8).write(to: url, options: .atomic) }
        catch { alert = AppAlert(title: L("rdp.export"), message: error.localizedDescription) }
    }

    /// Plik .rdp (np. z portalu firmy) → nowy serwer otwarty w edytorze do sprawdzenia przed zapisem.
    func importRdp() {
        let panel = NSOpenPanel()
        panel.title = L("rdp.import")
        panel.allowedContentTypes = [UTType(filenameExtension: "rdp") ?? .data]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        guard let text = (try? String(contentsOf: url, encoding: .utf8))
                ?? (try? String(contentsOf: url, encoding: .utf16)) else {
            alert = AppAlert(title: L("rdp.import"), message: L("import.err.format")); return
        }
        var s = RdpFile.parse(text)
        if s.host.isEmpty { alert = AppAlert(title: L("rdp.import"), message: L("import.err.format")); return }
        s.name = url.deletingPathExtension().lastPathComponent
        editing = s
        editingIsNew = true
    }

    private func add(_ tab: SessionTab) {
        sessions.append(tab)
        activeSessionID = tab.id
    }

    /// Panel plików SFTP — dla serwerów SFTP i SSH (ten sam login, osobne połączenie).
    func openFiles(_ s: Server) {
        add(.files(FileSession(server: s)))
    }

    /// Zamyka kartę; działające połączenie wymaga potwierdzenia (jak „Potwierdzaj zamknięcie" w Windows).
    func close(_ session: SessionTab, confirm: Bool = true) {
        if confirm && session.isRunning {
            let a = NSAlert()
            a.messageText = String(format: L("close.title"), session.title)
            a.informativeText = L("close.msg")
            a.addButton(withTitle: L("close.confirm"))
            a.addButton(withTitle: L("btn.cancel"))
            guard a.runModal() == .alertFirstButtonReturn else { return }
        }
        session.close()
        guard let i = sessions.firstIndex(where: { $0.id == session.id }) else { return }
        sessions.remove(at: i)
        if activeSessionID == session.id {
            // Jak w przeglądarce: aktywna staje się karta po prawej, a gdy jej nie ma — po lewej.
            activeSessionID = sessions.isEmpty ? nil : sessions[min(i, sessions.count - 1)].id
        }
    }

    func activateTab(_ index: Int) {
        guard sessions.indices.contains(index) else { return }
        activeSessionID = sessions[index].id
    }

    func cycleTab(_ delta: Int) {
        guard !sessions.isEmpty else { return }
        let cur = sessions.firstIndex { $0.id == activeSessionID } ?? -1
        let next = ((cur + delta) % sessions.count + sessions.count) % sessions.count
        activeSessionID = sessions[next].id
    }

    /// ⌘Q przy działających połączeniach: jedno pytanie o wszystkie.
    func confirmQuit() -> Bool {
        let running = sessions.filter(\.isRunning).count
        guard running > 0 else { return true }
        let a = NSAlert()
        a.messageText = L("quit.title")
        a.informativeText = String(format: L("quit.msg"), running)
        a.addButton(withTitle: L("quit.confirm"))
        a.addButton(withTitle: L("btn.cancel"))
        guard a.runModal() == .alertFirstButtonReturn else { return false }
        for s in sessions { s.close() }
        return true
    }

    /// Skróty kart: ⌘W zamyka kartę (zamiast całego okna), ⌘1…⌘9 przełącza, ⌘⇧[ / ⌘⇧] — poprzednia/następna.
    /// Monitor zdarzeń, bo terminal jako pierwszy odbiorca i tak dostałby te klawisze przed menu SwiftUI.
    func installKeyMonitor() {
        NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            guard let self, event.modifierFlags.contains(.command) else { return event }
            let shift = event.modifierFlags.contains(.shift)
            let chars = event.charactersIgnoringModifiers ?? ""
            let isMain = event.window?.isKind(of: NSPanel.self) == false
            guard isMain else { return event }
            if chars == "w", !shift, let s = self.activeSession {
                self.close(s)
                return nil
            }
            if !shift, let d = Int(chars), (1...9).contains(d), !self.sessions.isEmpty {
                self.activateTab(d == 9 ? self.sessions.count - 1 : d - 1)
                return nil
            }
            if shift, chars == "]" || chars == "}" { self.cycleTab(1); return nil }
            if shift, chars == "[" || chars == "{" { self.cycleTab(-1); return nil }
            return event
        }
    }
}

struct AppAlert: Identifiable {
    let id = UUID()
    var title: String
    var message: String
    /// Opcjonalny drugi przycisk (np. „Otwórz App Store").
    var actionTitle: String? = nil
    var action: (() -> Void)? = nil
}
