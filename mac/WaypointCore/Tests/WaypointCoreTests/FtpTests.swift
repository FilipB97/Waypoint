import Foundation
import Testing
@testable import WaypointCore

@Suite struct FtpListingTests {
    let now = Date(timeIntervalSince1970: 1_791_590_400)   // 2026-10-10 00:00 UTC

    @Test func formatUniksowy() {
        let items = FtpListing.parse("""
        total 12
        drwxr-xr-x    2 1000     1000         4096 Oct 09 14:27 bin
        -rw-r--r--    1 1000     1000          123 Mar 31  2024 notatki z urlopu.txt
        lrwxrwxrwx    1 0        0               9 Oct 09 14:27 link.conf -> real.conf
        lrwxrwxrwx    1 0        0               9 Oct 09 14:27 www -> /var/www/
        drwxrwsr-x    2 0        33           4096 Oct 09 14:27 shared
        """, now: now)
        #expect(items.map(\.name) == ["bin", "notatki z urlopu.txt", "link.conf", "www", "shared"])
        #expect(items[0].attributes.isDirectory)
        #expect(items[1].attributes.size == 123)
        #expect(items[1].attributes.mode == 0o644)
        #expect(items[2].attributes.isSymlink && !items[2].linkLooksLikeDirectory)
        #expect(items[3].linkLooksLikeDirectory)
        #expect(items[4].attributes.mode == 0o2775)
        // „Mar 31 2024" → 2024-03-31; „Oct 09 14:27" bez roku → bieżący rok (nie w przyszłości)
        #expect(items[1].attributes.modified.map { Calendar(identifier: .gregorian).dateComponents(in: TimeZone(identifier: "UTC")!, from: $0).year } == 2024)
        #expect(items[0].attributes.modified! <= now)
    }

    @Test func dataBezRokuZPrzyszlosciToPoprzedniRok() {
        let i = FtpListing.parse("-rw-r--r-- 1 u g 1 Dec 24 10:00 x", now: now)
        #expect(Calendar(identifier: .gregorian).dateComponents(in: TimeZone(identifier: "UTC")!, from: i[0].attributes.modified!).year == 2025)
    }

    @Test func formatDOS() {
        let items = FtpListing.parse("""
        10-09-26  02:58PM       <DIR>          Nowy folder
        10-09-26  02:58PM                 1234 raport 2026.xlsx
        """, now: now)
        #expect(items.map(\.name) == ["Nowy folder", "raport 2026.xlsx"])
        #expect(items[0].attributes.isDirectory)
        #expect(items[1].attributes.size == 1234)
    }

    @Test func smieciIgnorowane() {
        #expect(FtpListing.parse("\n\nnie wiem co to\n", now: now).isEmpty)
    }

    @Test func adresy() throws {
        // Bez łączenia — tylko budowa adresów (ścieżki bezwzględne przez %2F, kodowanie spacji).
        #expect(FtpPaths.file(base: "ftp://h:21", "/var/www/a b.txt") == "ftp://h:21/%2Fvar/www/a%20b.txt")
        #expect(FtpPaths.dir(base: "ftp://h:21", "/") == "ftp://h:21/%2F")
        #expect(FtpPaths.dir(base: "ftp://h:21", "/home/wpt") == "ftp://h:21/%2Fhome/wpt/")
    }
}

/// Test na prawdziwym vsftpd: WAYPOINT_FTP_TEST=host:user:hasło:portFTP:portFTPSjawne:portFTPSniejawne
@Suite(.enabled(if: ProcessInfo.processInfo.environment["WAYPOINT_FTP_TEST"] != nil))
struct FtpIntegrationTests {
    static var cfg: [String] { ProcessInfo.processInfo.environment["WAYPOINT_FTP_TEST"]!.split(separator: ":").map(String.init) }

    static func client(_ enc: FtpEncryption, password: String? = nil, accept: Bool = true) throws -> FtpClient {
        let c = cfg
        let port = enc == .none ? Int(c[3])! : enc == .explicitTLS ? Int(c[4])! : Int(c[5])!
        return try FtpClient(host: c[0], port: port, user: c[1], password: password ?? c[2], encryption: enc,
                             acceptInvalidCertificate: accept)
    }

    @Test(arguments: [FtpEncryption.none, .explicitTLS, .implicitTLS])
    func pelnyCykl(enc: FtpEncryption) throws {
        let c = try Self.client(enc)
        defer { c.close() }
        let home = try c.realPath(".")
        #expect(home.hasPrefix("/"))
        let dir = RemotePath.join(home, "wp-ftp-\(enc.rawValue)-\(UUID().uuidString.prefix(6))")
        try c.mkdir(dir)
        defer { try? c.removeTree(dir) }

        var bytes = [UInt8](repeating: 0, count: 700_000)
        for i in bytes.indices { bytes[i] = UInt8(truncatingIfNeeded: i &* 31 >> 3) }
        let local = FileManager.default.temporaryDirectory.appendingPathComponent("wp-ftp-\(UUID().uuidString)")
        try Data(bytes).write(to: local)
        defer { try? FileManager.default.removeItem(at: local) }

        let remote = RemotePath.join(dir, "dane z polskim ąę.bin")
        var last: UInt64 = 0
        try c.upload(from: local, to: remote, overwrite: false) { last = $0; return true }
        #expect(last == UInt64(bytes.count))
        #expect(throws: SftpError.self) { try c.upload(from: local, to: remote, overwrite: false) }
        #expect(try c.stat(remote).size == UInt64(bytes.count))
        #expect(try c.stat(dir).isDirectory)
        #expect(try c.readFile(remote) == Data(bytes))

        try c.mkdir(RemotePath.join(dir, "pod"))
        try c.writeFile(RemotePath.join(dir, "pod/a.txt"), data: Data("A".utf8))
        let names = try c.list(dir).map(\.name).sorted()
        #expect(names == ["dane z polskim ąę.bin", "pod"])
        #expect(try c.list(dir).first { $0.name == "pod" }?.isDirectory == true)

        try c.rename(RemotePath.join(dir, "pod/a.txt"), to: RemotePath.join(dir, "b.txt"))
        #expect(try c.readFile(RemotePath.join(dir, "b.txt")) == Data("A".utf8))
        #expect(throws: SftpError.self) { _ = try c.stat(RemotePath.join(dir, "nie-ma")) }

        // Drzewa przez wspólne rozszerzenie RemoteFS.
        let back = FileManager.default.temporaryDirectory.appendingPathComponent("wp-ftp-back-\(UUID().uuidString.prefix(6))")
        try FileManager.default.createDirectory(at: back, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: back) }
        try c.downloadTree(dir, into: back)
        #expect(try Data(contentsOf: back.appendingPathComponent(RemotePath.name(dir)).appendingPathComponent("b.txt")) == Data("A".utf8))
    }

    @Test func zapisZEdytoraPrzezFtp() throws {
        let c = try Self.client(.explicitTLS)
        defer { c.close() }
        let f = RemotePath.join(try c.realPath("."), "wp-edit-\(UUID().uuidString.prefix(6)).conf")
        try c.writeFile(f, data: Data("a=1\r\n".utf8))
        defer { try? c.remove(f) }
        let info = try SafeWrite.stat(c, f)
        #expect(info.length == 5)
        #expect(try SafeWrite.write(c, content: Data("a=1\r\nb=2\r\n".utf8), original: info) == .inPlace(reasonKey: "edit.fb.ftp"))
        #expect(try c.readFile(f) == Data("a=1\r\nb=2\r\n".utf8))
    }

    @Test func zleHaslo() {
        do { _ = try Self.client(.none, password: "zle-haslo"); Issue.record("logowanie nie powinno przejść") }
        catch let SftpError.authenticationFailed(m) { #expect(!m.isEmpty) }
        catch { Issue.record("zły błąd: \(error)") }
    }

    @Test func certyfikatSamopodpisanyWymagaZgody() {
        do { _ = try Self.client(.explicitTLS, accept: false); Issue.record("weryfikacja powinna odrzucić certyfikat") }
        catch SftpError.certificateUntrusted { }
        catch { Issue.record("zły błąd: \(error)") }
    }

    @Test func przerwaniePobierania() throws {
        let c = try Self.client(.explicitTLS)
        defer { c.close() }
        let f = RemotePath.join(try c.realPath("."), "wp-cancel-\(UUID().uuidString.prefix(6)).bin")
        try c.writeFile(f, data: Data(count: 3 * 1024 * 1024))
        defer { try? c.remove(f) }
        let local = FileManager.default.temporaryDirectory.appendingPathComponent("wp-c-\(UUID().uuidString)")
        #expect(throws: SftpError.cancelled) { try c.download(f, to: local) { $0 < 200_000 } }
        #expect(!FileManager.default.fileExists(atPath: local.path))
        #expect(try c.stat(f).size == 3 * 1024 * 1024)   // połączenie nadal działa
    }
}
