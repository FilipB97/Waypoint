import Foundation

/// Metadane pliku otwartego do edycji (port `RemoteFileInfo.cs`).
public struct RemoteFileInfo: Equatable, Sendable {
    /// Ścieżka PO rozwiązaniu dowiązań — plik tymczasowy powstaje w katalogu PRAWDZIWEGO pliku, inaczej
    /// atomowa podmiana zastąpiłaby samo dowiązanie zwykłym plikiem (np. sites-enabled/default w nginx).
    public var path: String
    public var length: UInt64
    public var modified: UInt32
    /// st_mode & 07777; nil = serwer nie podał.
    public var mode: UInt32?
    public var uid: UInt32?
    public var gid: UInt32?

    public init(path: String, length: UInt64, modified: UInt32, mode: UInt32? = nil, uid: UInt32? = nil, gid: UInt32? = nil) {
        self.path = path; self.length = length; self.modified = modified; self.mode = mode; self.uid = uid; self.gid = gid
    }

    /// Czy plik zmienił się na serwerze od otwarcia (rozmiar albo czas — SFTP v3 podaje pełne sekundy).
    public static func changedSince(_ opened: RemoteFileInfo, _ now: RemoteFileInfo) -> Bool {
        opened.length != now.length || opened.modified != now.modified
    }

    /// Czy zalogowany (uid z właściciela katalogu domowego) może pisać — false tylko gdy odmowa jest
    /// PEWNA (wtedy edytor otwiera się tylko do odczytu), nil gdy rozstrzyga grupa/ACL.
    public func likelyWritable(by myUid: UInt32?) -> Bool? {
        guard let mode, let uid, let myUid else { return nil }
        if myUid == 0 { return true }
        if uid == myUid { return mode & 0o200 != 0 }
        if mode & 0o002 != 0 { return true }
        if mode & 0o020 != 0 { return nil }
        return false
    }
}

public enum SafeWriteMode: Equatable, Sendable {
    /// Plik tymczasowy obok + atomowa podmiana — zerwane połączenie nie zostawi uciętego pliku.
    case atomicReplace
    /// Nadpisanie w miejscu (zachowuje właściciela i i-węzeł, ale nie jest atomowe); powód — klucz tekstu.
    case inPlace(reasonKey: String)
}

/// Zapis się nie udał. Dla użytkownika liczy się, czy plik na serwerze jest cały.
public struct SafeWriteError: Error, Sendable {
    public let originalMayBeDamaged: Bool
    public let underlying: Error
    public var isPermissionDenied: Bool { (underlying as? SftpError)?.code == .permissionDenied }
}

/// Bezpieczny zapis pliku przez SFTP — port `SftpSafeWriter.cs` (zachowanie sprawdzone na OpenSSH,
/// przypadek po przypadku: dowiązania, uprawnienia, właściciel, grupa, katalog bez prawa zapisu).
public enum SafeWrite {
    static let staleAfter: TimeInterval = 10 * 60

    public static func stat(_ c: RemoteFS, _ path: String) throws -> RemoteFileInfo {
        let real = try c.realPath(path)
        let a = try c.stat(real)
        return RemoteFileInfo(path: real, length: a.size ?? 0, modified: a.modifyTime ?? 0, mode: a.mode, uid: a.uid, gid: a.gid)
    }

    public static func write(_ fs: RemoteFS, content: Data, original: RemoteFileInfo) throws -> SafeWriteMode {
        guard let c = fs as? SftpClient else {
            // FTP: zapis w miejscu (STOR). Podmiana przez RNFR/RNTO zależy od serwera, a plik tymczasowy
            // dostałby uprawnienia z umask serwera zamiast oryginalnych, których FTP nie odczyta.
            guard let ftp = fs as? FtpClient else { throw SftpError.status(.opUnsupported, "") }
            do { try ftp.writeFile(original.path, data: content) }
            catch {
                // Odmowa (brak uprawnień, logowanie) pada przed wysłaniem treści — plik cały.
                let safe: Bool
                switch error as? SftpError {
                case .status(.permissionDenied, _)?, .authenticationFailed?, .certificateUntrusted?, .disconnected?: safe = true
                default: safe = false
                }
                throw SafeWriteError(originalMayBeDamaged: !safe, underlying: error)
            }
            return .inPlace(reasonKey: "edit.fb.ftp")
        }
        return try writeSftp(c, content: content, original: original)
    }

    private static func writeSftp(_ c: SftpClient, content: Data, original: RemoteFileInfo) throws -> SafeWriteMode {
        let real = original.path
        let dir = RemotePath.parent(real)
        let prefix = "." + RemotePath.name(real) + ".waypoint-"
        let tmp = RemotePath.join(dir, prefix + String(UUID().uuidString.replacingOccurrences(of: "-", with: "").prefix(8)).lowercased() + ".tmp")
        removeStaleTemps(c, dir: dir, prefix: prefix)

        // Do chwili podmiany oryginał jest nietknięty — każdy błąd wcześniej jest „bezpieczny".
        do { return try writeCore(c, content, original, real, tmp) }
        catch let e as SafeWriteError { throw e }
        catch { throw SafeWriteError(originalMayBeDamaged: false, underlying: error) }
    }

    private static func removeStaleTemps(_ c: SftpClient, dir: String, prefix: String) {
        guard let list = try? c.list(dir) else { return }
        let now = UInt32(Date().timeIntervalSince1970)
        for e in list where e.attributes.isRegular && e.name.hasPrefix(prefix) && e.name.hasSuffix(".tmp") {
            // Młodszy niż 10 min może należeć do zapisu, który właśnie trwa w innym oknie.
            guard let m = e.attributes.modifyTime, now > m, TimeInterval(now - m) > staleAfter else { continue }
            try? c.remove(e.path)
        }
    }

    private static func writeCore(_ c: SftpClient, _ content: Data, _ original: RemoteFileInfo,
                                  _ real: String, _ tmp: String) throws -> SafeWriteMode {
        // 1) Pusty plik tymczasowy, EXCL — nigdy nie nadpisze czegoś, co akurat tak się nazywa. Brak prawa
        //    zapisu do KATALOGU (plik może być zapisywalny, np. 0666 w katalogu roota) → zapis w miejscu.
        do { try c.closeHandle(try c.open(tmp, [.write, .create, .exclusive])) }
        catch let e as SftpError where e.code == .permissionDenied { return try inPlace(c, content, real, "edit.fb.dir") }

        var renamed = false
        defer { if !renamed { try? c.remove(tmp) } }
        let t = try c.stat(tmp)

        // 2) Właściciel. Podmiana daje plik o NASZYM właścicielu — plik roota w naszym katalogu zostałby
        //    po cichu „przejęty". W miejscu: albo się uda z zachowaniem właściciela, albo uczciwie odmówi.
        if let ou = original.uid, t.uid != ou { return try inPlace(c, content, real, "edit.fb.owner") }

        // 3) Grupa. Plik wpt:web 0664 po podmianie miałby grupę wpt i serwer WWW straciłby zapis.
        if let og = original.gid, t.gid != og {
            if let tu = t.uid { try? c.setAttributes(tmp, SftpAttributes(uid: tu, gid: og)) }
            if (try? c.stat(tmp).gid) != og { return try inPlace(c, content, real, "edit.fb.group") }
        }

        // 4) Uprawnienia ZANIM trafi treść — klucz 0600 nie bywa nawet przez chwilę czytelny dla innych.
        if let m = original.mode { try c.setAttributes(tmp, SftpAttributes(permissions: m)) }

        try c.writeFile(tmp, data: content, flags: [.write, .truncate])

        // 5) Kontrola rozmiaru przed podmianą.
        guard try c.stat(tmp).size == UInt64(content.count) else {
            throw SftpError.status(.failure, "plik tymczasowy ma inny rozmiar niż treść")
        }

        // 6) Atomowa podmiana; bez posix-rename (SFTP v3 nie nadpisuje istniejącego) — w miejscu.
        guard c.supportsPosixRename else { return try inPlace(c, content, real, "edit.fb.rename") }
        do { try c.rename(tmp, to: real, overwrite: true); renamed = true }
        catch is SftpError { return try inPlace(c, content, real, "edit.fb.rename") }
        return .atomicReplace
    }

    /// Nadpisanie w miejscu. Odmowa OTWARCIA (brak prawa zapisu) pada, zanim plik zostanie obcięty —
    /// oryginał cały. Każdy błąd po otwarciu (z obcięciem) mógł go już uszkodzić.
    private static func inPlace(_ c: SftpClient, _ content: Data, _ real: String, _ reason: String) throws -> SafeWriteMode {
        let handle: Data
        do { handle = try c.open(real, [.write, .truncate]) }
        catch { throw SafeWriteError(originalMayBeDamaged: false, underlying: error) }
        do {
            _ = try c.upload(handle: handle, source: { off, max in
                let start = Int(off)
                guard start < content.count else { return Data() }
                return content.subdata(in: start..<min(content.count, start + max))
            })
            try c.closeHandle(handle)
        } catch {
            try? c.closeHandle(handle)
            throw SafeWriteError(originalMayBeDamaged: true, underlying: error)
        }
        return .inPlace(reasonKey: reason)
    }
}
