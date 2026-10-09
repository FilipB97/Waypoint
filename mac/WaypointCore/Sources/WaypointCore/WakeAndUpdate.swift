import Foundation
#if canImport(Darwin)
import Darwin
#elseif canImport(Glibc)
import Glibc
#endif

/// Wake-on-LAN — port z Windows: MAC w formatach AA:BB:…, AA-BB-…, AABB.CCDD.EEFF, 12 hex; magic packet
/// (6×FF + 16× MAC) broadcastem UDP na porty 9 i 7.
public enum WakeOnLan {
    public static func parseMac(_ text: String) -> [UInt8]? {
        let hex = text.filter { !":-. ".contains($0) }.trimmingCharacters(in: .whitespaces)
        guard hex.count == 12 else { return nil }
        var out: [UInt8] = []
        var i = hex.startIndex
        for _ in 0..<6 {
            let j = hex.index(i, offsetBy: 2)
            guard let b = UInt8(hex[i..<j], radix: 16) else { return nil }
            out.append(b)
            i = j
        }
        return out
    }

    public static func magicPacket(_ mac: [UInt8]) -> [UInt8] {
        precondition(mac.count == 6)
        return [UInt8](repeating: 0xFF, count: 6) + (0..<16).flatMap { _ in mac }
    }

    /// Wysyła pakiet na `address` (domyślnie 255.255.255.255) na porty 9 i 7. Zwraca opis błędu albo nil.
    public static func send(_ mac: [UInt8], address: String = "255.255.255.255") -> String? {
        #if canImport(Glibc)
        let fd = socket(AF_INET, Int32(SOCK_DGRAM.rawValue), 0)
        #else
        let fd = socket(AF_INET, SOCK_DGRAM, 0)
        #endif
        guard fd >= 0 else { return String(cString: strerror(errno)) }
        defer { close(fd) }
        var one: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_BROADCAST, &one, socklen_t(MemoryLayout<Int32>.size))
        let packet = magicPacket(mac)
        for port in [9, 7] as [UInt16] {
            var a = sockaddr_in()
            a.sin_family = sa_family_t(AF_INET)
            a.sin_port = port.bigEndian
            a.sin_addr.s_addr = inet_addr(address)
            let n = withUnsafePointer(to: &a) { p in
                p.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    sendto(fd, packet, packet.count, 0, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
                }
            }
            if n < 0 { return String(cString: strerror(errno)) }
        }
        return nil
    }
}

extension Server {
    /// Adres MAC do Wake-on-LAN (pole Windows `MacAddress`).
    public var macAddress: String {
        get { if case .string(let s)? = extra["MacAddress"] { return s } else { return "" } }
        set {
            let v = newValue.trimmingCharacters(in: .whitespaces)
            if v.isEmpty { extra.removeValue(forKey: "MacAddress") } else { extra["MacAddress"] = .string(v) }
        }
    }
}

/// Sprawdzanie nowej wersji na GitHubie — port `UpdateCheck` z Windows, ale dla paczki macOS:
/// liczy się tylko wydanie, które ma plik `…-mac.zip` (wydania sprzed wersji na Maca go nie mają).
public enum UpdateCheck {
    public static let latestURL = URL(string: "https://api.github.com/repos/FilipB97/Waypoint/releases/latest")!

    public struct Release: Equatable, Sendable {
        public var version: [Int]
        public var versionText: String
        public var pageURL: URL?
        public var macZipURL: URL?
        public var notes: String
    }

    public static func parseRelease(_ data: Data) -> Release? {
        guard let o = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let tag = o["tag_name"] as? String, let v = parseVersion(tag) else { return nil }
        var zip: URL?
        for a in (o["assets"] as? [[String: Any]]) ?? [] {
            let name = (a["name"] as? String ?? "").lowercased()
            if name.hasSuffix("-mac.zip") || name == "waypoint-mac.zip", let u = a["browser_download_url"] as? String {
                zip = URL(string: u); break
            }
        }
        return Release(version: v, versionText: v.map(String.init).joined(separator: "."),
                       pageURL: (o["html_url"] as? String).flatMap(URL.init(string:)),
                       macZipURL: zip, notes: o["body"] as? String ?? "")
    }

    /// „v1.2.0" / „1.2" → [1, 2, 0]; nil, gdy to nie wersja.
    public static func parseVersion(_ tag: String) -> [Int]? {
        var t = tag.trimmingCharacters(in: .whitespaces)
        if t.first == "v" || t.first == "V" { t.removeFirst() }
        let parts = t.split(separator: ".", omittingEmptySubsequences: false).map { Int($0) }
        guard !parts.isEmpty, parts.count <= 4, parts.allSatisfy({ $0 != nil && $0! >= 0 }) else { return nil }
        return parts.map { $0! }
    }

    public static func isNewer(_ latest: [Int], than current: [Int]) -> Bool {
        let n = max(latest.count, current.count)
        let a = latest + [Int](repeating: 0, count: n - latest.count)
        let b = current + [Int](repeating: 0, count: n - current.count)
        return a.lexicographicallyPrecedes(b) == false && a != b
    }

    /// Nowsze wydanie z paczką dla Maca albo nil (brak, starsze, bez paczki macOS).
    public static func update(from release: Release?, current: String) -> Release? {
        guard let r = release, r.macZipURL != nil, let cur = parseVersion(current), isNewer(r.version, than: cur) else { return nil }
        return r
    }
}
