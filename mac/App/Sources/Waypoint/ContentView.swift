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
            VStack(spacing: 0) {
                if !model.sessions.isEmpty { SessionTabBar() }
                if let session = model.activeSession {
                    SessionContainer(session: session).id(session.id)
                } else if let s = model.selected {
                    ServerDetail(server: s)
                } else {
                    EmptyDetail()
                }
            }
        }
        // Klik w serwer na liście pokazuje jego szczegóły (karty zostają na pasku i działają dalej).
        .onChange(of: model.selection) { _, new in
            if new != nil { model.activeSessionID = nil }
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
