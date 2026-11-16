import Foundation

/// Linki do połączeń: `waypoint://connect/<nazwa albo id>` — dla aplikacji Skróty („Otwórz URL"),
/// Terminala (`open waypoint://…`) i zakładek. Wyszukiwanie: id, potem nazwa, potem host (bez wielkości liter).
public enum ServerLink {
    public static let scheme = "waypoint"

    /// Link po nazwie (czytelny w Skrótach); przy powtarzającej się nazwie — po id.
    public static func url(for s: Server, among all: [Server]) -> URL? {
        let name = s.displayName
        let unique = all.filter { $0.displayName.caseInsensitiveCompare(name) == .orderedSame }.count <= 1
        let key = unique && !name.isEmpty ? name : s.id
        guard let enc = key.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed.subtracting(CharacterSet(charactersIn: "/"))) else { return nil }
        return URL(string: "\(scheme)://connect/\(enc)")
    }

    /// Klucz serwera z linku (`waypoint://connect/X`, `waypoint://connect?name=X`); nil, gdy to nie link połączenia.
    public static func query(from url: URL) -> String? {
        guard url.scheme?.lowercased() == scheme,
              let comps = URLComponents(url: url, resolvingAgainstBaseURL: false),
              comps.host?.lowercased() == "connect" else { return nil }
        if let q = comps.queryItems?.first(where: { ["name", "id", "server"].contains($0.name.lowercased()) })?.value,
           !q.isEmpty { return q }
        let path = comps.path.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        return path.isEmpty ? nil : path   // URLComponents.path jest już odkodowana
    }

    public static func find(_ key: String, in servers: [Server]) -> Server? {
        let k = key.trimmingCharacters(in: .whitespaces)
        if let s = servers.first(where: { $0.id == k }) { return s }
        if let s = servers.first(where: { $0.displayName.caseInsensitiveCompare(k) == .orderedSame }) { return s }
        return servers.first { $0.host.caseInsensitiveCompare(k) == .orderedSame }
    }
}
