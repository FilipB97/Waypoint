import Foundation
import Testing
@testable import WaypointRDP

/// Prawdziwe połączenie z serwerem RDP (xrdp w CI na Linuksie).
/// WAYPOINT_RDP_TEST=host:port:user:password — bez zmiennej testy połączenia są pomijane.
let rdpTarget: (host: String, port: Int, user: String, password: String)? = {
    guard let v = ProcessInfo.processInfo.environment["WAYPOINT_RDP_TEST"] else { return nil }
    let p = v.split(separator: ":", maxSplits: 3).map(String.init)
    guard p.count == 4, let port = Int(p[1]) else { return nil }
    return (p[0], port, p[2], p[3])
}()

/// Zbiera zdarzenia z wątku połączenia.
final class Recorder: @unchecked Sendable {
    let lock = NSLock()
    var states: [RdpConnection.State] = []
    var frames = 0
    var sizes: [(Int, Int)] = []
    var certificates: [RdpConnection.Certificate] = []
    var clipboard: [String] = []
    var files: [[RdpConnection.RemoteFile]] = []
    var cursors: [RdpConnection.Cursor] = []
    func add(_ f: () -> Void) { lock.lock(); f(); lock.unlock() }
    func read<T>(_ f: () -> T) -> T { lock.lock(); defer { lock.unlock() }; return f() }
}

func waitUntil(_ seconds: Double, _ cond: () -> Bool) -> Bool {
    let end = Date().addingTimeInterval(seconds)
    while Date() < end { if cond() { return true }; Thread.sleep(forTimeInterval: 0.05) }
    return cond()
}

@Suite struct RdpUnitTests {
    @Test func bezpieczneSciezkiZSesji() {
        #expect(RdpConnection.safeRelativePath("a\\b\\c.txt") == "a/b/c.txt")
        #expect(RdpConnection.safeRelativePath("../etc/passwd") == nil)
        #expect(RdpConnection.safeRelativePath("/abs/./x") == "abs/x")
        #expect(RdpConnection.safeRelativePath("") == nil)
    }

    @Test func listaPlikowZMaca() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("wp-src-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: root.appendingPathComponent("dir/sub"), withIntermediateDirectories: true)
        try Data("abc".utf8).write(to: root.appendingPathComponent("dir/sub/x.txt"))
        try Data("12345".utf8).write(to: root.appendingPathComponent("plik.bin"))
        let list = RdpConnection.clipboardEntries(for: [root.appendingPathComponent("dir"), root.appendingPathComponent("plik.bin")])
        #expect(list.map(\.name) == ["dir", "dir/sub", "dir/sub/x.txt", "plik.bin"])
        #expect(list.map(\.isDirectory) == [true, true, false, false])
        #expect(list.last?.size == 5)
    }

    @Test func flagiKolka() {
        #expect(RdpMouse.wheelFlags(delta: 120) == 0x0200 | 120)
        #expect(RdpMouse.wheelFlags(delta: -120) == 0x0200 | 0x0100 | 0x88)   // -120 w 9 bitach
        #expect(RdpMouse.wheelFlags(delta: 1000) == 0x0200 | 0xFF)
        #expect(RdpMouse.wheelFlags(delta: -10, horizontal: true) & 0x0400 != 0)
    }

    @Test(.enabled(if: RdpConnection.isAvailable)) func kodyKlawiszyMac() {
        // kVK_ANSI_A = 0x00 → A (0x1E), Return 0x24 → 0x1C, strzałka w lewo 0x7B → 0x4B z flagą extended.
        #expect(RdpConnection.scancode(macKeyCode: 0x00) == 0x1E)
        #expect(RdpConnection.scancode(macKeyCode: 0x24) == 0x1C)
        #expect(RdpConnection.scancode(macKeyCode: 0x7B) == 0x14B)
        #expect(RdpConnection.isAuthenticationError(0) == false)
        #expect(RdpKeyboard.windowsLayout(forMacInputSource: "com.apple.keylayout.PolishPro") == 0x415)
        #expect(RdpKeyboard.windowsLayout(forMacInputSource: "com.apple.keylayout.Polish") == 0x415)
        #expect(RdpKeyboard.windowsLayout(forMacInputSource: "com.apple.keylayout.Klingon") == 0)
    }
}

@Suite(.enabled(if: rdpTarget != nil && RdpConnection.isAvailable), .serialized)
struct RdpConnectionTests {
    @Test func polaczenieObrazIRozlaczenie() throws {
        let t = rdpTarget!
        let rec = Recorder()
        let c = RdpConnection()
        c.onState = { s in rec.add { rec.states.append(s) } }
        c.onFrame = { _ in rec.add { rec.frames += 1 } }
        c.onResize = { w, h in rec.add { rec.sizes.append((w, h)) } }
        c.onCursor = { cur in rec.add { rec.cursors.append(cur) } }
        c.onVerifyCertificate = { cert in rec.add { rec.certificates.append(cert) }; return .acceptOnce }
        var cfg = RdpConnection.Config(host: t.host)
        cfg.port = t.port; cfg.username = t.user; cfg.password = t.password
        cfg.width = 1024; cfg.height = 768
        cfg.configDirectory = FileManager.default.temporaryDirectory.appendingPathComponent("wp-rdp-\(UUID().uuidString)").path
        #expect(c.start(cfg))
        let connected = waitUntil(20) { rec.read { rec.states.contains(.connected) } }
        #expect(connected, "stany: \(rec.read { rec.states })")
        // Certyfikat samopodpisany xrdp → pytanie (TOFU).
        #expect(rec.read { rec.certificates.count } >= 1)
        #expect(rec.read { rec.certificates.first?.fingerprint.isEmpty == false })
        // Pierwsze klatki i niepusty obraz.
        #expect(waitUntil(15) { rec.read { rec.frames } > 0 })
        let size = c.withFrame { _, w, h, _ in (w, h) }
        #expect(size.0 == 1024 && size.1 == 768)
        // Sesja X na świeżej maszynie startuje chwilę — pierwsze klatki bywają czarne, zanim xsetroot pomaluje tło.
        func nonBlack() -> Int {
            c.withFrame { data, w, h, stride -> Int in
                guard let data else { return 0 }
                var n = 0
                for y in Swift.stride(from: 0, to: h, by: 16) {
                    for x in Swift.stride(from: 0, to: w, by: 16) {
                        let p = data + y * stride + x * 4
                        if p[0] != 0 || p[1] != 0 || p[2] != 0 { n += 1 }
                    }
                }
                return n
            }
        }
        #expect(waitUntil(20) { nonBlack() > 10 }, "obraz pusty")
        // Tło sesji testowej #336699 (xsetroot) — piksel w BGRA: 0x99 0x66 0x33. Kolejność bajtów
        // musi się zgadzać, bo widok na macOS tworzy z tego bufora CGImage bez konwersji.
        if ProcessInfo.processInfo.environment["WAYPOINT_RDP_TEST_COLOR"] == "336699" {
            func center() -> [UInt8] {
                c.withFrame { data, w, h, stride -> [UInt8] in
                    guard let data else { return [] }
                    let p = data + (h / 2) * stride + (w / 2) * 4
                    return [p[0], p[1], p[2]]
                }
            }
            #expect(waitUntil(10) { center() == [0x99, 0x66, 0x33] }, "środek obrazu: \(center())")
        }
        // Zmiana rozdzielczości w trakcie sesji (Display Control) — jeśli serwer ją obsługuje.
        // Jak w aplikacji: zmiana rozmiaru dopiero gdy sesja się ustabilizuje (okno wysyła ją z opóźnieniem).
        Thread.sleep(forTimeInterval: 1.5)
        c.resize(width: 1280, height: 720, scalePercent: 100)
        let resized = waitUntil(10) { rec.read { rec.sizes.contains { $0 == (1280, 720) } } }
        if ProcessInfo.processInfo.environment["WAYPOINT_RDP_TEST_RESIZE"] == "1" {
            #expect(resized, "rozmiary: \(rec.read { rec.sizes })")
            #expect(c.withFrame { _, w, h, _ in (w, h) } == (1280, 720))
        }
        // Kursor z sesji (xrdp wysyła własny przy starcie i po ruchu myszy): obraz z sensownym punktem aktywnym.
        c.mouse(flags: RdpMouse.move, x: 50, y: 50)
        let cursorOK = waitUntil(10) { rec.read { rec.cursors.contains {
            if case .image(let w, let h, let hx, let hy, let data) = $0 {
                return w > 0 && h > 0 && hx < w && hy < h && data.count == w * h * 4 && data.contains { $0 != 0 }
            }
            return false
        } } }
        #expect(cursorOK, "kursory: \(rec.read { rec.cursors.count })")
        // Wejście nie może wywrócić połączenia.
        c.mouse(flags: RdpMouse.move, x: 100, y: 100)
        c.key(down: true, scancode: 0x1E); c.key(down: false, scancode: 0x1E)
        c.unicode(down: true, 0x0105); c.unicode(down: false, 0x0105)   // „ą"
        Thread.sleep(forTimeInterval: 1)
        #expect(!rec.read { rec.states.contains { if case .disconnected = $0 { return true }; return false } }, "stany: \(rec.read { rec.states })")
        c.stop()
        // Po stop() wątek się skończył; ponowny stop jest bezpieczny.
        c.stop()
    }

    /// Schowek w obie strony: sesja testowa (startwm.sh w CI) odsyła tekst „WP:x" jako „ECHO:x" —
    /// serwer pobiera nasz tekst (prośba o dane) i ogłasza swój (my go pobieramy).
    @Test(.enabled(if: ProcessInfo.processInfo.environment["WAYPOINT_RDP_TEST_CLIPBOARD"] == "1"))
    func schowekWObieStrony() {
        let t = rdpTarget!
        let rec = Recorder()
        let c = RdpConnection()
        c.onState = { s in rec.add { rec.states.append(s) } }
        c.onVerifyCertificate = { _ in .acceptOnce }
        c.onClipboardText = { text in rec.add { rec.clipboard.append(text) } }
        var cfg = RdpConnection.Config(host: t.host)
        cfg.port = t.port; cfg.username = t.user; cfg.password = t.password
        cfg.configDirectory = FileManager.default.temporaryDirectory.appendingPathComponent("wp-rdp-\(UUID().uuidString)").path
        // Przy okazji dziennik diagnostyczny: przebieg wymiany schowka w pliku.
        let logDir = FileManager.default.temporaryDirectory.appendingPathComponent("wp-rdplog-\(UUID().uuidString)")
        try? FileManager.default.createDirectory(at: logDir, withIntermediateDirectories: true)
        cfg.logDirectory = logDir.path
        #expect(c.start(cfg))
        #expect(waitUntil(20) { rec.read { rec.states.contains(.connected) } })
        Thread.sleep(forTimeInterval: 1.5)
        let token = "zażółć gęślą € \(Int.random(in: 1000...9999))"
        c.setClipboardText("WP:" + token)
        let echoed = waitUntil(15) { rec.read { rec.clipboard.contains("ECHO:" + token) } }
        #expect(echoed, "schowek: \(rec.read { rec.clipboard })")
        c.stop()
        let log = (try? String(contentsOf: logDir.appendingPathComponent("waypoint-rdp.log"), encoding: .utf8)) ?? ""
        #expect(log.contains("Monitor Ready") && log.contains("prośba o dane formatu 0xd"), "dziennik: \(log.suffix(2000))")
        #expect(!log.contains(t.password) && !log.contains(token))   // bez hasła i treści schowka
    }

    /// Pliki z sesji: startwm.sh po „WPFILE:x" wkłada do schowka dwa pliki (mały i 3 MB) jako
    /// text/uri-list; xrdp ogłasza FileGroupDescriptorW, a my pobieramy treść przez FileContents.
    @Test(.enabled(if: ProcessInfo.processInfo.environment["WAYPOINT_RDP_TEST_CLIPBOARD"] == "1"))
    func plikiZSesji() throws {
        let t = rdpTarget!
        let rec = Recorder()
        let c = RdpConnection()
        c.onState = { s in rec.add { rec.states.append(s) } }
        c.onVerifyCertificate = { _ in .acceptOnce }
        c.onClipboardFiles = { list in rec.add { rec.files.append(list) } }
        var cfg = RdpConnection.Config(host: t.host)
        cfg.port = t.port; cfg.username = t.user; cfg.password = t.password
        cfg.configDirectory = FileManager.default.temporaryDirectory.appendingPathComponent("wp-rdp-\(UUID().uuidString)").path
        #expect(c.start(cfg))
        #expect(waitUntil(20) { rec.read { rec.states.contains(.connected) } })
        Thread.sleep(forTimeInterval: 1.5)
        let name = "folder\(Int.random(in: 1000...9999))"
        c.setClipboardText("WPFILE:" + name)
        #expect(waitUntil(15) { rec.read { rec.files.contains { $0.map(\.path).sorted() == ["a.txt", "duzy.bin"] } } },
                "pliki: \(rec.read { rec.files })")
        let list = try #require(rec.read { rec.files.last })
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("wp-clip-\(UUID().uuidString)/\(name)")
        var last: (Int64, Int64) = (0, 0)
        let top = try c.downloadClipboardFiles(list, to: dir, progress: { last = ($0, $1) })
        #expect(top.map(\.lastPathComponent).sorted() == ["a.txt", "duzy.bin"])
        let small = try String(contentsOf: dir.appendingPathComponent("a.txt"), encoding: .utf8)
        #expect(small == "zawartość \(name)\n")
        let big = try FileManager.default.attributesOfItem(atPath: dir.appendingPathComponent("duzy.bin").path)
        #expect((big[.size] as? NSNumber)?.intValue == 3_000_000)
        #expect(last.0 == last.1 && last.0 >= 3_000_000)
        c.stop()
    }

    /// Pliki z Maca do sesji: lista FileGroupDescriptorW i treść na prośby FileContents. Sesja testowa
    /// (startwm.sh) kopiuje wklejone pliki z FUSE xrdp do /tmp/wpclip-out-<użytkownik> i odpowiada „COPIED".
    /// xrdp przenosi tylko pliki (foldery pomija) — foldery sprawdza test listy plików.
    @Test(.enabled(if: ProcessInfo.processInfo.environment["WAYPOINT_RDP_TEST_FILES_OUT"] != nil))
    func plikiDoSesji() throws {
        let t = rdpTarget!
        let out = URL(fileURLWithPath: ProcessInfo.processInfo.environment["WAYPOINT_RDP_TEST_FILES_OUT"]!)
        try? FileManager.default.removeItem(at: out)
        let src = FileManager.default.temporaryDirectory.appendingPathComponent("wp-src-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: src, withIntermediateDirectories: true)
        try Data("zażółć".utf8).write(to: src.appendingPathComponent("maly.txt"))
        let big = Data((0..<700_000).map { UInt8($0 % 251) })
        try big.write(to: src.appendingPathComponent("plik.bin"))

        let rec = Recorder()
        let c = RdpConnection()
        c.onState = { s in rec.add { rec.states.append(s) } }
        c.onVerifyCertificate = { _ in .acceptOnce }
        c.onClipboardText = { text in rec.add { rec.clipboard.append(text) } }
        var cfg = RdpConnection.Config(host: t.host)
        cfg.port = t.port; cfg.username = t.user; cfg.password = t.password
        cfg.configDirectory = FileManager.default.temporaryDirectory.appendingPathComponent("wp-rdp-\(UUID().uuidString)").path
        #expect(c.start(cfg))
        #expect(waitUntil(20) { rec.read { rec.states.contains(.connected) } })
        Thread.sleep(forTimeInterval: 1.5)
        c.setClipboardFiles(RdpConnection.clipboardEntries(for: [src.appendingPathComponent("maly.txt"), src.appendingPathComponent("plik.bin")]))
        #expect(waitUntil(20) { rec.read { rec.clipboard.contains("COPIED") } }, "schowek: \(rec.read { rec.clipboard })")
        #expect((try? Data(contentsOf: out.appendingPathComponent("plik.bin"))) == big)
        #expect((try? String(contentsOf: out.appendingPathComponent("maly.txt"), encoding: .utf8)) == "zażółć")
        c.stop()
    }

    /// Folder Maca jako dysk w sesji (rdpdr/drive): sesja testowa czyta probe.txt z dysku „WPDRIVE"
    /// i odsyła treść przez schowek.
    @Test(.enabled(if: ProcessInfo.processInfo.environment["WAYPOINT_RDP_TEST_FILES_OUT"] != nil))
    func dyskWSesji() throws {
        let t = rdpTarget!
        let share = FileManager.default.temporaryDirectory.appendingPathComponent("wp-drive-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: share, withIntermediateDirectories: true)
        let token = "dysk-\(Int.random(in: 100000...999999))"
        try Data(token.utf8).write(to: share.appendingPathComponent("probe.txt"))
        let rec = Recorder()
        let c = RdpConnection()
        c.onState = { s in rec.add { rec.states.append(s) } }
        c.onVerifyCertificate = { _ in .acceptOnce }
        c.onClipboardText = { text in rec.add { rec.clipboard.append(text) } }
        var cfg = RdpConnection.Config(host: t.host)
        cfg.port = t.port; cfg.username = t.user; cfg.password = t.password
        cfg.drivePath = share.path
        cfg.driveName = "WPDRIVE"
        cfg.configDirectory = FileManager.default.temporaryDirectory.appendingPathComponent("wp-rdp-\(UUID().uuidString)").path
        #expect(c.start(cfg))
        #expect(waitUntil(20) { rec.read { rec.states.contains(.connected) } })
        #expect(waitUntil(20) { rec.read { rec.clipboard.contains("DRIVE:" + token) } }, "schowek: \(rec.read { rec.clipboard })")
        c.stop()
    }

    /// Schowek „na żądanie": ogłoszenie tekstu bez treści; treść dopiero, gdy serwer o nią poprosi
    /// (tak Waypoint unika odczytu schowka Maca w tle). Sesja testowa odsyła „WP:x" jako „ECHO:x".
    @Test(.enabled(if: ProcessInfo.processInfo.environment["WAYPOINT_RDP_TEST_CLIPBOARD"] == "1"))
    func schowekNaZadanie() {
        let t = rdpTarget!
        let rec = Recorder()
        let c = RdpConnection()
        let token = "na-żądanie \(Int.random(in: 1000...9999))"
        c.onState = { s in rec.add { rec.states.append(s) } }
        c.onVerifyCertificate = { _ in .acceptOnce }
        c.onClipboardText = { text in rec.add { rec.clipboard.append(text) } }
        c.onClipboardProvide = { rec.add { rec.clipboard.append("<provide>") }; c.setClipboardText("WP:" + token) }
        var cfg = RdpConnection.Config(host: t.host)
        cfg.port = t.port; cfg.username = t.user; cfg.password = t.password
        cfg.configDirectory = FileManager.default.temporaryDirectory.appendingPathComponent("wp-rdp-\(UUID().uuidString)").path
        #expect(c.start(cfg))
        #expect(waitUntil(20) { rec.read { rec.states.contains(.connected) } })
        Thread.sleep(forTimeInterval: 1.5)
        c.announceClipboard(text: true, files: false)
        #expect(waitUntil(15) { rec.read { rec.clipboard.contains("ECHO:" + token) } }, "schowek: \(rec.read { rec.clipboard })")
        #expect(rec.read { rec.clipboard.contains("<provide>") })
        c.stop()
    }

    /// Dwie sesje naraz w jednym procesie (jak karty Waypointa): schowek ma działać w obu.
    /// WAYPOINT_RDP_TEST_USER2=user:hasło — drugie konto (xrdp łączy to samo konto z tą samą sesją).
    @Test(.enabled(if: ProcessInfo.processInfo.environment["WAYPOINT_RDP_TEST_USER2"] != nil
                   && ProcessInfo.processInfo.environment["WAYPOINT_RDP_TEST_CLIPBOARD"] == "1"))
    func schowekWDwochSesjach() {
        let t = rdpTarget!
        let u2 = ProcessInfo.processInfo.environment["WAYPOINT_RDP_TEST_USER2"]!.split(separator: ":", maxSplits: 1).map(String.init)
        func open(_ user: String, _ pw: String) -> (RdpConnection, Recorder) {
            let rec = Recorder()
            let c = RdpConnection()
            c.onState = { s in rec.add { rec.states.append(s) } }
            c.onVerifyCertificate = { _ in .acceptOnce }
            c.onClipboardText = { text in rec.add { rec.clipboard.append(text) } }
            var cfg = RdpConnection.Config(host: t.host)
            cfg.port = t.port; cfg.username = user; cfg.password = pw
            cfg.configDirectory = FileManager.default.temporaryDirectory.appendingPathComponent("wp-rdp-\(UUID().uuidString)").path
            #expect(c.start(cfg))
            #expect(waitUntil(20) { rec.read { rec.states.contains(.connected) } })
            return (c, rec)
        }
        let (a, ra) = open(t.user, t.password)
        let (b, rb) = open(u2[0], u2[1])
        #expect(waitUntil(15) { a.isClipboardReady }, "pierwsza sesja bez schowka")
        #expect(waitUntil(15) { b.isClipboardReady }, "druga sesja bez schowka")
        Thread.sleep(forTimeInterval: 1)
        b.setClipboardText("WP:druga")
        a.setClipboardText("WP:pierwsza")
        #expect(waitUntil(15) { rb.read { rb.clipboard.contains("ECHO:druga") } }, "druga: \(rb.read { rb.clipboard })")
        #expect(waitUntil(15) { ra.read { ra.clipboard.contains("ECHO:pierwsza") } }, "pierwsza: \(ra.read { ra.clipboard })")
        a.stop(); b.stop()
    }

    @Test func certyfikatOdrzucony() {
        let t = rdpTarget!
        let rec = Recorder()
        let c = RdpConnection()
        c.onState = { s in rec.add { rec.states.append(s) } }
        c.onVerifyCertificate = { _ in .reject }
        var cfg = RdpConnection.Config(host: t.host)
        cfg.port = t.port; cfg.username = t.user; cfg.password = t.password
        cfg.configDirectory = FileManager.default.temporaryDirectory.appendingPathComponent("wp-rdp-\(UUID().uuidString)").path
        #expect(c.start(cfg))
        let ended = waitUntil(20) { rec.read { rec.states.contains { if case .disconnected(let e, _) = $0 { return e != 0 }; return false } } }
        #expect(ended, "stany: \(rec.read { rec.states })")
        #expect(!rec.read { rec.states.contains(.connected) })
        c.stop()
    }

    @Test func zamknietyPort() {
        let rec = Recorder()
        let c = RdpConnection()
        c.onState = { s in rec.add { rec.states.append(s) } }
        var cfg = RdpConnection.Config(host: "127.0.0.1")
        cfg.port = 1
        #expect(c.start(cfg))
        let ended = waitUntil(20) { rec.read { rec.states.contains { if case .disconnected(let e, _) = $0 { return e != 0 }; return false } } }
        #expect(ended, "stany: \(rec.read { rec.states })")
        c.stop()
    }
}
