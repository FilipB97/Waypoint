import SwiftUI
import WaypointCore

struct ContentView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        @Bindable var model = model
        NavigationSplitView {
            ServerSidebar()
                .navigationSplitViewColumnWidth(min: 240, ideal: 290, max: 420)
        } detail: {
            if let s = model.selected {
                ServerDetail(server: s)
            } else {
                EmptyDetail()
            }
        }
        .sheet(item: $model.editing) { s in
            ServerEditor(server: s, isNew: model.editingIsNew)
        }
        .alert(item: $model.alert) { a in
            Alert(title: Text(a.title), message: Text(a.message))
        }
    }
}

/// Prawy panel bez zaznaczenia: przy pustej liście zaprasza do dodania serwera albo importu z Windows.
private struct EmptyDetail: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        if model.servers.isEmpty {
            ContentUnavailableView {
                Label(L("empty.title"), systemImage: "server.rack")
            } description: {
                Text(L("empty.desc"))
            } actions: {
                HStack {
                    Button(L("menu.newserver")) { model.beginNew() }
                        .buttonStyle(.borderedProminent)
                    Button(L("menu.import")) { model.importProfile() }
                }
            }
        } else {
            ContentUnavailableView(L("detail.none"), systemImage: "sidebar.left")
        }
    }
}
