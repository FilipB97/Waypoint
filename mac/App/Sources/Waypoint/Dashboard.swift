import Charts
import SwiftUI
import WaypointCore

/// Pulpit (prawy panel bez zaznaczenia) — odpowiednik pulpitu z Windows: liczniki, szybkie połączenie,
/// ostatnio używane serwery i statystyki z dziennika połączeń.
struct Dashboard: View {
    @Environment(AppModel.self) private var model
    @State private var stats: ConnectionStats?
    @State private var quick = ""
    @State private var hoverDay: Date?

    private static let days = 14

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 22) {
                counters
                quickConnect
                if !model.recentServers.isEmpty { recents }
                if let stats, stats.totalConnects > 0 { activity(stats) }
            }
            .padding(24)
            .frame(maxWidth: 820, alignment: .leading)
            .frame(maxWidth: .infinity)
        }
        // Po każdym połączeniu (nowa karta) dziennik ma nowy wpis.
        .task(id: model.sessions.count) { stats = model.connectionStats(days: Self.days) }
    }

    private var counters: some View {
        HStack(spacing: 12) {
            Tile(value: "\(model.servers.count)", label: L("dash.servers"), symbol: "server.rack")
            if model.settings.reachabilityEnabled {
                Tile(value: model.reach.isEmpty ? "–" : "\(model.onlineCount)", label: L("dash.online"),
                     symbol: "dot.radiowaves.left.and.right")
            }
            Tile(value: "\(stats?.perDay.reduce(0, +) ?? 0)", label: String(format: L("dash.connects"), Self.days),
                 symbol: "point.3.connected.trianglepath.dotted")
        }
    }

    private var quickConnect: some View {
        HStack {
            Image(systemName: "bolt.horizontal").foregroundStyle(.secondary)
            TextField(L("dash.quick.placeholder"), text: $quick)
                .textFieldStyle(.plain)
                .onSubmit { connectQuick() }
            Button(L("act.connect")) { connectQuick() }
                .disabled(QuickConnect.server(from: quick) == nil)
        }
        .padding(10)
        .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 8))
    }

    private func connectQuick() {
        guard QuickConnect.server(from: quick) != nil else { return }
        model.quickConnect(quick)
        quick = ""
    }

    private var recents: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(L("dash.recent")).font(.headline)
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 200), spacing: 10)], spacing: 10) {
                ForEach(model.recentServers) { s in
                    Button { model.connect(s) } label: {
                        HStack(spacing: 10) {
                            Avatar(server: s, size: 30)
                                .overlay(alignment: .bottomTrailing) {
                                    if let r = model.reach[s.id] { ReachDot(reach: r).offset(x: 2, y: 2) }
                                }
                            VStack(alignment: .leading, spacing: 1) {
                                Text(s.displayName).lineLimit(1)
                                Text(s.host).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                            }
                            Spacer(minLength: 0)
                            ProtocolBadge(server: s)
                        }
                        .padding(10)
                        .contentShape(Rectangle())
                        .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 8))
                    }
                    .buttonStyle(.plain)
                    .help(String(format: L("dash.recent.help"), s.displayName))
                }
            }
        }
    }

    private struct DayCount: Identifiable {
        let day: Date
        let count: Int
        var id: Date { day }
    }

    private func days(_ stats: ConnectionStats) -> [DayCount] {
        let cal = Calendar.current
        let today = cal.startOfDay(for: Date())
        return stats.perDay.enumerated().map { i, c in
            DayCount(day: cal.date(byAdding: .day, value: i - (stats.perDay.count - 1), to: today)!, count: c)
        }
    }

    private func activity(_ stats: ConnectionStats) -> some View {
        let data = days(stats)
        let hovered = hoverDay.flatMap { d in data.first { Calendar.current.isDate($0.day, inSameDayAs: d) } }
        return VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text(String(format: L("dash.activity"), Self.days)).font(.headline)
                Spacer()
                Button(L("log.reveal")) { model.revealConnectionLog() }
                    .buttonStyle(.link)
            }
            Chart {
                ForEach(data) { d in
                    BarMark(x: .value(L("dash.day"), d.day, unit: .day), y: .value(L("dash.count"), d.count), width: .ratio(0.6))
                        .foregroundStyle(Color.accentColor.opacity(hovered == nil || hovered?.day == d.day ? 1 : 0.45))
                        .cornerRadius(4)
                }
                if let h = hovered {
                    RuleMark(x: .value(L("dash.day"), h.day, unit: .day))
                        .foregroundStyle(.clear)
                        .annotation(position: .top, overflowResolution: .init(x: .fit(to: .chart), y: .disabled)) {
                            Text("\(h.day.formatted(.dateTime.day().month())): \(h.count)")
                                .font(.caption).padding(.horizontal, 6).padding(.vertical, 3)
                                .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 5))
                        }
                }
            }
            .chartXSelection(value: $hoverDay)
            .chartYAxis { AxisMarks(values: .automatic(desiredCount: 3)) { _ in AxisGridLine(); AxisValueLabel() } }
            .chartXAxis { AxisMarks(values: .stride(by: .day, count: 2)) { _ in AxisValueLabel(format: .dateTime.day().month(.defaultDigits)) } }
            .frame(height: 140)
            .accessibilityLabel(String(format: L("dash.activity"), Self.days))

            if !stats.topServers.isEmpty {
                Text(L("dash.top")).font(.subheadline).foregroundStyle(.secondary).padding(.top, 6)
                ForEach(Array(stats.topServers.enumerated()), id: \.offset) { _, t in
                    HStack {
                        Text(t.name).lineLimit(1)
                        Spacer()
                        Text("\(t.count)").monospacedDigit().foregroundStyle(.secondary)
                    }
                    .font(.callout)
                }
            }
        }
    }
}

private struct Tile: View {
    let value: String
    let label: String
    let symbol: String

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Label(label, systemImage: symbol).font(.caption).foregroundStyle(.secondary)
            Text(value).font(.system(size: 26, weight: .semibold, design: .rounded)).monospacedDigit()
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 8))
    }
}
