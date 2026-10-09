import AppKit
import WaypointCore

/// Stan sondy osiągalności jednego serwera.
enum Reach: Equatable {
    case online(Int)   // ms
    case offline
}

/// Lista serwerów: osiągalność w tle, grupy (zwijanie, zmiana nazwy, przenoszenie), kolejność,
/// ostatnio używane i dziennik połączeń.
extension AppModel {
    // MARK: Osiągalność

    /// (Re)start cyklu sondy wg ustawień — po wczytaniu i po każdej zmianie ustawień.
    func restartReachability() {
        reachTask?.cancel()
        reachTask = nil
        guard settings.reachabilityEnabled else { reach = [:]; return }
        let interval = MacSettings.clampInterval(settings.reachabilityIntervalSec)
        reachTask = Task { [weak self] in
            while !Task.isCancelled {
                await self?.probeAll()
                try? await Task.sleep(for: .seconds(interval))
            }
        }
    }

    /// Jeden cykl: wszystkie serwery naraz (najwyżej 32 sondy równocześnie, jak w Windows).
    func probeAll() async {
        let targets = servers.filter { $0.proto?.probeable ?? false }.map { ($0.id, $0.host, $0.port) }
        let timeout = TimeInterval(MacSettings.clampTimeout(settings.probeTimeoutSeconds))
        let results = await withTaskGroup(of: (String, Int?).self, returning: [String: Reach].self) { group in
            var out: [String: Reach] = [:]
            var inFlight = 0
            for (id, host, port) in targets {
                if inFlight >= 32, let (done, ms) = await group.next() {
                    out[done] = ms.map(Reach.online) ?? .offline
                    inFlight -= 1
                }
                group.addTask { (id, await Self.probe(host: host, port: port, timeout: timeout)) }
                inFlight += 1
            }
            for await (done, ms) in group { out[done] = ms.map(Reach.online) ?? .offline }
            return out
        }
        guard !Task.isCancelled else { return }
        reach = results
        let ms = results.values.compactMap { if case .online(let v) = $0 { return Double(v) } else { return nil } }
        if !ms.isEmpty {
            latencySamples.append(ms.reduce(0, +) / Double(ms.count))
            if latencySamples.count > 48 { latencySamples.removeFirst(latencySamples.count - 48) }
        }
    }

    /// Sonda blokuje wątek (connect + poll), więc idzie na kolejkę GCD, a nie na wątki współbieżności Swifta
    /// — 32 zablokowane naraz zagłodziłyby pulę kooperacyjną (ma tyle wątków, ile rdzeni).
    nonisolated static func probe(host: String, port: Int, timeout: TimeInterval) async -> Int? {
        await withCheckedContinuation { c in
            DispatchQueue.global(qos: .utility).async {
                c.resume(returning: TcpProbe.probe(host: host, port: port, timeout: timeout))
            }
        }
    }

    var onlineCount: Int { reach.values.filter { if case .online = $0 { return true } else { return false } }.count }

    // MARK: Grupy i kolejność

    func isCollapsed(_ group: String) -> Bool { settings.collapsedGroups.contains(group) }

    func setCollapsed(_ group: String, _ collapsed: Bool) {
        settings.collapsedGroups.removeAll { $0 == group }
        if collapsed { settings.collapsedGroups.append(group) }
        saveSettings()
    }

    func renameGroup(_ old: String, to new: String) {
        let n = new.trimmingCharacters(in: .whitespaces)
        guard !n.isEmpty, n != old else { return }
        servers = ServerList.renameGroup(servers, from: old, to: n)
        // Stan zwinięcia przechodzi na nową nazwę.
        if settings.collapsedGroups.contains(old) {
            settings.collapsedGroups.removeAll { $0 == old || $0 == n }
            settings.collapsedGroups.append(n)
            saveSettings()
        }
        persist()
    }

    func moveToGroup(_ s: Server, group: String) {
        servers = ServerList.moveToGroup(servers, ids: [s.id], group: group)
        persist()
    }

    func move(in section: ServerList.Section, from offsets: IndexSet, to dest: Int) {
        servers = ServerList.move(servers, in: section, from: Array(offsets), to: dest)
        persist()
    }

    /// Pytanie o nową nazwę grupy (NSAlert z polem tekstowym — w SwiftUI na macOS 14 to najprostsze).
    func promptRenameGroup(_ old: String) {
        if let n = askText(title: L("group.rename.title"), message: String(format: L("group.rename.msg"), old), value: old) {
            renameGroup(old, to: n)
        }
    }

    func promptNewGroup(for s: Server) {
        if let n = askText(title: L("group.new.title"), message: L("group.new.msg"), value: ""),
           !n.trimmingCharacters(in: .whitespaces).isEmpty {
            moveToGroup(s, group: n)
        }
    }

    private func askText(title: String, message: String, value: String) -> String? {
        let a = NSAlert()
        a.messageText = title
        a.informativeText = message
        let field = NSTextField(string: value)
        field.frame = NSRect(x: 0, y: 0, width: 260, height: 24)
        a.accessoryView = field
        a.addButton(withTitle: L("btn.ok"))
        a.addButton(withTitle: L("btn.cancel"))
        a.window.initialFirstResponder = field
        return a.runModal() == .alertFirstButtonReturn ? field.stringValue : nil
    }

    // MARK: Ostatnie i dziennik

    var recentServers: [Server] { ServerList.recents(servers, ids: settings.recentIds) }

    /// Rozpoczęcie połączenia: „ostatnie" + wpis CONNECTED w dzienniku.
    func noteConnected(_ s: Server) {
        if servers.contains(where: { $0.id == s.id }) {   // szybkie połączenie nie trafia do „ostatnich"
            settings.recordRecent(s.id)
            saveSettings()
        }
        logConnection("CONNECTED", s)
    }

    func logConnection(_ event: String, _ s: Server) {
        guard settings.connectionLogEnabled else { return }
        ConnectionLog.append(event, server: s, dir: dataDirectory)
    }

    func connectionStats(days: Int = 14) -> ConnectionStats {
        ConnectionStats.compute(ConnectionLog.readLines(dir: dataDirectory), now: Date(), days: days)
    }

    func revealConnectionLog() {
        let url = dataDirectory.appendingPathComponent(ConnectionLog.fileName)
        if FileManager.default.fileExists(atPath: url.path) {
            NSWorkspace.shared.activateFileViewerSelecting([url])
        } else {
            toast = L("log.empty")
        }
    }
}
