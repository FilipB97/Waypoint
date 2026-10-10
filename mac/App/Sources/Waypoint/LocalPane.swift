import AppKit
import Observation
import SwiftUI
import UniformTypeIdentifiers
import WaypointCore

/// Lewa strona dwupanelowego menedżera plików: katalog na Macu (jak DualFilePanel w Windows).
@MainActor
@Observable
final class LocalPane {
    private(set) var dir: URL
    private(set) var entries: [LocalEntry] = []
    var selection = Set<LocalEntry.ID>()
    var showHidden = false { didSet { refresh() } }
    private(set) var error: String?

    init(dir: URL? = nil) {
        self.dir = dir ?? FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSHomeDirectory(), isDirectory: true)
        refresh()
    }

    var selected: [LocalEntry] { entries.filter { selection.contains($0.id) } }

    func navigate(_ url: URL) {
        dir = url.standardizedFileURL
        selection = []
        refresh()
    }

    func refresh() {
        do { entries = try LocalListing.list(dir, showHidden: showHidden); error = nil }
        catch { entries = []; self.error = error.localizedDescription }
        selection = selection.filter { id in entries.contains { $0.id == id } }
    }

    func up() { navigate(dir.deletingLastPathComponent()) }

    func open(_ e: LocalEntry) {
        if e.isDirectory { navigate(e.url) } else { NSWorkspace.shared.open(e.url) }
    }

    func makeDirectory(_ name: String) {
        guard !name.isEmpty, !name.contains("/") else { return }
        do { try FileManager.default.createDirectory(at: dir.appendingPathComponent(name), withIntermediateDirectories: false) }
        catch { self.error = error.localizedDescription }
        refresh()
    }

    /// Do Kosza (odwracalne), a nie trwałe usunięcie.
    func trash(_ list: [LocalEntry]) {
        for e in list {
            do { try FileManager.default.trashItem(at: e.url, resultingItemURL: nil) }
            catch { self.error = error.localizedDescription }
        }
        refresh()
    }
}

struct LocalPaneView: View {
    @Bindable var pane: LocalPane
    /// Wysłanie zaznaczonych plików na serwer (do bieżącego katalogu prawego panelu).
    let upload: ([URL]) -> Void
    let canUpload: Bool
    @State private var newFolder: String?

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 6) {
                Button { pane.up() } label: { Image(systemName: "arrow.up") }
                    .help(L("files.up")).disabled(pane.dir.path == "/")
                ScrollView(.horizontal, showsIndicators: false) {
                    HStack(spacing: 2) {
                        ForEach(Array(LocalListing.breadcrumbs(pane.dir).enumerated()), id: \.offset) { i, c in
                            if i > 0 { Image(systemName: "chevron.right").font(.caption2).foregroundStyle(.tertiary) }
                            Button(c.name) { pane.navigate(c.url) }
                                .buttonStyle(.plain)
                                .padding(.horizontal, 5).padding(.vertical, 2)
                                .background(RoundedRectangle(cornerRadius: 4).fill(Color.primary.opacity(0.05)))
                        }
                    }
                }
                Spacer(minLength: 6)
                Button { pane.refresh() } label: { Image(systemName: "arrow.clockwise") }.help(L("files.refresh"))
                Button { newFolder = "" } label: { Image(systemName: "folder.badge.plus") }.help(L("files.newfolder"))
                Button { pane.showHidden.toggle() } label: { Image(systemName: pane.showHidden ? "eye" : "eye.slash") }
                    .help(L("local.hidden"))
                Button { upload(pane.selected.map(\.url)) } label: { Image(systemName: "arrow.right.circle") }
                    .help(L("local.send.help"))
                    .disabled(pane.selected.isEmpty || !canUpload)
            }
            .buttonStyle(.borderless)
            .padding(.horizontal, 10).padding(.vertical, 6)
            Divider()
            table
            if let err = pane.error {
                Divider()
                Text(err).font(.callout).foregroundStyle(.red).lineLimit(2).padding(8)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
        .alert(L("files.newfolder"), isPresented: Binding(get: { newFolder != nil }, set: { if !$0 { newFolder = nil } })) {
            TextField(L("files.newfolder.ph"), text: Binding(get: { newFolder ?? "" }, set: { newFolder = $0 }))
            Button(L("btn.cancel"), role: .cancel) {}
            Button(L("btn.add")) { if let n = newFolder { pane.makeDirectory(n.trimmingCharacters(in: .whitespaces)) } }
        }
    }

    private var table: some View {
        Table(of: LocalEntry.self, selection: $pane.selection) {
            TableColumn(L("files.col.name")) { e in
                HStack(spacing: 6) {
                    Image(nsImage: NSWorkspace.shared.icon(forFile: e.url.path)).resizable().frame(width: 16, height: 16)
                    Text(e.name).lineLimit(1).foregroundStyle(e.isHidden ? .secondary : .primary)
                }
            }
            .width(min: 120)
            TableColumn(L("files.col.size")) { e in
                Text(e.isDirectory ? "—" : ByteCountFormatter.string(fromByteCount: Int64(e.size), countStyle: .file))
                    .foregroundStyle(.secondary).monospacedDigit()
            }
            .width(min: 56, ideal: 70, max: 100)
            TableColumn(L("files.col.modified")) { e in
                Text(e.modified.map { $0.formatted(date: .abbreviated, time: .shortened) } ?? "").foregroundStyle(.secondary)
            }
            .width(min: 90, ideal: 130, max: 170)
        } rows: {
            // Przeciągnięcie wiersza na prawy panel = wysłanie (prawy panel przyjmuje pliki z Findera tak samo).
            ForEach(pane.entries) { e in
                TableRow(e).itemProvider { NSItemProvider(object: e.url as NSURL) }
            }
        }
        .contextMenu(forSelectionType: LocalEntry.ID.self) { ids in
            let items = pane.entries.filter { ids.contains($0.id) }
            if !items.isEmpty {
                if items.count == 1, let e = items.first {
                    Button(e.isDirectory ? L("files.openfolder") : L("files.open")) { pane.open(e) }
                }
                Button(L("local.send")) { upload(items.map(\.url)) }.disabled(!canUpload)
                Button(L("local.reveal")) { NSWorkspace.shared.activateFileViewerSelecting(items.map(\.url)) }
                Divider()
                Button(L("local.trash"), role: .destructive) { pane.trash(items) }
            } else {
                Button(L("files.newfolder")) { newFolder = "" }
                Button(L("local.reveal")) { NSWorkspace.shared.activateFileViewerSelecting([pane.dir]) }
                Button(L("files.refresh")) { pane.refresh() }
            }
        } primaryAction: { ids in
            if let e = pane.entries.first(where: { ids.contains($0.id) }) { pane.open(e) }
        }
        .onAppear { pane.refresh() }
    }
}
