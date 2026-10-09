import Foundation

/// Dziennik połączeń (`connections.log` w katalogu danych) — ten sam format linii co w Windows:
/// „yyyy-MM-dd HH:mm:ss  EVENT        nazwa (host:port) user=…". Tylko metadane, nigdy hasła.
public enum ConnectionLog {
    public static let fileName = "connections.log"

    public static func format(_ ts: Date, event: String, server: Server) -> String {
        let user = server.username.isEmpty ? "-"
            : server.domain.isEmpty ? server.username : server.domain + "\\" + server.username
        let ev = event.count >= 12 ? event : event + String(repeating: " ", count: 12 - event.count)
        return "\(timestamp(ts))  \(ev) \(sanitize(server.name)) (\(sanitize(server.host)):\(server.port)) user=\(sanitize(user))"
    }

    /// Znaki sterujące (w tym CR/LF) → spacja; puste pole → „-". Nazwa z nową linią nie podrobi wpisu.
    public static func sanitize(_ v: String) -> String {
        if v.isEmpty { return "-" }
        return String(String.UnicodeScalarView(v.unicodeScalars.map {
            CharacterSet.controlCharacters.contains($0) ? " " : $0
        }))
    }

    /// Dopisuje linię; błędy zapisu są ignorowane (dziennik to „best effort").
    public static func append(_ event: String, server: Server, dir: URL, at ts: Date = Date()) {
        let url = dir.appendingPathComponent(fileName)
        let line = Data((format(ts, event: event, server: server) + "\n").utf8)
        do {
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            if let h = try? FileHandle(forWritingTo: url) {
                defer { try? h.close() }
                try h.seekToEnd()
                try h.write(contentsOf: line)
            } else {
                try line.write(to: url, options: .atomic)
            }
        } catch {}
    }

    public static func readLines(dir: URL) -> [String] {
        guard let d = try? Data(contentsOf: dir.appendingPathComponent(fileName)) else { return [] }
        return String(decoding: d, as: UTF8.self).split(whereSeparator: \.isNewline).map(String.init)
    }

    static func timestamp(_ d: Date) -> String {
        let c = Calendar(identifier: .gregorian).dateComponents(in: .current, from: d)
        return String(format: "%04d-%02d-%02d %02d:%02d:%02d", c.year ?? 0, c.month ?? 0, c.day ?? 0,
                      c.hour ?? 0, c.minute ?? 0, c.second ?? 0)
    }

    /// Odwrotność `timestamp` (czas lokalny); nil dla linii, która nie zaczyna się od daty.
    static func parseTimestamp(_ s: Substring) -> Date? {
        let parts = s.split(whereSeparator: { $0 == "-" || $0 == " " || $0 == ":" })
        guard s.count == 19, parts.count == 6 else { return nil }
        let n = parts.compactMap { Int($0) }
        guard n.count == 6 else { return nil }
        var dc = DateComponents()
        (dc.year, dc.month, dc.day, dc.hour, dc.minute, dc.second) = (n[0], n[1], n[2], n[3], n[4], n[5])
        dc.timeZone = .current
        return Calendar(identifier: .gregorian).date(from: dc)
    }
}

/// Statystyki pulpitu z linii dziennika — port ConnectionStats z Windows (liczone tylko CONNECTED).
public struct ConnectionStats: Equatable, Sendable {
    /// Połączenia w kolejnych dniach; indeks 0 = najstarszy, ostatni = dziś.
    public var perDay: [Int]
    public var totalConnects: Int
    /// Najczęściej używane serwery (nazwa, liczba), malejąco.
    public var topServers: [(name: String, count: Int)]
    /// Wg dnia tygodnia z całego dziennika; 0 = poniedziałek … 6 = niedziela.
    public var perWeekday: [Int]

    public static func == (a: Self, b: Self) -> Bool {
        a.perDay == b.perDay && a.totalConnects == b.totalConnects && a.perWeekday == b.perWeekday
            && a.topServers.map(\.name) == b.topServers.map(\.name) && a.topServers.map(\.count) == b.topServers.map(\.count)
    }

    public static func compute(_ lines: [String], now: Date, days: Int, top: Int = 5) -> ConnectionStats {
        var cal = Calendar(identifier: .gregorian)
        cal.timeZone = .current
        var perDay = [Int](repeating: 0, count: max(1, days))
        var weekday = [Int](repeating: 0, count: 7)
        var byServer: [String: (name: String, count: Int)] = [:]
        var total = 0
        let today = cal.startOfDay(for: now)

        for line in lines where line.count >= 20 {
            guard let ts = ConnectionLog.parseTimestamp(line.prefix(19)) else { continue }
            let after = line.dropFirst(19).drop(while: { $0 == " " })
            guard let sp = after.firstIndex(of: " "), sp > after.startIndex,
                  after[..<sp].caseInsensitiveCompare("CONNECTED") == .orderedSame else { continue }
            total += 1
            weekday[(cal.component(.weekday, from: ts) + 5) % 7] += 1   // Calendar: niedziela=1
            let rest = after[sp...].drop(while: { $0 == " " })
            // Nazwa może sama zawierać „ (" — np. „sshd (Pęk kluczy)" — więc ostatnie „ (" przed „ user=".
            let head = rest.range(of: ") user=", options: .backwards).map { rest[..<$0.lowerBound] } ?? rest
            let name = (head.range(of: " (", options: .backwards).map { rest[..<$0.lowerBound] } ?? rest)
                .trimmingCharacters(in: .whitespaces)
            if !name.isEmpty {
                let key = name.lowercased()
                byServer[key] = (byServer[key]?.name ?? name, (byServer[key]?.count ?? 0) + 1)
            }
            let ago = cal.dateComponents([.day], from: cal.startOfDay(for: ts), to: today).day ?? -1
            let idx = perDay.count - 1 - ago
            if ago >= 0, idx >= 0 { perDay[idx] += 1 }
        }
        let tops = byServer.values.sorted {
            $0.count != $1.count ? $0.count > $1.count : $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending
        }.prefix(max(0, top))
        return ConnectionStats(perDay: perDay, totalConnects: total, topServers: Array(tops), perWeekday: weekday)
    }
}
