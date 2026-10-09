import Foundation

/// Wyszukiwanie i układ listy serwerów — czysta logika, którą widok tylko rysuje.
public enum ServerList {
    /// Sekcja listy: przypięte na górze, potem grupy alfabetycznie, na końcu wpisy bez grupy.
    public struct Section: Identifiable, Equatable, Sendable {
        public enum Kind: Equatable, Sendable { case pinned, group(String), ungrouped }
        public var kind: Kind
        public var servers: [Server]
        public var id: String {
            switch kind {
            case .pinned: return "\u{1}pinned"
            case .group(let g): return "g:" + g
            case .ungrouped: return "\u{2}ungrouped"
            }
        }
    }

    /// Każde słowo zapytania musi wystąpić w nazwie, hoście, loginie, grupie, tagach albo protokole.
    /// Bez rozróżniania wielkości liter i polskich znaków („lodz" znajdzie „Łódź").
    public static func matches(_ s: Server, query: String) -> Bool {
        let words = fold(query).split(whereSeparator: { $0.isWhitespace })
        guard !words.isEmpty else { return true }
        let haystack = fold(([s.name, s.host, s.username, s.group, s.protocolName,
                              s.proto?.badge ?? ""] + s.tags).joined(separator: " "))
        return words.allSatisfy { haystack.contains($0) }
    }

    public static func sections(_ servers: [Server], query: String = "") -> [Section] {
        let visible = servers.filter { matches($0, query: query) }
        var result: [Section] = []
        let pinned = visible.filter(\.pinned)
        if !pinned.isEmpty { result.append(Section(kind: .pinned, servers: pinned)) }

        // Przypięty serwer jest tylko w „Przypiętych" — nie powtarza się w swojej grupie. Dwa wiersze
        // z tym samym identyfikatorem na liście oznaczały podwójne zaznaczenie przy jednym kliknięciu.
        var groups: [String: [Server]] = [:]
        var ungrouped: [Server] = []
        for s in visible where !s.pinned {
            let g = s.group.trimmingCharacters(in: .whitespaces)
            if g.isEmpty { ungrouped.append(s) } else { groups[g, default: []].append(s) }
        }
        for g in groups.keys.sorted(by: { $0.localizedStandardCompare($1) == .orderedAscending }) {
            result.append(Section(kind: .group(g), servers: groups[g]!))
        }
        if !ungrouped.isEmpty { result.append(Section(kind: .ungrouped, servers: ungrouped)) }
        return result
    }

    /// Istniejące nazwy grup — do podpowiedzi w edytorze serwera.
    public static func groupNames(_ servers: [Server]) -> [String] {
        Array(Set(servers.map { $0.group.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }))
            .sorted { $0.localizedStandardCompare($1) == .orderedAscending }
    }

    /// Zmienia nazwę grupy dla wszystkich jej serwerów naraz. Pusta nowa nazwa = bez zmian.
    public static func renameGroup(_ servers: [Server], from old: String, to new: String) -> [Server] {
        let n = new.trimmingCharacters(in: .whitespaces)
        guard !n.isEmpty, n != old else { return servers }
        return servers.map { s in
            var s = s
            if s.group.trimmingCharacters(in: .whitespaces) == old { s.group = n }
            return s
        }
    }

    /// Przenosi serwery do grupy (pusta nazwa = „Bez grupy").
    public static func moveToGroup(_ servers: [Server], ids: Set<String>, group: String) -> [Server] {
        let g = group.trimmingCharacters(in: .whitespaces)
        return servers.map { s in
            var s = s
            if ids.contains(s.id) { s.group = g }
            return s
        }
    }

    /// Zmiana kolejności (przeciąganie): wstawia `id` przed albo za `target`. Jak w Windows upuszczenie
    /// na wiersz innej grupy przenosi do niej serwer; w „Przypiętych" — przypina go.
    public static func move(_ servers: [Server], id: String, relativeTo target: String, after: Bool) -> [Server] {
        guard id != target, let from = servers.firstIndex(where: { $0.id == id }),
              let t = servers.firstIndex(where: { $0.id == target }) else { return servers }
        var list = servers
        var moved = list.remove(at: from)
        let tgt = servers[t]
        moved.pinned = tgt.pinned
        if !tgt.pinned { moved.group = tgt.group }
        let ti = list.firstIndex(where: { $0.id == target })!
        list.insert(moved, at: after ? ti + 1 : ti)
        return list
    }

    /// Przesunięcie wewnątrz sekcji w układzie `List.onMove` (offsety i indeks docelowy w tej sekcji).
    public static func move(_ servers: [Server], in section: Section, from offsets: [Int], to dest: Int) -> [Server] {
        let ids = offsets.sorted().compactMap { section.servers.indices.contains($0) ? section.servers[$0].id : nil }
        let remaining = section.servers.enumerated().filter { !offsets.contains($0.offset) }.map(\.element.id)
        // Pozycja docelowa liczona wśród POZOSTAŁYCH wierszy sekcji.
        let before = section.servers.prefix(dest).filter { !ids.contains($0.id) }.count
        var list = servers
        if before < remaining.count {
            for id in ids { list = move(list, id: id, relativeTo: remaining[before], after: false) }
        } else if let last = remaining.last {
            for id in ids.reversed() { list = move(list, id: id, relativeTo: last, after: true) }
        }
        return list
    }

    /// Serwery z listy „ostatnich" (w jej kolejności), bez usuniętych.
    public static func recents(_ servers: [Server], ids: [String], limit: Int = 8) -> [Server] {
        Array(ids.compactMap { id in servers.first { $0.id == id } }.prefix(limit))
    }

    static func fold(_ s: String) -> String {
        // „ł" nie ma rozkładu kanonicznego, więc diacriticInsensitive go nie zdejmie — ręcznie.
        s.folding(options: [.caseInsensitive, .diacriticInsensitive], locale: nil)
            .replacingOccurrences(of: "ł", with: "l")
            .replacingOccurrences(of: "Ł", with: "l")
    }
}
