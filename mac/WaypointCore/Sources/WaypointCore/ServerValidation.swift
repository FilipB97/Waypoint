import Foundation

/// Sprawdzenie formularza serwera przed zapisem. Zwraca klucze tekstów (patrz `Strings`), żeby
/// logika nie zależała od języka interfejsu.
public enum ServerValidation {
    public static func problems(_ s: Server) -> [String] {
        var out: [String] = []
        if s.host.trimmingCharacters(in: .whitespaces).isEmpty { out.append("edit.err.host") }
        else if s.host.contains(where: { $0.isWhitespace }) { out.append("edit.err.hostspace") }
        else if s.proto == .http && ExternalLinks.webURL(s.host) == nil { out.append("edit.err.url") }
        if s.proto == .serial { if s.port <= 0 { out.append("edit.err.baud") } }
        else if s.proto != .http && !(1...65535).contains(s.port) { out.append("edit.err.port") }
        return out
    }

    /// Porządkuje pola przed zapisem: obcina spacje, usuwa puste i powtórzone tagi.
    public static func normalized(_ s: Server) -> Server {
        var r = s
        r.name = s.name.trimmingCharacters(in: .whitespacesAndNewlines)
        r.host = s.host.trimmingCharacters(in: .whitespacesAndNewlines)
        r.username = s.username.trimmingCharacters(in: .whitespacesAndNewlines)
        r.group = s.group.trimmingCharacters(in: .whitespacesAndNewlines)
        var seen = Set<String>()
        r.tags = s.tags.map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty && seen.insert($0.lowercased()).inserted }
        return r
    }
}
