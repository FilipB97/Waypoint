import Foundation

/// Protokół SFTP w wersji 3 (draft-ietf-secsh-filexfer-02) — to, co mówi `sftp-server` z OpenSSH.
/// Tylko kodowanie i dekodowanie pakietów: bez wejścia/wyjścia, w pełni testowalne.
public enum SftpPacketType: UInt8 {
    case initPacket = 1, version = 2, open = 3, close = 4, read = 5, write = 6, lstat = 7, fstat = 8
    case setstat = 9, fsetstat = 10, opendir = 11, readdir = 12, remove = 13, mkdir = 14, rmdir = 15
    case realpath = 16, stat = 17, rename = 18, readlink = 19, symlink = 20
    case status = 101, handle = 102, data = 103, name = 104, attrs = 105
    case extended = 200, extendedReply = 201
}

public enum SftpStatusCode: UInt32, Sendable {
    case ok = 0, eof = 1, noSuchFile = 2, permissionDenied = 3, failure = 4, badMessage = 5
    case noConnection = 6, connectionLost = 7, opUnsupported = 8
}

public struct SftpOpenFlags: OptionSet, Sendable {
    public let rawValue: UInt32
    public init(rawValue: UInt32) { self.rawValue = rawValue }
    public static let read = SftpOpenFlags(rawValue: 0x01)
    public static let write = SftpOpenFlags(rawValue: 0x02)
    public static let append = SftpOpenFlags(rawValue: 0x04)
    public static let create = SftpOpenFlags(rawValue: 0x08)
    public static let truncate = SftpOpenFlags(rawValue: 0x10)
    public static let exclusive = SftpOpenFlags(rawValue: 0x20)
}

/// Atrybuty pliku. Pola opcjonalne — serwer wysyła tylko te, które zna (flagi w pakiecie).
public struct SftpAttributes: Equatable, Sendable {
    public var size: UInt64?
    public var uid: UInt32?
    public var gid: UInt32?
    /// st_mode: typ pliku (S_IFMT) + uprawnienia.
    public var permissions: UInt32?
    public var accessTime: UInt32?
    public var modifyTime: UInt32?

    public init(size: UInt64? = nil, uid: UInt32? = nil, gid: UInt32? = nil, permissions: UInt32? = nil,
                accessTime: UInt32? = nil, modifyTime: UInt32? = nil) {
        self.size = size; self.uid = uid; self.gid = gid; self.permissions = permissions
        self.accessTime = accessTime; self.modifyTime = modifyTime
    }

    static let flagSize: UInt32 = 0x1, flagUidGid: UInt32 = 0x2, flagPermissions: UInt32 = 0x4
    static let flagTimes: UInt32 = 0x8, flagExtended: UInt32 = 0x8000_0000

    public var fileType: UInt32? { permissions.map { $0 & 0o170000 } }
    public var isDirectory: Bool { fileType == 0o040000 }
    public var isSymlink: Bool { fileType == 0o120000 }
    public var isRegular: Bool { fileType == 0o100000 }
    /// Same uprawnienia (bez typu pliku), np. 0o644.
    public var mode: UInt32? { permissions.map { $0 & 0o7777 } }
    public var modified: Date? { modifyTime.map { Date(timeIntervalSince1970: TimeInterval($0)) } }
}

/// Wpis katalogu.
public struct SftpEntry: Equatable, Sendable, Identifiable {
    public var name: String
    public var path: String
    public var attributes: SftpAttributes
    /// Dla dowiązania: czy wskazuje na katalog (osobny STAT, bo READDIR podaje atrybuty samego dowiązania).
    public var linkTargetIsDirectory: Bool = false
    public var id: String { path }

    public var isDirectory: Bool { attributes.isDirectory || (attributes.isSymlink && linkTargetIsDirectory) }
    public var size: UInt64 { attributes.size ?? 0 }
}

public enum SftpError: Error, Equatable, Sendable, CustomStringConvertible {
    /// Odpowiedź STATUS z kodem błędu (i komunikatem serwera).
    case status(SftpStatusCode, String)
    /// Połączenie się zakończyło; `detail` = końcówka stderr ssh (np. „Permission denied (publickey)").
    case disconnected(String)
    case protocolViolation(String)
    case cancelled

    public var description: String {
        switch self {
        case .status(let c, let m): return m.isEmpty ? "SFTP \(c)" : m
        case .disconnected(let d): return d.isEmpty ? "Połączenie zamknięte" : d
        case .protocolViolation(let m): return "SFTP: \(m)"
        case .cancelled: return "Anulowano"
        }
    }

    public var code: SftpStatusCode? { if case .status(let c, _) = self { return c }; return nil }
}

/// Bufor do budowy pakietów (big-endian, napisy z prefiksem długości).
struct SftpWriter {
    var data = Data()
    mutating func u8(_ v: UInt8) { data.append(v) }
    mutating func u32(_ v: UInt32) { withUnsafeBytes(of: v.bigEndian) { data.append(contentsOf: $0) } }
    mutating func u64(_ v: UInt64) { withUnsafeBytes(of: v.bigEndian) { data.append(contentsOf: $0) } }
    mutating func bytes(_ d: Data) { u32(UInt32(d.count)); data.append(d) }
    mutating func string(_ s: String) { bytes(Data(s.utf8)) }
    mutating func attrs(_ a: SftpAttributes) {
        var flags: UInt32 = 0
        if a.size != nil { flags |= SftpAttributes.flagSize }
        if a.uid != nil && a.gid != nil { flags |= SftpAttributes.flagUidGid }
        if a.permissions != nil { flags |= SftpAttributes.flagPermissions }
        if a.accessTime != nil && a.modifyTime != nil { flags |= SftpAttributes.flagTimes }
        u32(flags)
        if let s = a.size { u64(s) }
        if let u = a.uid, let g = a.gid { u32(u); u32(g) }
        if let p = a.permissions { u32(p) }
        if let at = a.accessTime, let mt = a.modifyTime { u32(at); u32(mt) }
    }

    /// Ramka: długość + typ + treść.
    static func packet(_ type: SftpPacketType, _ body: (inout SftpWriter) -> Void) -> Data {
        var inner = SftpWriter()
        inner.u8(type.rawValue)
        body(&inner)
        var outer = SftpWriter()
        outer.u32(UInt32(inner.data.count))
        outer.data.append(inner.data)
        return outer.data
    }
}

struct SftpReader {
    let data: Data
    var pos: Int

    init(_ data: Data) { self.data = data; self.pos = data.startIndex }

    var remaining: Int { data.endIndex - pos }

    mutating func u8() throws -> UInt8 {
        guard remaining >= 1 else { throw SftpError.protocolViolation("krótki pakiet") }
        defer { pos += 1 }
        return data[pos]
    }
    mutating func u32() throws -> UInt32 {
        guard remaining >= 4 else { throw SftpError.protocolViolation("krótki pakiet") }
        var v: UInt32 = 0
        for i in 0..<4 { v = (v << 8) | UInt32(data[pos + i]) }
        pos += 4
        return v
    }
    mutating func u64() throws -> UInt64 {
        let hi = try u32(), lo = try u32()
        return (UInt64(hi) << 32) | UInt64(lo)
    }
    mutating func bytes() throws -> Data {
        let n = Int(try u32())
        guard n >= 0, remaining >= n else { throw SftpError.protocolViolation("zła długość pola") }
        defer { pos += n }
        return data.subdata(in: pos..<(pos + n))
    }
    mutating func string() throws -> String {
        let d = try bytes()
        // Nazwy plików bywają nie-UTF-8 (stare serwery, Latin-1) — wtedy bajt po bajcie zamiast błędu.
        return String(data: d, encoding: .utf8) ?? String(decoding: d, as: UTF8.self)
    }
    mutating func attrs() throws -> SftpAttributes {
        let flags = try u32()
        var a = SftpAttributes()
        if flags & SftpAttributes.flagSize != 0 { a.size = try u64() }
        if flags & SftpAttributes.flagUidGid != 0 { a.uid = try u32(); a.gid = try u32() }
        if flags & SftpAttributes.flagPermissions != 0 { a.permissions = try u32() }
        if flags & SftpAttributes.flagTimes != 0 { a.accessTime = try u32(); a.modifyTime = try u32() }
        if flags & SftpAttributes.flagExtended != 0 {
            let n = try u32()
            for _ in 0..<n { _ = try string(); _ = try string() }
        }
        return a
    }
}

/// Ścieżki zdalne (POSIX, niezależnie od systemu klienta).
public enum RemotePath {
    public static func join(_ dir: String, _ name: String) -> String {
        if dir.isEmpty { return name }
        return dir.hasSuffix("/") ? dir + name : dir + "/" + name
    }

    public static func parent(_ p: String) -> String {
        guard p != "/" else { return "/" }
        let trimmed = p.hasSuffix("/") ? String(p.dropLast()) : p
        guard let i = trimmed.lastIndex(of: "/") else { return "." }
        return i == trimmed.startIndex ? "/" : String(trimmed[..<i])
    }

    public static func name(_ p: String) -> String {
        let trimmed = p.count > 1 && p.hasSuffix("/") ? String(p.dropLast()) : p
        return trimmed.split(separator: "/").last.map(String.init) ?? trimmed
    }

    /// Składniki do paska ścieżki: „/var/www" → [("/", "/"), ("var", "/var"), ("www", "/var/www")].
    public static func breadcrumbs(_ p: String) -> [(name: String, path: String)] {
        var out: [(String, String)] = [("/", "/")]
        var cur = ""
        for part in p.split(separator: "/") {
            cur += "/" + part
            out.append((String(part), cur))
        }
        return out
    }

    /// Nazwa podana przez użytkownika (nowy folder, zmiana nazwy) nie może wyprowadzić poza katalog.
    public static func isSafeName(_ n: String) -> Bool {
        !n.isEmpty && n != "." && n != ".." && !n.contains("/") && !n.contains("\0")
    }
}
