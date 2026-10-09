import Foundation

/// Zdalny system plików widziany przez panel plików i edytor — SFTP (`SftpClient`) albo FTP/FTPS
/// (`FtpClient`). Operacje na drzewach katalogów są wspólne (rozszerzenie niżej), więc oba protokoły
/// mają te same zabezpieczenia: bezpieczne nazwy, bez śledzenia dowiązań do katalogów, limit głębokości.
public protocol RemoteFS: AnyObject, Sendable {
    func realPath(_ path: String) throws -> String
    func stat(_ path: String) throws -> SftpAttributes
    func lstat(_ path: String) throws -> SftpAttributes
    func list(_ dir: String) throws -> [SftpEntry]
    func mkdir(_ path: String) throws
    func rmdir(_ path: String) throws
    func remove(_ path: String) throws
    func rename(_ from: String, to: String, overwrite: Bool) throws
    func download(_ path: String, to local: URL, progress: (UInt64) -> Bool) throws
    func upload(from local: URL, to path: String, overwrite: Bool, progress: (UInt64) -> Bool) throws
    func readFile(_ path: String, limit: Int) throws -> Data
    func close()
}

extension SftpClient: RemoteFS {}

public extension RemoteFS {
    func download(_ path: String, to local: URL) throws { try download(path, to: local, progress: { _ in true }) }
    func readFile(_ path: String) throws -> Data { try readFile(path, limit: 64 * 1024 * 1024) }
    func rename(_ from: String, to: String) throws { try rename(from, to: to, overwrite: false) }
    func upload(from local: URL, to path: String, overwrite: Bool) throws {
        try upload(from: local, to: path, overwrite: overwrite, progress: { _ in true })
    }


    /// Liczba plików i bajtów (do paska postępu przed transferem katalogu).
    func treeSize(_ path: String, depth: Int = 0) throws -> (files: Int, bytes: UInt64) {
        guard depth < 64 else { return (0, 0) }
        let a = try lstat(path)
        guard a.isDirectory else { return (1, a.size ?? 0) }
        var files = 0, bytes: UInt64 = 0
        for e in try list(path) {
            if e.attributes.isDirectory { let t = try treeSize(e.path, depth: depth + 1); files += t.files; bytes += t.bytes }
            else if !(e.attributes.isSymlink && e.linkTargetIsDirectory) { files += 1; bytes += e.size }
        }
        return (files, bytes)
    }

    /// Pobiera plik albo katalog (rekurencyjnie) do `localDir/<nazwa>`. Dowiązań do katalogów nie
    /// śledzi (pętla dowiązań = nieskończona rekurencja); nazwy z serwera muszą być bezpieczne —
    /// „../x" z wrogiego serwera nie może zapisać pliku poza wybranym katalogiem.
    func downloadTree(_ remote: String, into localDir: URL, progress: (UInt64) -> Bool = { _ in true }) throws {
        var done: UInt64 = 0
        try downloadTree(remote, into: localDir, done: &done, progress: progress, depth: 0)
    }

    private func downloadTree(_ remote: String, into localDir: URL, done: inout UInt64,
                              progress: (UInt64) -> Bool, depth: Int) throws {
        guard depth < 64 else { throw SftpError.status(.failure, "zbyt głęboka struktura katalogów") }
        let name = RemotePath.name(remote)
        guard RemotePath.isSafeName(name) else { throw SftpError.status(.failure, "niebezpieczna nazwa: \(name)") }
        let target = localDir.appendingPathComponent(name)
        let a = try stat(remote)
        if a.isDirectory {
            try FileManager.default.createDirectory(at: target, withIntermediateDirectories: true)
            for e in try list(remote) {
                if e.attributes.isSymlink && e.linkTargetIsDirectory { continue }
                try downloadTree(e.path, into: target, done: &done, progress: progress, depth: depth + 1)
            }
        } else {
            let base = done
            try download(remote, to: target) { progress(base + $0) }
            done = base + (a.size ?? 0)
        }
    }

    /// Wysyła plik albo katalog (rekurencyjnie) do `remoteDir/<nazwa>`.
    func uploadTree(_ local: URL, into remoteDir: String, overwrite: Bool,
                           progress: (UInt64) -> Bool = { _ in true }) throws {
        var done: UInt64 = 0
        try uploadTree(local, into: remoteDir, overwrite: overwrite, done: &done, progress: progress, depth: 0)
    }

    private func uploadTree(_ local: URL, into remoteDir: String, overwrite: Bool, done: inout UInt64,
                            progress: (UInt64) -> Bool, depth: Int) throws {
        guard depth < 64 else { throw SftpError.status(.failure, "zbyt głęboka struktura katalogów") }
        let target = RemotePath.join(remoteDir, local.lastPathComponent)
        let values = try local.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey, .fileSizeKey])
        if values.isSymbolicLink == true && depth > 0 { return }   // dowiązania wewnątrz drzewa pomijamy
        if values.isDirectory == true {
            if (try? stat(target))?.isDirectory != true { try mkdir(target) }
            let children = try FileManager.default.contentsOfDirectory(at: local, includingPropertiesForKeys: nil)
            for child in children.sorted(by: { $0.lastPathComponent < $1.lastPathComponent }) {
                try uploadTree(child, into: target, overwrite: overwrite, done: &done, progress: progress, depth: depth + 1)
            }
        } else {
            let base = done
            try upload(from: local, to: target, overwrite: overwrite) { progress(base + $0) }
            done = base + UInt64(values.fileSize ?? 0)
        }
    }

    /// Rozmiar lokalnego pliku albo katalogu (do paska postępu wysyłania).
    static func localTreeSize(_ url: URL) -> UInt64 {
        let fm = FileManager.default
        var isDir: ObjCBool = false
        guard fm.fileExists(atPath: url.path, isDirectory: &isDir) else { return 0 }
        if !isDir.boolValue { return UInt64((try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0) }
        var total: UInt64 = 0
        if let e = fm.enumerator(at: url, includingPropertiesForKeys: [.fileSizeKey, .isRegularFileKey]) {
            for case let f as URL in e {
                let v = try? f.resourceValues(forKeys: [.fileSizeKey, .isRegularFileKey])
                if v?.isRegularFile == true { total += UInt64(v?.fileSize ?? 0) }
            }
        }
        return total
    }

    /// Usuwa plik albo katalog z zawartością (dowiązań nie śledzi — usuwa samo dowiązanie).
    func removeTree(_ path: String, depth: Int = 0) throws {
        guard depth < 64 else { throw SftpError.status(.failure, "zbyt głęboka struktura katalogów") }
        let a = try lstat(path)
        if a.isDirectory {
            for e in try list(path) {
                if e.attributes.isDirectory { try removeTree(e.path, depth: depth + 1) } else { try remove(e.path) }
            }
            try rmdir(path)
        } else {
            try remove(path)
        }
    }
}
