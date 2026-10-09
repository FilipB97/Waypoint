import Foundation
#if canImport(Darwin)
import Darwin
#elseif canImport(Glibc)
import Glibc
#endif

/// Kanał bajtów do serwera SFTP.
public protocol SftpTransport: AnyObject {
    func write(_ data: Data) throws
    /// Dokładnie `count` bajtów albo błąd (koniec strumienia = rozłączenie).
    func read(exactly count: Int) throws -> Data
    func close()
}

/// Klient SFTP v3. Metody są synchroniczne i blokujące — wołający uruchamia je na własnej kolejce
/// (jedna operacja naraz). Pobieranie i wysyłanie są potokowe: kilkanaście żądań READ/WRITE w locie,
/// inaczej każdy blok czekałby pełny czas obiegu do serwera i transfer na łączu z opóźnieniem
/// 50 ms nie przekraczałby ~1 MB/s.
public final class SftpClient: @unchecked Sendable {
    public let transport: SftpTransport
    public private(set) var extensions: [String: String] = [:]
    private var nextID: UInt32 = 1
    private let lock = NSLock()

    public static let chunkSize = 64 * 1024
    public static let window = 16

    public init(transport: SftpTransport) throws {
        self.transport = transport
        try transport.write(SftpWriter.packet(.initPacket) { $0.u32(3) })
        let (type, body) = try readPacket()
        guard type == SftpPacketType.version.rawValue else {
            throw SftpError.protocolViolation("oczekiwano VERSION, przyszło \(type)")
        }
        var r = SftpReader(body)
        let version = try r.u32()
        guard version >= 3 else { throw SftpError.protocolViolation("serwer SFTP w wersji \(version)") }
        while r.remaining > 0 {
            let name = try r.string(), value = try r.string()
            extensions[name] = value
        }
    }

    public func close() { transport.close() }

    public var supportsPosixRename: Bool { extensions["posix-rename@openssh.com"] != nil }

    // MARK: Ramki

    private func readPacket() throws -> (UInt8, Data) {
        let header = try transport.read(exactly: 4)
        var hr = SftpReader(header)
        let length = Int(try hr.u32())
        guard length >= 1, length <= 4 * 1024 * 1024 else { throw SftpError.protocolViolation("długość pakietu \(length)") }
        let payload = try transport.read(exactly: length)
        return (payload[payload.startIndex], payload.dropFirst())
    }

    private func newID() -> UInt32 {
        defer { nextID &+= 1 }
        return nextID
    }

    /// Wysyła żądanie i czeka na odpowiedź o tym samym id (pojedyncze operacje — bez potoku).
    private func request(_ type: SftpPacketType, _ body: (inout SftpWriter) -> Void) throws -> (UInt8, SftpReader) {
        lock.lock(); defer { lock.unlock() }
        let id = newID()
        try transport.write(SftpWriter.packet(type) { w in w.u32(id); body(&w) })
        let (rtype, payload) = try readPacket()
        var r = SftpReader(payload)
        let rid = try r.u32()
        guard rid == id else { throw SftpError.protocolViolation("odpowiedź \(rid) na żądanie \(id)") }
        return (rtype, r)
    }

    private static func statusError(_ r: inout SftpReader) throws -> SftpError {
        let code = SftpStatusCode(rawValue: try r.u32()) ?? .failure
        let msg = (try? r.string()) ?? ""
        return .status(code, msg)
    }

    private func expectStatusOK(_ t: UInt8, _ r: inout SftpReader) throws {
        guard t == SftpPacketType.status.rawValue else { throw SftpError.protocolViolation("oczekiwano STATUS") }
        let e = try Self.statusError(&r)
        if e.code != .ok { throw e }
    }

    private func expectHandle(_ t: UInt8, _ r: inout SftpReader) throws -> Data {
        if t == SftpPacketType.status.rawValue { throw try Self.statusError(&r) }
        guard t == SftpPacketType.handle.rawValue else { throw SftpError.protocolViolation("oczekiwano HANDLE") }
        return try r.bytes()
    }

    private func expectAttrs(_ t: UInt8, _ r: inout SftpReader) throws -> SftpAttributes {
        if t == SftpPacketType.status.rawValue { throw try Self.statusError(&r) }
        guard t == SftpPacketType.attrs.rawValue else { throw SftpError.protocolViolation("oczekiwano ATTRS") }
        return try r.attrs()
    }

    // MARK: Operacje

    public func realPath(_ path: String) throws -> String {
        var (t, r) = try request(.realpath) { $0.string(path) }
        if t == SftpPacketType.status.rawValue { throw try Self.statusError(&r) }
        guard t == SftpPacketType.name.rawValue, try r.u32() >= 1 else { throw SftpError.protocolViolation("REALPATH") }
        return try r.string()
    }

    public func stat(_ path: String) throws -> SftpAttributes {
        var (t, r) = try request(.stat) { $0.string(path) }
        return try expectAttrs(t, &r)
    }

    public func lstat(_ path: String) throws -> SftpAttributes {
        var (t, r) = try request(.lstat) { $0.string(path) }
        return try expectAttrs(t, &r)
    }

    public func list(_ dir: String) throws -> [SftpEntry] {
        var (t, r) = try request(.opendir) { $0.string(dir) }
        let handle = try expectHandle(t, &r)
        defer { try? closeHandle(handle) }
        var out: [SftpEntry] = []
        while true {
            var (t2, r2) = try request(.readdir) { $0.bytes(handle) }
            if t2 == SftpPacketType.status.rawValue {
                let e = try Self.statusError(&r2)
                if e.code == .eof { break }
                throw e
            }
            guard t2 == SftpPacketType.name.rawValue else { throw SftpError.protocolViolation("READDIR") }
            let n = try r2.u32()
            for _ in 0..<n {
                let name = try r2.string()
                _ = try r2.string()           // longname (format ls -l) — nie polegamy na nim
                let attrs = try r2.attrs()
                if name == "." || name == ".." { continue }
                out.append(SftpEntry(name: name, path: RemotePath.join(dir, name), attributes: attrs))
            }
        }
        // Dowiązania: czy prowadzą do katalogu (żeby dało się do nich wejść jak do folderu).
        for i in out.indices where out[i].attributes.isSymlink {
            out[i].linkTargetIsDirectory = (try? stat(out[i].path).isDirectory) ?? false
        }
        return out
    }

    public func mkdir(_ path: String) throws {
        var (t, r) = try request(.mkdir) { w in w.string(path); w.attrs(SftpAttributes()) }
        try expectStatusOK(t, &r)
    }

    public func rmdir(_ path: String) throws {
        var (t, r) = try request(.rmdir) { $0.string(path) }
        try expectStatusOK(t, &r)
    }

    public func remove(_ path: String) throws {
        var (t, r) = try request(.remove) { $0.string(path) }
        try expectStatusOK(t, &r)
    }

    /// Zmiana nazwy. `overwrite` wymaga rozszerzenia posix-rename (SFTP v3 odmawia nadpisania istniejącego).
    public func rename(_ from: String, to: String, overwrite: Bool = false) throws {
        if overwrite && supportsPosixRename {
            var (t, r) = try request(.extended) { w in w.string("posix-rename@openssh.com"); w.string(from); w.string(to) }
            try expectStatusOK(t, &r)
        } else {
            var (t, r) = try request(.rename) { w in w.string(from); w.string(to) }
            try expectStatusOK(t, &r)
        }
    }

    public func setAttributes(_ path: String, _ attrs: SftpAttributes) throws {
        var (t, r) = try request(.setstat) { w in w.string(path); w.attrs(attrs) }
        try expectStatusOK(t, &r)
    }

    public func open(_ path: String, _ flags: SftpOpenFlags, attrs: SftpAttributes = SftpAttributes()) throws -> Data {
        var (t, r) = try request(.open) { w in w.string(path); w.u32(flags.rawValue); w.attrs(attrs) }
        return try expectHandle(t, &r)
    }

    public func closeHandle(_ handle: Data) throws {
        var (t, r) = try request(.close) { $0.bytes(handle) }
        try expectStatusOK(t, &r)
    }

    // MARK: Transfer (potokowy)

    /// Pobiera plik do `sink(offset, dane)`. `progress(bajty)` zwraca false, żeby przerwać.
    /// Zwraca liczbę pobranych bajtów.
    @discardableResult
    public func download(_ path: String, sink: (UInt64, Data) throws -> Void,
                         progress: (UInt64) -> Bool = { _ in true }) throws -> UInt64 {
        let handle = try open(path, .read)
        defer { try? closeHandle(handle) }
        lock.lock(); defer { lock.unlock() }

        var pending: [UInt32: (offset: UInt64, length: UInt32)] = [:]
        var nextOffset: UInt64 = 0
        var reachedEOF = false
        var total: UInt64 = 0
        var retry: [(UInt64, UInt32)] = []   // krótkie odczyty: brakująca reszta bloku

        func send(_ offset: UInt64, _ length: UInt32) throws {
            let id = newID()
            pending[id] = (offset, length)
            try transport.write(SftpWriter.packet(.read) { w in w.u32(id); w.bytes(handle); w.u64(offset); w.u32(length) })
        }

        while true {
            while pending.count < Self.window {
                if let (o, l) = retry.popLast() { try send(o, l); continue }
                if reachedEOF { break }
                try send(nextOffset, UInt32(Self.chunkSize))
                nextOffset += UInt64(Self.chunkSize)
            }
            if pending.isEmpty { break }

            let (t, payload) = try readPacket()
            var r = SftpReader(payload)
            let id = try r.u32()
            guard let req = pending.removeValue(forKey: id) else { throw SftpError.protocolViolation("nieznane id \(id)") }
            if t == SftpPacketType.status.rawValue {
                let e = try Self.statusError(&r)
                guard e.code == .eof else { throw e }
                reachedEOF = true          // za końcem pliku — dalszych bloków nie wysyłamy
                continue
            }
            guard t == SftpPacketType.data.rawValue else { throw SftpError.protocolViolation("oczekiwano DATA") }
            let chunk = try r.bytes()
            if chunk.isEmpty { reachedEOF = true; continue }
            try sink(req.offset, chunk)
            total += UInt64(chunk.count)
            if UInt32(chunk.count) < req.length {
                retry.append((req.offset + UInt64(chunk.count), req.length - UInt32(chunk.count)))
            }
            if !progress(total) {
                // Odbierz resztę odpowiedzi, żeby kanał został spójny dla kolejnych operacji.
                while !pending.isEmpty { let (_, p) = try readPacket(); var rr = SftpReader(p); pending.removeValue(forKey: try rr.u32()) }
                throw SftpError.cancelled
            }
        }
        return total
    }

    /// Wysyła dane z `source(offset, maks) -> blok` (pusty blok = koniec) do już otwartego uchwytu.
    public func upload(handle: Data, source: (UInt64, Int) throws -> Data,
                       progress: (UInt64) -> Bool = { _ in true }) throws -> UInt64 {
        lock.lock(); defer { lock.unlock() }
        var pending = Set<UInt32>()
        var offset: UInt64 = 0
        var finished = false
        var failure: SftpError?

        while true {
            while !finished && pending.count < Self.window && failure == nil {
                let block = try source(offset, Self.chunkSize)
                if block.isEmpty { finished = true; break }
                let id = newID()
                pending.insert(id)
                let at = offset
                try transport.write(SftpWriter.packet(.write) { w in w.u32(id); w.bytes(handle); w.u64(at); w.bytes(block) })
                offset += UInt64(block.count)
            }
            if pending.isEmpty { break }
            let (t, payload) = try readPacket()
            var r = SftpReader(payload)
            let id = try r.u32()
            guard pending.remove(id) != nil else { throw SftpError.protocolViolation("nieznane id \(id)") }
            guard t == SftpPacketType.status.rawValue else { throw SftpError.protocolViolation("oczekiwano STATUS") }
            let e = try Self.statusError(&r)
            if e.code != .ok && failure == nil { failure = e }
            if failure == nil && !progress(offset) { failure = .cancelled }
        }
        if let failure { throw failure }
        return offset
    }

    // MARK: Wygodne operacje na plikach lokalnych

    public func download(_ path: String, to local: URL, progress: (UInt64) -> Bool = { _ in true }) throws {
        _ = FileManager.default.createFile(atPath: local.path, contents: nil)
        let fh = try FileHandle(forWritingTo: local)
        defer { try? fh.close() }
        do {
            try download(path, sink: { off, data in
                try fh.seek(toOffset: off)
                try fh.write(contentsOf: data)
            }, progress: progress)
        } catch {
            try? fh.close()
            try? FileManager.default.removeItem(at: local)   // nie zostawiamy połowy pliku
            throw error
        }
    }

    public func readFile(_ path: String, limit: Int = 64 * 1024 * 1024) throws -> Data {
        var out = Data()
        try download(path, sink: { off, data in
            let end = Int(off) + data.count
            guard end <= limit else { throw SftpError.status(.failure, "plik większy niż \(limit) B") }
            if out.count < end { out.append(Data(count: end - out.count)) }
            out.replaceSubrange(Int(off)..<end, with: data)
        })
        return out
    }

    /// Wysyła plik lokalny. Bez nadpisywania istniejącego, chyba że `overwrite`.
    public func upload(from local: URL, to path: String, overwrite: Bool,
                       progress: (UInt64) -> Bool = { _ in true }) throws {
        let fh = try FileHandle(forReadingFrom: local)
        defer { try? fh.close() }
        let flags: SftpOpenFlags = overwrite ? [.write, .create, .truncate] : [.write, .create, .exclusive]
        let handle = try open(path, flags)
        do {
            _ = try upload(handle: handle, source: { _, max in try fh.read(upToCount: max) ?? Data() }, progress: progress)
            try closeHandle(handle)
        } catch {
            try? closeHandle(handle)
            if case SftpError.cancelled = error { try? remove(path) }   // przerwany transfer — bez połowy pliku
            throw error
        }
    }

    public func writeFile(_ path: String, data: Data, flags: SftpOpenFlags, attrs: SftpAttributes = SftpAttributes()) throws {
        let handle = try open(path, flags, attrs: attrs)
        do {
            _ = try upload(handle: handle, source: { off, max in
                let start = Int(off)
                guard start < data.count else { return Data() }
                return data.subdata(in: start..<min(data.count, start + max))
            })
            try closeHandle(handle)
        } catch {
            try? closeHandle(handle)
            throw error
        }
    }

    // MARK: Drzewa katalogów

    /// Liczba plików i bajtów (do paska postępu przed transferem katalogu).
    public func treeSize(_ path: String, depth: Int = 0) throws -> (files: Int, bytes: UInt64) {
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
    public func downloadTree(_ remote: String, into localDir: URL, progress: (UInt64) -> Bool = { _ in true }) throws {
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
    public func uploadTree(_ local: URL, into remoteDir: String, overwrite: Bool,
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
    public static func localTreeSize(_ url: URL) -> UInt64 {
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
    public func removeTree(_ path: String, depth: Int = 0) throws {
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

/// Transport przez proces `ssh -s … sftp` (stdin/stdout). Uwierzytelnianie robi ssh — ten sam
/// mechanizm co terminal (config, agent, known_hosts, askpass).
public final class SshProcessTransport: SftpTransport, @unchecked Sendable {
    private let process = Process()
    private let input = Pipe(), output = Pipe(), errors = Pipe()
    private let lock = NSLock()
    private var stderrTail = Data()
    private var closed = false

    public init(executable: String, arguments: [String], environment: [String: String]) throws {
        // Zapis do potoku zakończonego procesu nie może zabić aplikacji sygnałem SIGPIPE.
        signal(SIGPIPE, SIG_IGN)
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = arguments
        process.environment = environment
        process.standardInput = input
        process.standardOutput = output
        process.standardError = errors
        errors.fileHandleForReading.readabilityHandler = { [weak self] h in
            let d = h.availableData
            guard let self, !d.isEmpty else { return }
            self.lock.lock()
            self.stderrTail.append(d)
            if self.stderrTail.count > 4096 { self.stderrTail = self.stderrTail.suffix(4096) }
            self.lock.unlock()
        }
        try process.run()
    }

    /// Ostatnie linie stderr ssh — powód rozłączenia dla użytkownika.
    public var stderrText: String {
        lock.lock(); defer { lock.unlock() }
        return String(decoding: stderrTail, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private func disconnected() -> SftpError {
        // Chwila na dopisanie stderr przez ssh (np. „Permission denied").
        if process.isRunning { usleep(100_000) } else { usleep(50_000) }
        let lines = stderrText.split(whereSeparator: \.isNewline).suffix(3).joined(separator: "\n")
        return .disconnected(lines)
    }

    public func write(_ data: Data) throws {
        let fd = input.fileHandleForWriting.fileDescriptor
        let ok = data.withUnsafeBytes { raw -> Bool in
            var off = 0
            while off < raw.count {
                let n = sysWrite(fd, raw.baseAddress! + off, raw.count - off)
                if n < 0 && errno == EINTR { continue }
                if n <= 0 { return false }
                off += n
            }
            return true
        }
        if !ok { throw disconnected() }
    }

    public func read(exactly count: Int) throws -> Data {
        var out = Data(count: count)
        var got = 0
        let fd = output.fileHandleForReading.fileDescriptor
        while got < count {
            let n = out.withUnsafeMutableBytes { sysRead(fd, $0.baseAddress! + got, count - got) }
            if n < 0 && errno == EINTR { continue }
            if n <= 0 { throw disconnected() }
            got += n
        }
        return out
    }

    public func close() {
        lock.lock()
        if closed { lock.unlock(); return }
        closed = true
        lock.unlock()
        try? input.fileHandleForWriting.close()
        if process.isRunning { process.terminate() }
        errors.fileHandleForReading.readabilityHandler = nil
    }

    deinit { close() }
}

#if canImport(Darwin)
private func sysWrite(_ fd: Int32, _ p: UnsafeRawPointer, _ n: Int) -> Int { Darwin.write(fd, p, n) }
private func sysRead(_ fd: Int32, _ p: UnsafeMutableRawPointer, _ n: Int) -> Int { Darwin.read(fd, p, n) }
#else
private func sysWrite(_ fd: Int32, _ p: UnsafeRawPointer, _ n: Int) -> Int { Glibc.write(fd, p, n) }
private func sysRead(_ fd: Int32, _ p: UnsafeMutableRawPointer, _ n: Int) -> Int { Glibc.read(fd, p, n) }
#endif
