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
    let dataDirectory: URL = ProcessInfo.processInfo.environment["WAYPOINT_DATA_DIR"]
        .map { URL(fileURLWithPath: $0, isDirectory: true) } ?? ServerStore.defaultDirectory
    private var store: ServerStore { ServerStore(directory: dataDirectory) }

    var settings = MacSettings()
    /// Współdzielone profile poświadczeń (`credprofiles.json`, jak w Windows).
    var profiles: [CredentialProfile] = []
    var profileManagerOpen = false
    var generatorOpen = false
    /// Serwer w arkuszu „Połącz jako…".
    var connectAsTarget: Server?
    var snippets: [CommandSnippet] = []
    /// Paleta poleceń (⌘K) i jej tekst startowy (np. z „Szybkie połączenie…").
    var paletteOpen = false
    var paletteSeed = ""
    var snippetPickerOpen = false
    var snippetManagerOpen = false

    /// Wynik ostatniej sondy osiągalności (id serwera → stan); brak wpisu = jeszcze nie sprawdzono.
    var reach: [String: Reach] = [:]
    /// Średnie opóźnienie osiągalnych hostów z kolejnych cykli (ostatnie 48) — wykres na pulpicie.
    var latencySamples: [Double] = []
    @ObservationIgnored var reachTask: Task<Void, Never>?

    var activeSession: SessionTab? { sessions.first { $0.id == activeSessionID } }

    var sections: [ServerList.Section] { ServerList.sections(servers, query: query) }
    var selected: Server? { servers.first { $0.id == selection } }
    var groupNames: [String] { ServerList.groupNames(servers) }

    func load() {
        settings = MacSettings.load(from: dataDirectory)
        snippets = SnippetStore(directory: dataDirectory).load()
        profiles = CredentialProfileStore(directory: dataDirectory).load()
        switch store.load() {
        case .ok(let list): servers = list
        case .missing: servers = []
        case .corrupt(let path, let fallback):
            servers = fallback
            alert = AppAlert(title: L("alert.corrupt.title"), message: String(format: L("alert.corrupt.msg"), path))
        }
        restartReachability()
    }

    func persist() {
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

    /// Serwer z loginem z profilu poświadczeń (o ile wskazuje istniejący profil).
    func resolved(_ s: Server) -> Server { Credentials.resolve(s, profiles: profiles) }

    func connect(_ server: Server) {
        let s = resolved(server)
        if s.proto != .sftp && s.proto != .ftp && s.proto?.supportedOnMac == true { noteConnected(s) }   // pliki — w openFiles
        switch s.proto {
        case .ssh?, .telnet?, .serial?:
            let t = TerminalSession(server: s)
            t.onEnded = { [weak self] code in
                // 255 = błąd samego ssh (host, uwierzytelnienie); inne kody to wyjście z powłoki.
                self?.logConnection(code == 255 ? "FAILED" : "DISCONNECTED", s)
            }
            add(.terminal(t))
        case .sftp?, .ftp?:
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
        case .vnc?:
            openExternal(ExternalLinks.vncURL(s), for: s, opened: L("vnc.opened"))
        case .http?:
            openExternal(ExternalLinks.webURL(s.host), for: s, opened: nil)
        default:
            alert = AppAlert(title: s.displayName, message: L("connect.notyet"))
        }
    }

    /// VNC → Udostępnianie ekranu, WWW → domyślna przeglądarka. W teście dymnym tylko komunikat z adresem
    /// (otwarcie innej aplikacji na runnerze CI niczego nie sprawdza, a mogłoby zawiesić test).
    private func openExternal(_ url: URL?, for s: Server, opened: String?) {
        guard let url else {
            alert = AppAlert(title: s.displayName, message: String(format: L("link.bad"), s.host))
            return
        }
        if ProcessInfo.processInfo.environment["WAYPOINT_SMOKE_DIR"] != nil { toast = url.absoluteString; return }
        NSWorkspace.shared.open(url)
        if let opened { toast = opened }
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

    func add(_ tab: SessionTab) {
        sessions.append(tab)
        activeSessionID = tab.id
    }

    /// Panel plików SFTP — dla serwerów SFTP i SSH (ten sam login, osobne połączenie).
    func openFiles(_ server: Server) {
        let s = resolved(server)
        noteConnected(s)
        let fs = FileSession(server: s)
        fs.onTrustCertificate = { [weak self] updated in
            guard let self, let i = self.servers.firstIndex(where: { $0.id == updated.id }) else { return }
            self.servers[i].ftpAcceptInvalidCertificate = updated.ftpAcceptInvalidCertificate
            self.persist()
        }
        add(.files(fs))
    }

    /// Zamyka kartę; działające połączenie wymaga potwierdzenia (jak „Potwierdzaj zamknięcie" w Windows).
    func close(_ session: SessionTab, confirm: Bool = true) {
        if confirm && settings.confirmCloseConnected && session.isRunning {
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
    // MARK: Ustawienia, snippety, terminal

    func saveSettings() {
        try? settings.save(to: dataDirectory)
    }

    func saveSnippets(_ list: [CommandSnippet]) {
        snippets = SnippetStore.sanitize(list)
        do { try SnippetStore(directory: dataDirectory).save(snippets) }
        catch { alert = AppAlert(title: L("snip.title"), message: error.localizedDescription) }
    }

    var activeTerminal: TerminalSession? { activeSession?.terminal }

    /// Wysyła snippet do aktywnego terminala jak wpisany z klawiatury (zmienne serwera podstawione).
    func send(_ snippet: CommandSnippet) {
        guard let t = activeTerminal else { toast = L("snip.noterminal"); return }
        let text = SnippetVars.keystrokes(SnippetVars.expand(snippet.command, server: t.server), sendEnter: snippet.sendEnter)
        t.view.send(txt: text)
        t.view.window?.makeFirstResponder(t.view)
    }

    func sendSnippet(at index: Int) {
        guard snippets.indices.contains(index) else { return }
        send(snippets[index])
    }

    /// ⌘+ / ⌘− / ⌘0 — czcionka wszystkich terminali (zapamiętana, jak w Windows).
    func zoomTerminal(_ delta: Int) {
        settings.terminalFontSize = delta == 0 ? 13 : MacSettings.clampFont(settings.terminalFontSize + delta)
        saveSettings()
        for case .terminal(let t) in sessions { TerminalAppearance.setFont(t.view, size: settings.terminalFontSize) }
    }

    /// ⌘F — pasek szukania w buforze aktywnego terminala (wbudowany w SwiftTerm).
    func findInTerminal() {
        guard let t = activeTerminal else { return }
        let item = NSMenuItem()
        item.tag = Int(NSTextFinder.Action.showFindInterface.rawValue)
        t.view.window?.makeFirstResponder(t.view)
        t.view.performTextFinderAction(item)
    }

    /// Druga karta tego samego serwera (np. drugi terminal obok logów).
    func duplicate(_ tab: SessionTab) {
        switch tab {
        case .terminal(let t): connect(t.server)
        case .files(let f): openFiles(f.server)
        }
    }

    /// Przeciągnięcie karty na miejsce innej.
    func moveTab(_ id: SessionTab.ID, before target: SessionTab.ID) {
        guard id != target, let from = sessions.firstIndex(where: { $0.id == id }) else { return }
        let tab = sessions.remove(at: from)
        let to = sessions.firstIndex(where: { $0.id == target }) ?? sessions.count
        sessions.insert(tab, at: to)
    }

    /// Szybkie połączenie (bez zapisywania serwera).
    func quickConnect(_ text: String) {
        guard let s = QuickConnect.server(from: text) else { return }
        connect(s)
    }

    /// Pulpit: bez zaznaczenia i bez aktywnej karty.
    func showDashboard() {
        selection = nil
        activeSessionID = nil
    }

    func openPalette(seed: String = "") {
        paletteSeed = seed
        paletteOpen = true
    }

    func installKeyMonitor() {
        NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            guard let self, event.modifierFlags.contains(.command) else { return event }
            let shift = event.modifierFlags.contains(.shift)
            let chars = event.charactersIgnoringModifiers ?? ""
            let isMain = event.window?.isKind(of: NSPanel.self) == false
            guard isMain else { return event }
            let option = event.modifierFlags.contains(.option)
            // ⌥⌘1…9 — snippet o tym numerze (⌘⇧3/4/5 to w macOS zrzuty ekranu).
            if option, !shift, let d = Int(chars), (1...9).contains(d) {
                self.sendSnippet(at: d - 1)
                return nil
            }
            if option { return event }
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
