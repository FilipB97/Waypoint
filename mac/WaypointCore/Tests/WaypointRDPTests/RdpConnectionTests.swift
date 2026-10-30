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
    func add(_ f: () -> Void) { lock.lock(); f(); lock.unlock() }
    func read<T>(_ f: () -> T) -> T { lock.lock(); defer { lock.unlock() }; return f() }
}

func waitUntil(_ seconds: Double, _ cond: () -> Bool) -> Bool {
    let end = Date().addingTimeInterval(seconds)
    while Date() < end { if cond() { return true }; Thread.sleep(forTimeInterval: 0.05) }
    return cond()
}

@Suite struct RdpUnitTests {
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
        let nonBlack = c.withFrame { data, w, h, stride -> Int in
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
        #expect(nonBlack > 10, "obraz pusty")
        // Tło sesji testowej #336699 (xsetroot) — piksel w BGRA: 0x99 0x66 0x33. Kolejność bajtów
        // musi się zgadzać, bo widok na macOS tworzy z tego bufora CGImage bez konwersji.
        if ProcessInfo.processInfo.environment["WAYPOINT_RDP_TEST_COLOR"] == "336699" {
            let px = c.withFrame { data, w, h, stride -> [UInt8] in
                guard let data else { return [] }
                let p = data + (h / 2) * stride + (w / 2) * 4
                return [p[0], p[1], p[2]]
            }
            #expect(px == [0x99, 0x66, 0x33], "środek obrazu: \(px)")
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
