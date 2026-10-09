import AppKit
import SwiftUI
import WaypointCore

/// Paleta poleceń (⌘K): karty, serwery, akcje i szybkie połączenie w jednym polu — jak Ctrl+P w Windows.
/// Strzałki wybierają, Enter wykonuje, Esc zamyka.
struct CommandPaletteView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @State private var query: String
    @State private var selected = 0
    @FocusState private var focused: Bool

    init(seed: String) { _query = State(initialValue: seed) }

    struct Item: Identifiable {
        let id: String
        let title: String
        let subtitle: String
        let icon: String
        let section: String
        let run: () -> Void
    }

    private var items: [Item] {
        var out: [(Int, Item)] = []
        let q = query.trimmingCharacters(in: .whitespaces)
        func add(_ score: Int, _ item: Item) { if q.isEmpty || score >= 0 { out.append((score, item)) } }

        for tab in model.sessions {
            let s = max(CommandPalette.score(tab.title, q), CommandPalette.score(tab.server.host, q))
            add(s + 50, Item(id: "t" + tab.id.uuidString, title: tab.title, subtitle: tab.server.host, icon: tab.systemImage,
                             section: L("pal.tabs")) { model.activeSessionID = tab.id })
        }
        for srv in model.servers {
            let s = [srv.displayName, srv.host, srv.group].map { CommandPalette.score($0, q) }.max() ?? -1
            add(s, Item(id: "s" + srv.id, title: srv.displayName, subtitle: "\(srv.proto?.badge ?? srv.protocolName) · \(srv.host)",
                        icon: "server.rack", section: L("pal.servers")) { model.connect(srv) })
        }
        let actions: [(String, String, () -> Void)] = [
            (L("menu.newserver"), "plus", { model.beginNew() }),
            (L("menu.import"), "square.and.arrow.down", { model.importProfile() }),
            (L("snip.menu.pick"), "text.badge.plus", { model.snippetPickerOpen = true }),
            (L("snip.menu.manage"), "list.bullet.rectangle", { model.snippetManagerOpen = true }),
            (L("term.find"), "magnifyingglass", { model.findInTerminal() }),
        ]
        for (t, icon, run) in actions {
            add(CommandPalette.score(t, q) - 100, Item(id: "a" + t, title: t, subtitle: "", icon: icon, section: L("pal.actions"), run: run))
        }
        var sorted = out.sorted { $0.0 > $1.0 }.map(\.1)
        // Szybkie połączenie: tekst wygląda jak adres, a nie ma takiego serwera na liście.
        if !q.isEmpty, let srv = QuickConnect.server(from: q), !model.servers.contains(where: { $0.host == srv.host }) {
            let label = String(format: L("pal.quick"), q)
            sorted.insert(Item(id: "q", title: label, subtitle: "\(srv.proto?.badge ?? "") · \(srv.host):\(srv.port)",
                               icon: "bolt.horizontal", section: L("pal.quick.section")) { model.quickConnect(q) }, at: 0)
        }
        return Array(sorted.prefix(40))
    }

    var body: some View {
        let list = items
        VStack(spacing: 0) {
            HStack(spacing: 8) {
                Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                TextField(L("pal.placeholder"), text: $query)
                    .textFieldStyle(.plain)
                    .font(.title3)
                    .focused($focused)
                    .onSubmit { run(list) }
                    .onKeyPress(.downArrow) { selected = min(selected + 1, max(list.count - 1, 0)); return .handled }
                    .onKeyPress(.upArrow) { selected = max(selected - 1, 0); return .handled }
                    .onKeyPress(.escape) { dismiss(); return .handled }
            }
            .padding(14)
            Divider()
            ScrollViewReader { proxy in
                List {
                    ForEach(Array(list.enumerated()), id: \.element.id) { i, item in
                        HStack(spacing: 10) {
                            Image(systemName: item.icon).frame(width: 18).foregroundStyle(.secondary)
                            VStack(alignment: .leading, spacing: 1) {
                                Text(item.title).lineLimit(1)
                                if !item.subtitle.isEmpty {
                                    Text(item.subtitle).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                                }
                            }
                            Spacer()
                            Text(item.section).font(.caption2).foregroundStyle(.tertiary)
                        }
                        .padding(.vertical, 3)
                        .listRowBackground(i == selected ? Color.accentColor.opacity(0.18) : Color.clear)
                        .contentShape(Rectangle())
                        .onTapGesture { selected = i; run(list) }
                        .id(item.id)
                    }
                }
                .listStyle(.plain)
                .onChange(of: selected) { _, new in if list.indices.contains(new) { proxy.scrollTo(list[new].id) } }
            }
        }
        .frame(width: 560, height: 420)
        .onChange(of: query) { selected = 0 }
        .onAppear { focused = true }
    }

    private func run(_ list: [Item]) {
        guard list.indices.contains(selected) else { return }
        let item = list[selected]
        dismiss()
        DispatchQueue.main.async { item.run() }
    }
}

/// Wybór snippetu do wysłania (⌘⇧K) — filtr, strzałki, Enter.
struct SnippetPickerView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @State private var query = ""
    @State private var selected = 0
    @FocusState private var focused: Bool

    private var list: [(Int, CommandSnippet)] {
        let q = query.trimmingCharacters(in: .whitespaces)
        return model.snippets.enumerated().map { ($0.offset, $0.element) }
            .filter { q.isEmpty || CommandPalette.score($0.1.displayName, q) >= 0 || CommandPalette.score($0.1.command, q) >= 0 }
    }

    var body: some View {
        let items = list
        VStack(spacing: 0) {
            TextField(L("snip.filter"), text: $query)
                .textFieldStyle(.plain).font(.title3).padding(14)
                .focused($focused)
                .onSubmit { send(items) }
                .onKeyPress(.downArrow) { selected = min(selected + 1, max(items.count - 1, 0)); return .handled }
                .onKeyPress(.upArrow) { selected = max(selected - 1, 0); return .handled }
            Divider()
            if model.snippets.isEmpty {
                ContentUnavailableView {
                    Label(L("snip.empty"), systemImage: "text.badge.plus")
                } description: { Text(L("snip.empty.desc")) } actions: {
                    Button(L("snip.menu.manage")) { dismiss(); model.snippetManagerOpen = true }
                }
            } else {
                List {
                    ForEach(Array(items.enumerated()), id: \.element.1.id) { i, pair in
                        let (index, s) = pair
                        HStack {
                            VStack(alignment: .leading, spacing: 2) {
                                Text(s.displayName)
                                Text(SnippetVars.expand(s.command, server: model.activeTerminal?.server))
                                    .font(.system(.caption, design: .monospaced)).foregroundStyle(.secondary).lineLimit(2)
                            }
                            Spacer()
                            if index < 9 { Text("⌥⌘\(index + 1)").font(.caption).foregroundStyle(.tertiary) }
                            if !s.sendEnter { Image(systemName: "return").foregroundStyle(.tertiary).help(L("snip.noenter")) }
                        }
                        .padding(.vertical, 2)
                        .listRowBackground(i == selected ? Color.accentColor.opacity(0.18) : Color.clear)
                        .contentShape(Rectangle())
                        .onTapGesture { selected = i; send(items) }
                    }
                }
                .listStyle(.plain)
            }
            Divider()
            HStack {
                Button(L("snip.menu.manage")) { dismiss(); model.snippetManagerOpen = true }
                Spacer()
                Text(model.activeTerminal.map { String(format: L("snip.target"), $0.title) } ?? L("snip.noterminal"))
                    .font(.caption).foregroundStyle(.secondary)
            }
            .padding(10)
        }
        .frame(width: 520, height: 400)
        .onChange(of: query) { selected = 0 }
        .onAppear { focused = true }
    }

    private func send(_ items: [(Int, CommandSnippet)]) {
        guard items.indices.contains(selected) else { return }
        let s = items[selected].1
        dismiss()
        DispatchQueue.main.async { model.send(s) }
    }
}

/// Zarządzanie snippetami: lista + edycja (nazwa, treść, Enter na końcu, zmienne).
struct SnippetManagerView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @State private var list: [CommandSnippet] = []
    @State private var selection: CommandSnippet.ID?

    var body: some View {
        VStack(spacing: 0) {
            HSplitView {
                List(selection: $selection) {
                    ForEach(list) { s in Text(s.displayName.isEmpty ? L("snip.new") : s.displayName).tag(s.id) }
                        .onMove { list.move(fromOffsets: $0, toOffset: $1) }
                }
                .frame(minWidth: 180, idealWidth: 200)
                editor.frame(minWidth: 360)
            }
            Divider()
            HStack {
                Button { let s = CommandSnippet(); list.append(s); selection = s.id } label: { Image(systemName: "plus") }
                Button { list.removeAll { $0.id == selection }; selection = list.first?.id } label: { Image(systemName: "minus") }
                    .disabled(selection == nil)
                Spacer()
                Text(L("snip.hint")).font(.caption).foregroundStyle(.secondary)
                Spacer()
                Button(L("btn.cancel"), role: .cancel) { dismiss() }.keyboardShortcut(.cancelAction)
                Button(L("btn.save")) { model.saveSnippets(list); dismiss() }.keyboardShortcut(.defaultAction)
            }
            .padding(12)
        }
        .frame(width: 720, height: 460)
        .onAppear { list = model.snippets; selection = list.first?.id }
    }

    @ViewBuilder private var editor: some View {
        if let i = list.firstIndex(where: { $0.id == selection }) {
            Form {
                TextField(L("snip.name"), text: $list[i].name, prompt: Text(SnippetStore.firstLine(list[i].command)))
                VStack(alignment: .leading, spacing: 6) {
                    Text(L("snip.command"))
                    TextEditor(text: $list[i].command)
                        .font(.system(.body, design: .monospaced))
                        .frame(minHeight: 140)
                        .overlay(RoundedRectangle(cornerRadius: 6).stroke(.quaternary))
                    HStack(spacing: 4) {
                        Text(L("snip.vars")).font(.caption).foregroundStyle(.secondary)
                        ForEach(SnippetVars.names, id: \.self) { n in
                            Button("{\(n)}") { list[i].command += "{\(n)}" }.buttonStyle(.link).font(.caption.monospaced())
                        }
                    }
                }
                Toggle(L("snip.sendenter"), isOn: $list[i].sendEnter)
            }
            .formStyle(.grouped)
        } else {
            ContentUnavailableView(L("snip.empty"), systemImage: "text.badge.plus")
        }
    }
}
