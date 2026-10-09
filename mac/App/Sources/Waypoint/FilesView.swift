import AppKit
import SwiftUI
import UniformTypeIdentifiers
import WaypointCore

/// Panel plików SFTP w karcie.
struct FilesView: View {
    @Bindable var session: FileSession
    @State private var newFolder: String?
    @State private var renaming: SftpEntry?
    @State private var renameText = ""
    @State private var pendingDelete: [SftpEntry] = []
    @State private var dropTargeted = false

    private var selected: [SftpEntry] { session.entries.filter { session.selection.contains($0.id) } }

    var body: some View {
        VStack(spacing: 0) {
            toolbar
            Divider()
            ZStack {
                switch session.state {
                case .ready:
                    table
                case .connecting:
                    ProgressView(L("files.connecting")).frame(maxWidth: .infinity, maxHeight: .infinity)
                case .failed(let msg):
                    ContentUnavailableView {
                        Label(L("files.failed"), systemImage: "exclamationmark.triangle")
                    } description: {
                        Text(msg).textSelection(.enabled)
                    } actions: {
                        Button(L("tab.reconnect")) { session.connect() }.buttonStyle(.borderedProminent)
                        if session.certificateProblem {
                            Button(L("files.cert.trust")) { session.trustCertificate() }
                                .help(L("files.cert.trust.help"))
                        }
                    }
                }
                if let p = session.auth.prompt {
                    Color.black.opacity(0.25)
                    PromptCard(prompt: p, serverName: session.server.displayName).id(p.id)
                }
            }
            .overlay(alignment: .top) { NoticeBadge(auth: session.auth) }
            .overlay {
                if dropTargeted {
                    RoundedRectangle(cornerRadius: 8)
                        .strokeBorder(Color.accentColor, style: StrokeStyle(lineWidth: 2, dash: [6]))
                        .padding(6)
                        .allowsHitTesting(false)
                }
            }
            Divider()
            statusBar
        }
        .navigationTitle(session.title)
        .navigationSubtitle(session.path)
        .alert(L("files.newfolder"), isPresented: Binding(get: { newFolder != nil }, set: { if !$0 { newFolder = nil } })) {
            TextField(L("files.newfolder.ph"), text: Binding(get: { newFolder ?? "" }, set: { newFolder = $0 }))
            Button(L("btn.cancel"), role: .cancel) {}
            Button(L("btn.add")) { if let n = newFolder { session.makeDirectory(n.trimmingCharacters(in: .whitespaces)) } }
        }
        .alert(L("files.rename"), isPresented: Binding(get: { renaming != nil }, set: { if !$0 { renaming = nil } })) {
            TextField("", text: $renameText)
            Button(L("btn.cancel"), role: .cancel) {}
            Button(L("btn.save")) { if let e = renaming { session.rename(e, to: renameText.trimmingCharacters(in: .whitespaces)) } }
        }
        .confirmationDialog(deleteTitle, isPresented: Binding(get: { !pendingDelete.isEmpty }, set: { if !$0 { pendingDelete = [] } })) {
            Button(L("del.confirm"), role: .destructive) { session.delete(pendingDelete) }
        } message: {
            Text(L("files.delete.msg"))
        }
        .confirmationDialog(L("files.overwrite.title"),
                            isPresented: Binding(get: { session.overwriteQuestion != nil }, set: { if !$0 { session.overwriteQuestion = nil } })) {
            Button(L("files.overwrite.confirm"), role: .destructive) {
                if let q = session.overwriteQuestion { session.upload(q.urls, confirmedOverwrite: true) }
            }
        } message: {
            Text(String(format: L("files.overwrite.msg"), session.overwriteQuestion?.names.joined(separator: ", ") ?? ""))
        }
    }

    private var deleteTitle: String {
        pendingDelete.count == 1 ? String(format: L("files.delete.one"), pendingDelete[0].name)
                                 : String(format: L("files.delete.many"), pendingDelete.count)
    }

    // MARK: Pasek narzędzi i ścieżka

    private var toolbar: some View {
        HStack(spacing: 6) {
            Button { session.up() } label: { Image(systemName: "arrow.up") }
                .help(L("files.up"))
                .disabled(session.path == "/" || session.state != .ready)
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 2) {
                    ForEach(Array(RemotePath.breadcrumbs(session.path).enumerated()), id: \.offset) { i, crumb in
                        if i > 1 { Image(systemName: "chevron.right").font(.caption2).foregroundStyle(.tertiary) }
                        Button(crumb.name) { session.navigate(crumb.path) }
                            .buttonStyle(.plain)
                            .padding(.horizontal, 5).padding(.vertical, 2)
                            .background(RoundedRectangle(cornerRadius: 4).fill(Color.primary.opacity(0.05)))
                    }
                }
            }
            Spacer(minLength: 8)
            Group {
                Button { session.refresh() } label: { Image(systemName: "arrow.clockwise") }.help(L("files.refresh"))
                Button { newFolder = "" } label: { Image(systemName: "folder.badge.plus") }.help(L("files.newfolder"))
                Button { chooseUpload() } label: { Image(systemName: "square.and.arrow.up") }.help(L("files.upload"))
                Button { chooseDownload(selected) } label: { Image(systemName: "square.and.arrow.down") }
                    .help(L("files.download")).disabled(selected.isEmpty)
                Button { pendingDelete = selected } label: { Image(systemName: "trash") }
                    .help(L("files.delete")).disabled(selected.isEmpty)
            }
            .disabled(session.state != .ready || session.busy)
        }
        .buttonStyle(.borderless)
        .padding(.horizontal, 10).padding(.vertical, 6)
    }

    // MARK: Tabela

    private var table: some View {
        Table(session.entries, selection: $session.selection) {
            TableColumn(L("files.col.name")) { e in
                HStack(spacing: 6) {
                    Image(nsImage: icon(for: e)).resizable().frame(width: 16, height: 16)
                    Text(e.name).lineLimit(1)
                    if e.attributes.isSymlink { Image(systemName: "arrow.turn.up.right").font(.caption2).foregroundStyle(.secondary) }
                }
            }
            .width(min: 140)   // bez „ideal" — nazwa bierze resztę szerokości
            TableColumn(L("files.col.size")) { e in
                Text(e.isDirectory ? "—" : ByteCountFormatter.string(fromByteCount: Int64(e.size), countStyle: .file))
                    .foregroundStyle(.secondary).monospacedDigit()
            }
            .width(min: 60, ideal: 76, max: 110)
            TableColumn(L("files.col.modified")) { e in
                Text(e.attributes.modified.map { $0.formatted(date: .abbreviated, time: .shortened) } ?? "")
                    .foregroundStyle(.secondary)
            }
            .width(min: 100, ideal: 150, max: 190)
            TableColumn(L("files.col.mode")) { e in
                Text(e.attributes.mode.map { UnixPermissions.symbolic(Int($0), directory: e.attributes.isDirectory) } ?? "")
                    .font(.system(.body, design: .monospaced))
                    .foregroundStyle(.secondary)
            }
            .width(min: 86, ideal: 96, max: 120)
        }
        .contextMenu(forSelectionType: SftpEntry.ID.self) { ids in
            let items = session.entries.filter { ids.contains($0.id) }
            if items.count == 1, let e = items.first {
                Button(e.isDirectory ? L("files.openfolder") : L("files.open")) { session.open(e) }
                if !e.isDirectory {
                    Button(L("files.edit")) { edit(e) }
                }
                Button(L("files.rename")) { renameText = e.name; renaming = e }
                Button(L("files.copypath")) {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(e.path, forType: .string)
                }
                Divider()
            }
            if !items.isEmpty {
                Button(L("files.download")) { chooseDownload(items) }
                Button(L("files.delete"), role: .destructive) { pendingDelete = items }
            } else {
                Button(L("files.newfolder")) { newFolder = "" }
                Button(L("files.upload")) { chooseUpload() }
                Button(L("files.refresh")) { session.refresh() }
            }
        } primaryAction: { ids in
            if let e = session.entries.first(where: { ids.contains($0.id) }) { session.open(e) }
        }
        .onDeleteCommand { if !selected.isEmpty { pendingDelete = selected } }
        .onKeyPress(characters: ["e"], phases: .down) { press in
            guard press.modifiers == .command, selected.count == 1, let e = selected.first, !e.isDirectory else { return .ignored }
            edit(e)
            return .handled
        }
        .dropDestination(for: URL.self) { urls, _ in
            let files = urls.filter(\.isFileURL)
            session.upload(files)
            return !files.isEmpty
        } isTargeted: { dropTargeted = $0 }
    }

    private func edit(_ e: SftpEntry) {
        let server = session.server
        session.readForEdit(e) { payload in
            EditorWindowController.show(server: server, entry: e, payload: payload)
        }
    }

    private func icon(for e: SftpEntry) -> NSImage {
        if e.isDirectory { return NSWorkspace.shared.icon(for: .folder) }
        let ext = (e.name as NSString).pathExtension
        return NSWorkspace.shared.icon(for: UTType(filenameExtension: ext) ?? .data)
    }

    // MARK: Pasek stanu

    private var statusBar: some View {
        HStack(spacing: 10) {
            if let t = session.transfer {
                Text(t.label).lineLimit(1)
                if t.total > 0 {
                    ProgressView(value: Double(min(t.done, t.total)), total: Double(t.total)).frame(maxWidth: 220)
                    Text("\(ByteCountFormatter.string(fromByteCount: Int64(t.done), countStyle: .file)) / \(ByteCountFormatter.string(fromByteCount: Int64(t.total), countStyle: .file))")
                        .monospacedDigit().foregroundStyle(.secondary)
                } else {
                    ProgressView().controlSize(.small)
                }
                Button(L("btn.cancel")) { session.cancelTransfer() }
            } else if let m = session.message {
                Text(m).foregroundStyle(session.messageIsError ? Color.red : Color.secondary).lineLimit(1)
                    .textSelection(.enabled)
            } else if session.state == .ready {
                Text(String(format: L("files.count"), session.entries.count)).foregroundStyle(.secondary)
            }
            Spacer()
            if session.busy && session.transfer == nil { ProgressView().controlSize(.small) }
        }
        .font(.callout)
        .padding(.horizontal, 12).padding(.vertical, 6)
    }

    // MARK: Okna wyboru

    private func chooseUpload() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.allowsMultipleSelection = true
        panel.prompt = L("files.upload.button")
        if panel.runModal() == .OK { session.upload(panel.urls) }
    }

    private func chooseDownload(_ items: [SftpEntry]) {
        guard !items.isEmpty else { return }
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.directoryURL = FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask).first
        panel.prompt = L("files.download.button")
        if panel.runModal() == .OK, let dir = panel.url { session.download(items, to: dir) }
    }
}
