import AppKit
import SwiftUI
import WaypointCore

struct ServerSidebar: View {
    @Environment(AppModel.self) private var model
    @State private var pendingDelete: Server?

    var body: some View {
        @Bindable var model = model
        List(selection: $model.selection) {
            ForEach(model.sections) { section in
                Section(title(for: section.kind)) {
                    ForEach(section.servers) { s in
                        ServerRow(server: s)
                            .tag(s.id)
                    }
                }
            }
        }
        .listStyle(.sidebar)
        // Menu kontekstowe i akcja główna (dwuklik / Enter = połącz) na poziomie listy — dzięki temu
        // pojedynczy klik zaznacza od razu, bez czekania na rozpoznanie dwukliku.
        .contextMenu(forSelectionType: Server.ID.self) { ids in
            if let id = ids.first, let s = model.servers.first(where: { $0.id == id }) { menu(for: s) }
        } primaryAction: { ids in
            if let id = ids.first, let s = model.servers.first(where: { $0.id == id }) { model.connect(s) }
        }
        .searchable(text: $model.query, placement: .sidebar, prompt: Text(L("list.search")))
        .overlay {
            if !model.servers.isEmpty && model.sections.isEmpty {
                ContentUnavailableView.search(text: model.query)
            }
        }
        .toolbar {
            ToolbarItem {
                Button { model.beginNew() } label: { Label(L("menu.newserver"), systemImage: "plus") }
                    .help(L("menu.newserver"))
            }
        }
        .confirmationDialog(L("del.title"), isPresented: Binding(get: { pendingDelete != nil },
                                                                 set: { if !$0 { pendingDelete = nil } }),
                            presenting: pendingDelete) { s in
            Button(L("del.confirm"), role: .destructive) { model.delete(s) }
        } message: { s in
            Text(String(format: L("del.msg"), s.displayName))
        }
    }

    private func title(for kind: ServerList.Section.Kind) -> String {
        switch kind {
        case .pinned: return L("list.pinned")
        case .group(let g): return g
        case .ungrouped: return L("list.ungrouped")
        }
    }

    @ViewBuilder
    private func menu(for s: Server) -> some View {
        Button(L("act.connect")) { model.connect(s) }
        Divider()
        Button(L("act.edit")) { model.beginEdit(s) }
        Button(L("act.duplicate")) { model.duplicate(s) }
        Button(s.pinned ? L("act.unpin") : L("act.pin")) { model.togglePin(s) }
        Divider()
        Button(L("act.copyhost")) {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(s.host, forType: .string)
        }
        Divider()
        Button(L("act.delete"), role: .destructive) { pendingDelete = s }
    }
}

struct ServerRow: View {
    let server: Server

    var body: some View {
        HStack(spacing: 10) {
            Avatar(server: server, size: 28)
            VStack(alignment: .leading, spacing: 1) {
                Text(server.displayName).lineLimit(1)
                Text(server.username.isEmpty ? server.host : "\(server.username)@\(server.host)")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            Spacer(minLength: 4)
            ProtocolBadge(server: server)
        }
        .padding(.vertical, 2)
        .help(server.notes)
    }
}

struct ProtocolBadge: View {
    let server: Server

    var body: some View {
        Text(server.proto?.badge ?? server.protocolName)
            .font(.system(size: 9, weight: .semibold, design: .rounded))
            .padding(.horizontal, 5)
            .padding(.vertical, 2)
            .foregroundStyle(server.proto?.supportedOnMac == true ? Color.primary : Color.secondary)
            .background(.quaternary, in: RoundedRectangle(cornerRadius: 4))
    }
}

/// Kółko z inicjałami. Kolor: zapisany w serwerze (zgodny z Windows), a gdy pusty — stały kolor
/// wyliczany z nazwy grupy, żeby serwery jednej grupy miały ten sam odcień.
struct Avatar: View {
    let server: Server
    let size: CGFloat

    var body: some View {
        Circle()
            .fill(color.gradient)
            .frame(width: size, height: size)
            .overlay {
                Text(server.initials)
                    .font(.system(size: size * 0.38, weight: .semibold, design: .rounded))
                    .foregroundStyle(.white)
            }
    }

    private var color: Color {
        if let c = Color(hex: server.avatarColor) { return c }
        let palette: [Color] = [.blue, .purple, .pink, .orange, .teal, .indigo, .green, .mint]
        let key = server.group.isEmpty ? server.displayName : server.group
        let h = key.unicodeScalars.reduce(UInt32(5381)) { ($0 &* 33) &+ $1.value }
        return palette[Int(h % UInt32(palette.count))]
    }
}

extension Color {
    /// „#RRGGBB" albo „#AARRGGBB" (format WPF); nil dla pustego/niepoprawnego.
    init?(hex: String) {
        var s = hex.trimmingCharacters(in: .whitespaces)
        if s.hasPrefix("#") { s.removeFirst() }
        guard s.count == 6 || s.count == 8, let v = UInt64(s, radix: 16) else { return nil }
        let rgb = s.count == 8 ? v & 0xFFFFFF : v
        self.init(red: Double((rgb >> 16) & 0xFF) / 255, green: Double((rgb >> 8) & 0xFF) / 255,
                  blue: Double(rgb & 0xFF) / 255)
    }
}
