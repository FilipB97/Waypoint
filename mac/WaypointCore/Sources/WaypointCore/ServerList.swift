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

        var groups: [String: [Server]] = [:]
        var ungrouped: [Server] = []
        for s in visible {
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

    static func fold(_ s: String) -> String {
        // „ł" nie ma rozkładu kanonicznego, więc diacriticInsensitive go nie zdejmie — ręcznie.
        s.folding(options: [.caseInsensitive, .diacriticInsensitive], locale: nil)
            .replacingOccurrences(of: "ł", with: "l")
            .replacingOccurrences(of: "Ł", with: "l")
    }
}
