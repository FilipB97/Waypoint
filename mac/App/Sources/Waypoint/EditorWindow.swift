import AppKit
import SwiftUI
import WaypointCore

/// Okno edytora pliku — osobne okno macOS (można je postawić obok terminala, ma własny przycisk
/// w Docku przez menu Okno). Zamknięcie z niezapisanymi zmianami pyta o zapis.
@MainActor
final class EditorWindowController: NSWindowController, NSWindowDelegate {
    static var open: [EditorWindowController] = []

    let doc: EditorDocument

    static func show(server: Server, entry: SftpEntry, payload: FileSession.EditPayload) {
        // Ten sam plik z tego samego serwera — przywróć okno zamiast otwierać drugie.
        if let w = open.first(where: { $0.doc.server.id == server.id && $0.doc.requestedPath == entry.path }) {
            w.window?.makeKeyAndOrderFront(nil)
            return
        }
        let doc = EditorDocument(server: server, entry: entry, payload: payload)
        let wc = EditorWindowController(doc: doc)
        open.append(wc)
        wc.showWindow(nil)
        wc.window?.makeKeyAndOrderFront(nil)
    }

    init(doc: EditorDocument) {
        self.doc = doc
        let host = NSHostingController(rootView: EditorView(doc: doc))
        // Bez tego okno przyjmuje „naturalną" wysokość WebView (zrzut z CI: 2362 px, poza ekranem).
        host.sizingOptions = []
        let w = NSWindow(contentViewController: host)
        w.setContentSize(NSSize(width: 980, height: 680))
        w.minSize = NSSize(width: 560, height: 360)
        w.title = doc.name
        w.subtitle = "\(doc.server.displayName) — \(doc.pathLabel)"
        w.tabbingMode = .preferred   // kilka plików = karty jednego okna (⌘⇧\ pokazuje wszystkie)
        w.center()
        super.init(window: w)
        w.delegate = self
        observeTitle()
    }

    required init?(coder: NSCoder) { fatalError() }

    private func observeTitle() {
        withObservationTracking { _ = doc.dirty } onChange: { [weak self] in
            DispatchQueue.main.async {
                guard let self else { return }
                self.window?.isDocumentEdited = self.doc.dirty   // kropka w czerwonym przycisku
                self.window?.title = self.doc.name   // kropka niezapisanych zmian jest w przycisku zamykania (macOS)
                self.observeTitle()
            }
        }
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        guard doc.dirty else { return true }
        let a = NSAlert()
        a.messageText = String(format: L("edit.close.ask"), doc.name)
        a.addButton(withTitle: L("btn.save"))
        a.addButton(withTitle: L("btn.cancel"))
        a.addButton(withTitle: L("edit.discard"))
        switch a.runModal() {
        case .alertFirstButtonReturn:
            doc.closeAfterSave = { [weak sender] in sender?.close() }
            doc.saveFromButton()
            return false
        case .alertThirdButtonReturn:
            return true
        default:
            return false
        }
    }

    func windowWillClose(_ notification: Notification) {
        doc.close()
        Self.open.removeAll { $0 === self }
    }

    /// Zamknięcie aplikacji: jedno pytanie o wszystkie niezapisane pliki.
    static func confirmQuit() -> Bool {
        let dirty = open.filter { $0.doc.dirty }.count
        guard dirty > 0 else { return true }
        let a = NSAlert()
        a.messageText = L("quit.title")
        a.informativeText = String(format: L("edit.shutdown"), dirty)
        a.addButton(withTitle: L("quit.confirm"))
        a.addButton(withTitle: L("btn.cancel"))
        return a.runModal() == .alertFirstButtonReturn
    }
}

struct EditorView: View {
    @Bindable var doc: EditorDocument
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 8) {
                Text(doc.pathLabel).font(.callout).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
                    .textSelection(.enabled)
                Spacer()
                Button { doc.toggleWrap() } label: { Image(systemName: "text.word.spacing") }
                    .help(L("edit.wrap")).buttonStyle(.borderless)
                    .foregroundStyle(doc.wrap ? Color.accentColor : Color.secondary)
                Button { doc.reload() } label: { Image(systemName: "arrow.clockwise") }
                    .help(L("edit.reload")).buttonStyle(.borderless)
                Button { doc.saveCopy() } label: { Image(systemName: "square.and.arrow.down") }
                    .help(L("edit.savecopy")).buttonStyle(.borderless)
                Button(L("btn.save")) { doc.saveFromButton() }
                    .keyboardShortcut("s")
                    .buttonStyle(.borderedProminent)
                    .disabled(!doc.ready || doc.readOnly || doc.saving)
            }
            .padding(.horizontal, 12).padding(.vertical, 7)

            if doc.readOnly, let reason = doc.readOnlyReason {
                HStack(spacing: 10) {
                    Image(systemName: "lock.fill")
                    Text(reason).font(.callout).fixedSize(horizontal: false, vertical: true)
                    Spacer()
                    if doc.sudoAvailable {
                        Button(L("edit.ro.sudo")) { doc.editWithSudo() }
                    }
                    Button(L("edit.ro.anyway")) { doc.editAnyway() }
                }
                .padding(.horizontal, 12).padding(.vertical, 8)
                .background(Color.accentColor.opacity(0.12))
            }
            Divider()

            ZStack {
                EditorWebViewHost(bridge: doc.bridge)
                if !doc.ready { ProgressView(L("edit.loading")) }
                if let p = doc.connection.auth.prompt {
                    Color.black.opacity(0.25)
                    PromptCard(prompt: p, serverName: doc.server.displayName).id(p.id)
                }
            }

            Divider()
            HStack(spacing: 10) {
                if let s = doc.status {
                    Text(s).foregroundStyle(doc.statusIsError ? Color.red : Color.secondary).lineLimit(1).textSelection(.enabled)
                }
                Spacer()
                Text(info).foregroundStyle(.secondary).monospacedDigit()
            }
            .font(.callout)
            .padding(.horizontal, 12).padding(.vertical, 5)
        }
        .onChange(of: scheme) { doc.applyTheme() }
    }

    private var info: String {
        var parts = [String(format: L("edit.pos"), doc.line, doc.column) + (doc.selected > 0 ? " " + String(format: L("edit.sel"), doc.selected) : "")]
        parts.append(doc.format.encodingName)
        parts.append(doc.format.eolLabel)
        parts.append(doc.language)
        if doc.readOnly { parts.append(L("edit.ro.short")) }
        if doc.sudoMode { parts.append(L("edit.sudo.short")) }
        return parts.joined(separator: "  ·  ")
    }
}
