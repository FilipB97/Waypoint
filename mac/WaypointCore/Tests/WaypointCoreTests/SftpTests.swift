import Foundation
import Testing
@testable import WaypointCore

@Suite struct SftpProtocolTests {
    @Test func atrybutyTamIZPowrotem() throws {
        let a = SftpAttributes(size: 1 << 33, uid: 1000, gid: 33, permissions: 0o100644, accessTime: 1, modifyTime: 1_700_000_000)
        var w = SftpWriter()
        w.attrs(a)
        var r = SftpReader(w.data)
        #expect(try r.attrs() == a)
        #expect(a.isRegular && !a.isDirectory)
        #expect(a.mode == 0o644)
    }

    @Test func ramkaPakietu() throws {
        let p = SftpWriter.packet(.opendir) { w in w.u32(7); w.string("/tmp") }
        var r = SftpReader(p)
        #expect(try r.u32() == UInt32(p.count - 4))
        #expect(try r.u8() == SftpPacketType.opendir.rawValue)
        #expect(try r.u32() == 7)
        #expect(try r.string() == "/tmp")
    }

    @Test func krotkiPakietToBladNieCrash() {
        var r = SftpReader(Data([0, 0, 0, 9, 1]))
        #expect(throws: SftpError.self) { _ = try r.bytes() }
    }

    @Test func sciezki() {
        #expect(RemotePath.join("/var/www", "a.txt") == "/var/www/a.txt")
        #expect(RemotePath.join("/", "etc") == "/etc")
        #expect(RemotePath.parent("/var/www") == "/var")
        #expect(RemotePath.parent("/var") == "/")
        #expect(RemotePath.parent("/") == "/")
        #expect(RemotePath.name("/var/www/") == "www")
        #expect(RemotePath.breadcrumbs("/var/www").map(\.path) == ["/", "/var", "/var/www"])
        #expect(!RemotePath.isSafeName("../x") && !RemotePath.isSafeName("..") && RemotePath.isSafeName("nowy folder"))
    }

    @Test func uprawnieniaJakLs() {
        #expect(UnixPermissions.symbolic(0o755) == "-rwxr-xr-x")
        #expect(UnixPermissions.symbolic(0o644) == "-rw-r--r--")
        #expect(UnixPermissions.symbolic(0o2775, directory: true) == "drwxrwsr-x")
        #expect(UnixPermissions.symbolic(0o1777, directory: true) == "drwxrwxrwt")
        #expect(UnixPermissions.symbolic(0o4644) == "-rwSr--r--")
    }

    @Test func polecenieSftp() {
        let s = Server(host: "h", port: 2222, username: "u", proto: .sftp, tunnels: ["8080:localhost:80"])
        let l = SshCommand.buildSftp(s, homeDirectory: "/Users/f", fileExists: { _ in true })
        #expect(!l.arguments.contains("-L"))   // tunele otwiera tylko terminal
        #expect(Array(l.arguments.suffix(4)) == ["-s", "--", "h", "sftp"])
        #expect(l.arguments.contains("-T"))
    }
}

/// Test na prawdziwym serwerze OpenSSH. Uruchamia się, gdy jest zmienna
/// WAYPOINT_SFTP_TEST=host:port:user:ścieżka_klucza[:known_hosts] (lokalnie albo w CI z sshd).
@Suite(.enabled(if: ProcessInfo.processInfo.environment["WAYPOINT_SFTP_TEST"] != nil))
struct SftpIntegrationTests {
    struct Target { let host: String; let port: Int; let user: String; let key: String; let knownHosts: String }

    static var target: Target {
        let p = ProcessInfo.processInfo.environment["WAYPOINT_SFTP_TEST"]!.split(separator: ":").map(String.init)
        return Target(host: p[0], port: Int(p[1])!, user: p[2], key: p[3], knownHosts: p.count > 4 ? p[4] : "/dev/null")
    }

    static func connect(user: String? = nil) throws -> SftpClient {
        let t = target
        let s = Server(host: t.host, port: t.port, username: user ?? t.user, proto: .sftp, privateKeyPath: t.key)
        let l = SshCommand.buildSftp(s, homeDirectory: NSHomeDirectory(), fileExists: { FileManager.default.fileExists(atPath: $0) })
        let extra = ["-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=accept-new", "-o", "UserKnownHostsFile=\(t.knownHosts)"]
        let env = Dictionary(uniqueKeysWithValues: SshCommand.environment(base: ProcessInfo.processInfo.environment)
            .map { kv -> (String, String) in let i = kv.firstIndex(of: "=")!; return (String(kv[..<i]), String(kv[kv.index(after: i)...])) })
        return try SftpClient(transport: SshProcessTransport(executable: "/usr/bin/ssh", arguments: extra + l.arguments, environment: env))
    }

    @Test func pelnyCyklPlikow() throws {
        let c = try Self.connect()
        defer { c.close() }
        #expect(c.supportsPosixRename)
        let home = try c.realPath(".")
        let dir = RemotePath.join(home, "wp-test-\(UUID().uuidString.prefix(8))")
        try c.mkdir(dir)
        defer { try? c.removeTree(dir) }

        // 3 MB losowych danych — kilkadziesiąt bloków, więc potok READ/WRITE naprawdę pracuje.
        var bytes = [UInt8](repeating: 0, count: 3 * 1024 * 1024 + 123)
        for i in bytes.indices { bytes[i] = UInt8(truncatingIfNeeded: i &* 2654435761 >> 13) }
        let local = FileManager.default.temporaryDirectory.appendingPathComponent("wp-up-\(UUID().uuidString)")
        try Data(bytes).write(to: local)
        defer { try? FileManager.default.removeItem(at: local) }

        let remote = RemotePath.join(dir, "dane.bin")
        var lastProgress: UInt64 = 0
        try c.upload(from: local, to: remote, overwrite: false) { lastProgress = $0; return true }
        #expect(lastProgress == UInt64(bytes.count))
        #expect(try c.stat(remote).size == UInt64(bytes.count))

        // Bez overwrite drugi raz — odmowa (plik istnieje).
        #expect(throws: SftpError.self) { try c.upload(from: local, to: remote, overwrite: false) }

        let back = FileManager.default.temporaryDirectory.appendingPathComponent("wp-down-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: back) }
        try c.download(remote, to: back)
        #expect(try Data(contentsOf: back) == Data(bytes))
        #expect(try c.readFile(remote) == Data(bytes))

        // Lista, zmiana nazwy, podmiana posix-rename, katalog z zawartością.
        try c.mkdir(RemotePath.join(dir, "pod"))
        try c.writeFile(RemotePath.join(dir, "pod/a.txt"), data: Data("A".utf8), flags: [.write, .create, .truncate])
        try c.writeFile(RemotePath.join(dir, "b.txt"), data: Data("B".utf8), flags: [.write, .create, .truncate])
        let names = try c.list(dir).map(\.name).sorted()
        #expect(names == ["b.txt", "dane.bin", "pod"])
        #expect(try c.list(dir).first { $0.name == "pod" }?.isDirectory == true)

        try c.rename(RemotePath.join(dir, "b.txt"), to: RemotePath.join(dir, "c.txt"))
        try c.writeFile(RemotePath.join(dir, "d.txt"), data: Data("D".utf8), flags: [.write, .create, .truncate])
        #expect(throws: SftpError.self) { try c.rename(RemotePath.join(dir, "c.txt"), to: RemotePath.join(dir, "d.txt")) }
        try c.rename(RemotePath.join(dir, "c.txt"), to: RemotePath.join(dir, "d.txt"), overwrite: true)
        #expect(try c.readFile(RemotePath.join(dir, "d.txt")) == Data("B".utf8))

        try c.removeTree(RemotePath.join(dir, "pod"))
        #expect(try c.list(dir).map(\.name).sorted() == ["d.txt", "dane.bin"])
    }

    @Test func drzewaKatalogowWObieStrony() throws {
        let c = try Self.connect()
        defer { c.close() }
        let fm = FileManager.default
        let src = fm.temporaryDirectory.appendingPathComponent("wp-tree-\(UUID().uuidString.prefix(6))")
        try fm.createDirectory(at: src.appendingPathComponent("a/b"), withIntermediateDirectories: true)
        try Data("jeden".utf8).write(to: src.appendingPathComponent("1.txt"))
        try Data(count: 200_000).write(to: src.appendingPathComponent("a/b/duzy.bin"))
        try Data("zażółć".utf8).write(to: src.appendingPathComponent("a/ł.txt"))
        defer { try? fm.removeItem(at: src) }

        let home = try c.realPath(".")
        var last: UInt64 = 0
        try c.uploadTree(src, into: home, overwrite: false) { last = $0; return true }
        let remote = RemotePath.join(home, src.lastPathComponent)
        defer { try? c.removeTree(remote) }
        #expect(last == SftpClient.localTreeSize(src))
        let size = try c.treeSize(remote)
        #expect(size.files == 3)
        #expect(size.bytes == 200_000 + 5 + UInt64("zażółć".utf8.count))

        let dst = fm.temporaryDirectory.appendingPathComponent("wp-tree-back-\(UUID().uuidString.prefix(6))")
        try fm.createDirectory(at: dst, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: dst) }
        try c.downloadTree(remote, into: dst)
        let back = dst.appendingPathComponent(src.lastPathComponent)
        #expect(try String(contentsOf: back.appendingPathComponent("a/ł.txt"), encoding: .utf8) == "zażółć")
        #expect(try Data(contentsOf: back.appendingPathComponent("a/b/duzy.bin")).count == 200_000)
    }

    @Test func przerwaniePobieraniaZostawiaKanalSprawny() throws {
        let c = try Self.connect()
        defer { c.close() }
        let home = try c.realPath(".")
        let f = RemotePath.join(home, "wp-cancel-\(UUID().uuidString.prefix(6)).bin")
        try c.writeFile(f, data: Data(count: 2 * 1024 * 1024), flags: [.write, .create, .truncate])
        defer { try? c.remove(f) }
        #expect(throws: SftpError.cancelled) {
            try c.download(f, sink: { _, _ in }, progress: { $0 < 300_000 })
        }
        // Po przerwaniu kolejne żądania nadal działają (odpowiedzi w locie zostały odebrane).
        #expect(try c.stat(f).size == 2 * 1024 * 1024)
    }

    @Test func bledySerwera() throws {
        let c = try Self.connect()
        defer { c.close() }
        #expect(throws: SftpError.status(.noSuchFile, "No such file")) { _ = try c.stat("/nie/ma/takiego") }
        do { _ = try c.list("/root"); Issue.record("lista /root nie powinna przejść") }
        catch let e as SftpError { #expect(e.code == .permissionDenied) }
    }

    @Test func nieudaneLogowanieMowiDlaczego() throws {
        do {
            _ = try Self.connect(user: "nie-ma-takiego-uzytkownika")
            Issue.record("logowanie nie powinno przejść")
        } catch let SftpError.disconnected(detail) {
            #expect(detail.contains("Permission denied"), "\(detail)")
        }
    }
}
