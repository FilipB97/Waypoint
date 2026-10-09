import AppKit
import Observation
import WaypointCore

/// Plik otwarty w edytorze. Zapis idzie WŁASNYM połączeniem (osobna sesja SFTP), więc działa także po
/// zamknięciu karty plików i nie czeka na trwający tam transfer. Polityka zapisu jak w wersji Windows:
/// format pliku wraca taki, jaki był; zmiana na serwerze od otwarcia → pytanie; zapis bezpieczny
/// (plik tymczasowy + atomowa podmiana); po błędzie tekst zostaje, a komunikat mówi, czy plik jest cały.
@MainActor
@Observable
final class EditorDocument: Identifiable {
    let id = UUID()
    let server: Server
    let name: String
    let requestedPath: String
    let connection: FileSession
    let bridge = EditorBridge()

    private(set) var opened: RemoteFileInfo
    private(set) var format: TextFileFormat
    private(set) var language: String
    private(set) var dirty = false
    private(set) var saving = false
    private(set) var ready = false
    var readOnly: Bool
    var wrap = false
    var line = 1
    var column = 1
    var selected = 0
    var status: String?
    var statusIsError = false
    /// Właściciel/uprawnienia do paska „tylko do odczytu".
    let readOnlyReason: String?

    @ObservationIgnored private var savedBytes: Data
    @ObservationIgnored private var initialText: String?
    @ObservationIgnored private var textRequest: ((String?) -> Void)?
    /// Wołane po udanym zapisie przy zamykaniu okna.
    @ObservationIgnored var closeAfterSave: (() -> Void)?

    init(server: Server, entry: SftpEntry, payload: FileSession.EditPayload) {
        self.server = server
        self.name = entry.name
        self.requestedPath = entry.path
        self.connection = FileSession(server: server)
        self.opened = payload.info
        self.savedBytes = payload.data
        let (fmt, text) = TextFileFormat.detect(payload.data)
        self.format = fmt
        self.initialText = text
        self.language = EditorLanguage.language(for: entry.name, firstLine: text.split(separator: "\n", maxSplits: 1).first.map(String.init))
        if payload.info.likelyWritable(by: payload.myUid) == false {
            readOnly = true
            let owner = payload.info.uid == 0 ? "root" : "uid \(payload.info.uid ?? 0)"
            readOnlyReason = String(format: L("edit.ro.text"), owner, UnixPermissions.symbolic(Int(payload.info.mode ?? 0)))
        } else {
            readOnly = false
            readOnlyReason = nil
        }
        if fmt.encodingName == TextFileFormat.latin1 { status = L("edit.note.latin1") }
        else if fmt.mixedEol { status = String(format: L("edit.note.mixed"), fmt.eolLabel) }
        bridge.onMessage = { [weak self] m in self?.handle(m) }
    }

    var title: String { (dirty ? "● " : "") + name }
    var pathLabel: String { opened.path == requestedPath ? opened.path : "\(requestedPath)  →  \(opened.path)" }

    // MARK: Strona edytora

    static func theme(dark: Bool) -> [String: Any] {
        dark ? ["base": "vs-dark", "colors": ["editor.background": "#0F1117", "editorGutter.background": "#0F1117",
                                            "editor.foreground": "#E7E8EE", "editorCursor.foreground": "#7AA2FF",
                                            "editor.selectionBackground": "#7AA2FF55", "editor.lineHighlightBackground": "#FFFFFF0A",
                                            "editor.lineHighlightBorder": "#00000000"]]
             : ["base": "vs", "colors": ["editor.lineHighlightBorder": "#00000000"]]
    }

    private var isDark: Bool {
        NSApp.effectiveAppearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
    }

    func applyTheme() { bridge.post(["t": "theme", "theme": Self.theme(dark: isDark)]) }

    private func handle(_ m: [String: Any]) {
        switch m["t"] as? String {
        case "ready":
            ready = true
            bridge.post(["t": "open", "text": initialText ?? "", "lang": language, "name": name,
                         "readOnly": readOnly, "fontSize": 13, "wrap": wrap, "theme": Self.theme(dark: isDark)])
            initialText = nil
        case "save":
            if let text = m["text"] as? String { save(text) }
        case "text":
            textRequest?(m["text"] as? String)
            textRequest = nil
        case "dirty":
            dirty = (m["v"] as? Bool) ?? dirty
        case "cursor":
            line = (m["ln"] as? Int) ?? line
            column = (m["col"] as? Int) ?? column
            selected = (m["sel"] as? Int) ?? 0
        default: break
        }
    }

    func requestText(_ completion: @escaping (String?) -> Void) {
        guard ready else { completion(nil); return }
        textRequest?(nil)
        textRequest = completion
        bridge.post(["t": "getText"])
    }

    func toggleWrap() { wrap.toggle(); bridge.post(["t": "wrap", "v": wrap]) }

    func editAnyway() {
        readOnly = false
        bridge.post(["t": "readOnly", "v": false])
        bridge.post(["t": "focus"])
    }

    // MARK: Zapis

    func saveFromButton() { requestText { [weak self] t in if let t { self?.save(t) } } }

    func save(_ text: String) {
        guard !saving else { return }
        if readOnly { setStatus(L("edit.ro.status"), error: true); return }

        var fmt = format
        var bytes: Data
        switch fmt.encode(text) {
        case .ok(let d): bytes = d
        case .unencodable(let ch):
            guard confirm(String(format: L("edit.latin1.ask"), ch)) else { return }
            fmt = fmt.asUTF8()
            guard case .ok(let d) = fmt.encode(text) else { return }
            bytes = d
        }
        if bytes == savedBytes {
            bridge.post(["t": "saved"])
            setStatus(L("edit.nochange"))
            finishCloseIfRequested()
            return
        }

        saving = true
        setStatus(L("edit.saving"))
        let path = opened.path
        let openedInfo = opened
        connection.perform({ c -> (RemoteFileInfo, Bool) in
            let now = try SafeWrite.stat(c, path)
            return (now, RemoteFileInfo.changedSince(openedInfo, now))
        }) { [weak self] r in
            guard let self else { return }
            switch r {
            case .failure(let e):
                self.saving = false
                self.fail(e)
            case .success(let (now, changed)):
                if changed && !self.confirm(String(format: L("edit.conflict"), self.name,
                                                   Date(timeIntervalSince1970: TimeInterval(now.modified)).formatted(date: .abbreviated, time: .shortened),
                                                   ByteCountFormatter.string(fromByteCount: Int64(now.length), countStyle: .file))) {
                    self.saving = false
                    self.setStatus(L("edit.conflict.kept"), error: true)
                    return
                }
                // Metadane ŚWIEŻE (now) — jeśli ktoś zmienił uprawnienia, zapis zachowa obecne.
                self.connection.perform({ c -> (SafeWriteMode, RemoteFileInfo?) in
                    let mode = try SafeWrite.write(c, content: bytes, original: now)
                    return (mode, try? SafeWrite.stat(c, path))
                }) { [weak self] r2 in
                    guard let self else { return }
                    self.saving = false
                    switch r2 {
                    case .failure(let e): self.fail(e)
                    case .success(let (mode, after)):
                        self.opened = after ?? now
                        self.format = fmt
                        self.savedBytes = bytes
                        self.bridge.post(["t": "saved"])
                        let when = Date().formatted(date: .omitted, time: .standard)
                        switch mode {
                        case .atomicReplace: self.setStatus(String(format: L("edit.saved"), when))
                        case .inPlace(let reason): self.setStatus(String(format: L("edit.saved.inplace"), when, L(reason)))
                        }
                        self.finishCloseIfRequested()
                    }
                }
            }
        }
    }

    private func finishCloseIfRequested() {
        let c = closeAfterSave
        closeAfterSave = nil
        c?()
    }

    private func fail(_ error: Error) {
        closeAfterSave = nil
        let msg: String
        if let e = error as? SafeWriteError {
            msg = e.originalMayBeDamaged ? String(format: L("edit.fail.damaged"), FileSession.describe(e.underlying))
                : e.isPermissionDenied ? L("edit.fail.denied")
                : String(format: L("edit.fail.safe"), FileSession.describe(e.underlying))
            setStatus(e.originalMayBeDamaged ? L("edit.fail.damaged.short") : L("edit.fail.short"), error: true)
        } else {
            msg = String(format: L("edit.fail.safe"), FileSession.describe(error))
            setStatus(L("edit.fail.short"), error: true)
        }
        let a = NSAlert()
        a.messageText = name
        a.informativeText = msg
        a.alertStyle = (error as? SafeWriteError)?.originalMayBeDamaged == true ? .critical : .warning
        a.runModal()
        if case .failed = connection.state { connection.connect() }   // następny zapis połączy się od nowa
    }

    // MARK: Inne

    func reload() {
        if dirty && !confirm(L("edit.reload.ask")) { return }
        setStatus(L("edit.reloading"))
        let path = opened.path
        connection.perform({ c -> (RemoteFileInfo, Data) in
            let info = try SafeWrite.stat(c, path)
            return (info, try c.readFile(path, limit: Int(FileSession.editMaxBytes)))
        }) { [weak self] r in
            guard let self else { return }
            switch r {
            case .failure(let e): self.setStatus(FileSession.describe(e), error: true)
            case .success(let (info, data)):
                self.opened = info
                self.savedBytes = data
                let (fmt, text) = TextFileFormat.detect(data)
                self.format = fmt
                self.bridge.post(["t": "reset", "text": text])
                self.setStatus(String(format: L("edit.reloaded"), Date().formatted(date: .omitted, time: .standard)))
            }
        }
    }

    func saveCopy() {
        requestText { [weak self] text in
            guard let self, let text else { return }
            let panel = NSSavePanel()
            panel.nameFieldStringValue = self.name
            guard panel.runModal() == .OK, let url = panel.url else { return }
            let data: Data
            switch self.format.encode(text) {
            case .ok(let d): data = d
            case .unencodable: data = { if case .ok(let d) = self.format.asUTF8().encode(text) { return d }; return Data() }()
            }
            do { try data.write(to: url, options: .atomic); self.setStatus(String(format: L("edit.copysaved"), url.path)) }
            catch { self.setStatus(error.localizedDescription, error: true) }
        }
    }

    func close() {
        textRequest?(nil)
        textRequest = nil
        bridge.teardown()
        connection.close()
    }

    func setStatus(_ s: String, error: Bool = false) {
        status = s
        statusIsError = error
    }

    private func confirm(_ text: String) -> Bool {
        let a = NSAlert()
        a.messageText = name
        a.informativeText = text
        a.addButton(withTitle: L("edit.yes"))
        a.addButton(withTitle: L("btn.cancel"))
        return a.runModal() == .alertFirstButtonReturn
    }
}
