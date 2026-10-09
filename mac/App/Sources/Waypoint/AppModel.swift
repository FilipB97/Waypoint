import AppKit
import Observation
import UniformTypeIdentifiers
import WaypointCore

/// Stan aplikacji: lista serwerów, wyszukiwanie, zaznaczenie, edytowany wpis i komunikaty.
/// Każda zmiana listy jest od razu zapisywana na dysk (jak w wersji Windows).
@MainActor
@Observable
final class AppModel {
    var servers: [Server] = []
    var query = ""
    var selection: Server.ID?
    /// Wpis otwarty w edytorze (arkusz). `isNew` rozróżnia „Dodaj" od „Edytuj".
    var editing: Server?
    var editingIsNew = false
    var alert: AppAlert?

    private let store = ServerStore(directory: ServerStore.defaultDirectory)

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

    // MARK: Łączenie

    /// Na tym etapie SSH otwiera się w systemowym Terminalu (adres ssh://). Wbudowany terminal
    /// w karcie Waypointa to następny krok — wtedy ta ścieżka zniknie.
    func connect(_ s: Server) {
        switch s.proto {
        case .ssh?:
            var c = URLComponents()
            c.scheme = "ssh"
            c.host = s.host
            if !s.username.isEmpty { c.user = s.username }
            if s.port != 22 { c.port = s.port }
            if let url = c.url { NSWorkspace.shared.open(url) }
        default:
            alert = AppAlert(title: s.displayName, message: L("connect.notyet"))
        }
    }
}

struct AppAlert: Identifiable {
    let id = UUID()
    var title: String
    var message: String
}
