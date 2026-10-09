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
                    SessionContent(tab: session).id(session.id)
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
        .sheet(isPresented: $model.paletteOpen) { CommandPaletteView(seed: model.paletteSeed) }
        .sheet(isPresented: $model.snippetPickerOpen) { SnippetPickerView() }
        .sheet(isPresented: $model.snippetManagerOpen) { SnippetManagerView() }
        .sheet(isPresented: $model.profileManagerOpen) { ProfileManagerView() }
        .sheet(isPresented: $model.generatorOpen) { PasswordGeneratorView() }
        .sheet(item: $model.connectAsTarget) { s in
            ConnectAsView(server: s, login: model.resolved(s).loginText)
        }
        .alert(model.alert?.title ?? "", isPresented: Binding(get: { model.alert != nil },
                                                               set: { if !$0 { model.alert = nil } }),
               presenting: model.alert) { a in
            if let t = a.actionTitle, let act = a.action {
                Button(t) { act() }
                Button(L("btn.close"), role: .cancel) {}
            } else {
                Button("OK", role: .cancel) {}
            }
        } message: { a in
            Text(a.message)
        }
        .overlay(alignment: .bottom) {
            if let t = model.toast {
                Text(t)
                    .font(.callout)
                    .padding(.horizontal, 14).padding(.vertical, 8)
                    .background(.regularMaterial, in: Capsule())
                    .shadow(radius: 6)
                    .padding(.bottom, 18)
                    .transition(.move(edge: .bottom).combined(with: .opacity))
                    .task(id: t) {
                        try? await Task.sleep(for: .seconds(3))
                        withAnimation { model.toast = nil }
                    }
            }
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
            Dashboard()
        }
    }
}
