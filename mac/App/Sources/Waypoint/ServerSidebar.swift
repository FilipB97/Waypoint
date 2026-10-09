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
                Section(isExpanded: expanded(section)) {
                    ForEach(section.servers) { s in
                        ServerRow(server: s, reach: model.reach[s.id], showLatency: model.settings.showLatency)
                            .tag(s.id)
                    }
                    .onMove { offsets, dest in model.move(in: section, from: offsets, to: dest) }
                } header: {
                    SectionHeader(title: title(for: section.kind), count: section.servers.count)
                        .contextMenu {
                            if case .group(let g) = section.kind {
                                Button(L("group.rename")) { model.promptRenameGroup(g) }
                            }
                            Button(model.isCollapsed(collapseKey(section.kind)) ? L("group.expand") : L("group.collapse")) {
                                model.setCollapsed(collapseKey(section.kind), !model.isCollapsed(collapseKey(section.kind)))
                            }
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
                Button { model.showDashboard() } label: { Label(L("dash.title"), systemImage: "house") }
                    .help(L("dash.title"))
            }
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

    /// Przy wyszukiwaniu wszystko rozwinięte — trafienie w zwiniętej grupie byłoby niewidoczne.
    private func expanded(_ section: ServerList.Section) -> Binding<Bool> {
        let key = collapseKey(section.kind)
        return Binding(get: { !model.query.isEmpty || !model.isCollapsed(key) },
                       set: { model.setCollapsed(key, !$0) })
    }

    private func collapseKey(_ kind: ServerList.Section.Kind) -> String {
        switch kind {
        case .pinned: return ":pinned"
        case .group(let g): return g
        case .ungrouped: return ":ungrouped"
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
        if s.proto?.supportedOnMac == true {
            Button(L("act.connectas")) { model.connectAsTarget = s }
        }
        if s.proto == .ssh {
            Button(L("act.files")) { model.openFiles(s) }
        }
        Divider()
        Button(L("act.edit")) { model.beginEdit(s) }
        Button(L("act.duplicate")) { model.duplicate(s) }
        Button(s.pinned ? L("act.unpin") : L("act.pin")) { model.togglePin(s) }
        Menu(L("group.moveto")) {
            ForEach(model.groupNames.filter { $0 != s.group }, id: \.self) { g in
                Button(g) { model.moveToGroup(s, group: g) }
            }
            if !s.group.isEmpty { Button(L("list.ungrouped")) { model.moveToGroup(s, group: "") } }
            Divider()
            Button(L("group.new")) { model.promptNewGroup(for: s) }
        }
        Divider()
        if s.proto == .rdp {
            Button(L("rdp.export")) { model.exportRdp(s) }
        }
        Button(L("act.copyhost")) {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(s.host, forType: .string)
        }
        Divider()
        Button(L("act.delete"), role: .destructive) { pendingDelete = s }
    }
}

private struct SectionHeader: View {
    let title: String
    let count: Int

    var body: some View {
        HStack(spacing: 6) {
            Text(title)
            Text("\(count)").foregroundStyle(.tertiary).monospacedDigit()
        }
    }
}

struct ServerRow: View {
    let server: Server
    var reach: Reach? = nil
    var showLatency = false

    var body: some View {
        HStack(spacing: 10) {
            Avatar(server: server, size: 28)
                .overlay(alignment: .bottomTrailing) {
                    if let reach { ReachDot(reach: reach).offset(x: 2, y: 2) }
                }
            VStack(alignment: .leading, spacing: 1) {
                Text(server.displayName).lineLimit(1)
                Text(server.username.isEmpty ? server.host : "\(server.username)@\(server.host)")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            Spacer(minLength: 4)
            if showLatency, case .online(let ms)? = reach {
                Text("\(ms) ms").font(.caption2).monospacedDigit().foregroundStyle(.secondary)
            }
            ProtocolBadge(server: server)
        }
        .padding(.vertical, 2)
        .help(helpText)
    }

    private var helpText: String {
        let status: String? = switch reach {
        case .online(let ms)?: String(format: L("reach.online"), ms)
        case .offline?: L("reach.offline")
        case nil: nil
        }
        return [status, server.notes.isEmpty ? nil : server.notes].compactMap { $0 }.joined(separator: "\n")
    }
}

/// Kropka dostępności na awatarze: zielona = port odpowiada, czerwona = nie. Obwódka w kolorze tła
/// odcina ją od awatara; stan jest też w podpowiedzi (nie tylko kolorem).
struct ReachDot: View {
    let reach: Reach

    var body: some View {
        Circle()
            .fill(color)
            .frame(width: 10, height: 10)
            .overlay(Circle().strokeBorder(Color(nsColor: .windowBackgroundColor), lineWidth: 2))
            .accessibilityLabel(reach == .offline ? L("reach.offline") : L("reach.online.short"))
    }

    private var color: Color {
        switch reach {
        case .online: return .green
        case .offline: return .red
        }
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
