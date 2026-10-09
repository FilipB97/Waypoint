import Foundation

/// Import serwerów z wersji Windows: plik z „Ustawienia → Eksportuj profil…" (obiekt z polami
/// `Version`, `Settings`, `Servers`) albo sam `servers.json` (tablica). Hasła w nim nie występują —
/// na Macu podaje się je przy pierwszym połączeniu i trafiają do Pęku kluczy.
public enum ProfileImport {
    public enum Failure: Error, Equatable {
        /// To nie JSON albo JSON o obcym kształcie — nic nie importujemy (żadnego „zastąp pustką").
        case unrecognized
        /// Poprawny profil, ale bez ani jednego serwera.
        case noServers
    }

    private struct Profile: Decodable {
        let servers: [Server]?
        enum CodingKeys: String, CodingKey { case servers = "Servers" }
    }

    public static func parse(_ data: Data) throws -> [Server] {
        let decoder = JSONDecoder()
        if let list = try? decoder.decode([Server].self, from: data) {
            guard !list.isEmpty else { throw Failure.noServers }
            return list
        }
        guard let profile = try? decoder.decode(Profile.self, from: data), let servers = profile.servers else {
            throw Failure.unrecognized
        }
        guard !servers.isEmpty else { throw Failure.noServers }
        return servers
    }

    public struct MergeResult: Equatable, Sendable {
        public var servers: [Server]
        public var added: Int
        public var updated: Int
    }

    /// Dokłada zaimportowane serwery do istniejących. Ten sam identyfikator = ten sam serwer
    /// (ponowny import tego samego pliku nie tworzy duplikatów) — wpis jest podmieniany w miejscu,
    /// więc kolejność listy na Macu się nie rozjeżdża. Nowe trafiają na koniec, w kolejności z pliku.
    public static func merge(existing: [Server], imported: [Server]) -> MergeResult {
        var result = existing
        var index: [String: Int] = [:]
        for (i, s) in result.enumerated() { index[s.id] = i }
        var added = 0, updated = 0
        for s in imported {
            if let i = index[s.id] {
                if result[i] != s { result[i] = s; updated += 1 }
            } else {
                index[s.id] = result.count
                result.append(s)
                added += 1
            }
        }
        return MergeResult(servers: result, added: added, updated: updated)
    }
}
