import Foundation
import Testing
@testable import WaypointCore

/// Te same przypadki co ConnectionStatsTests w wersji Windows + format linii dziennika.
@Suite struct ConnectionLogTests {
    let lines = [
        "2026-07-03 09:00:00  CONNECTED    web1 (10.0.0.1:3389) user=a",
        "2026-07-03 10:00:00  CONNECTED    web1 (10.0.0.1:3389) user=a",
        "2026-07-02 08:00:00  CONNECTED    db1 (10.0.0.2:22) user=b",
        "2026-07-03 11:00:00  DISCONNECTED web1 (10.0.0.1:3389) user=a",
        "2026-07-03 11:30:00  FAILED       db1 (10.0.0.2:22) user=b",
        "kompletny smiec bez sensu",
        "",
    ]
    func date(_ y: Int, _ m: Int, _ d: Int, _ h: Int = 12) -> Date {
        var c = DateComponents(); (c.year, c.month, c.day, c.hour) = (y, m, d, h); c.timeZone = .current
        return Calendar(identifier: .gregorian).date(from: c)!
    }

    @Test func dniISerwery() {
        let s = ConnectionStats.compute(lines, now: date(2026, 7, 3), days: 7)
        #expect(s.totalConnects == 3)
        #expect(s.perDay.count == 7)
        #expect(s.perDay[6] == 2 && s.perDay[5] == 1 && s.perDay[0] == 0)
        #expect(s.topServers.map(\.name) == ["web1", "db1"])
        #expect(s.topServers[0].count == 2)
    }

    @Test func dniTygodnia() {
        let s = ConnectionStats.compute(lines, now: date(2026, 7, 3), days: 7)   // 07-03 = piątek
        #expect(s.perWeekday[4] == 2 && s.perWeekday[3] == 1 && s.perWeekday[0] == 0)
    }

    @Test func pusteIPozaOknem() {
        #expect(ConnectionStats.compute([], now: date(2026, 7, 3), days: 14).perDay.count == 14)
        let old = ConnectionStats.compute(["2026-06-01 08:00:00  CONNECTED    web1 (10.0.0.1:3389) user=a"],
                                          now: date(2026, 7, 3), days: 7)
        #expect(old.totalConnects == 1 && old.perDay.allSatisfy { $0 == 0 })
    }

    @Test func formatJakWWindows() {
        let s = Server(name: "web\n1", host: "10.0.0.1", port: 3389, username: "jan", domain: "FIRMA", proto: .rdp)
        let line = ConnectionLog.format(date(2026, 7, 3, 9), event: "CONNECTED", server: s)
        #expect(line == "2026-07-03 09:00:00  CONNECTED    web 1 (10.0.0.1:3389) user=FIRMA\\jan")
        #expect(ConnectionLog.format(date(2026, 7, 3, 9), event: "DISCONNECTED", server: Server(host: "h")).hasSuffix(
            "DISCONNECTED - (h:22) user=-"))
        // Linia z format() wraca do statystyk.
        #expect(ConnectionStats.compute([line], now: date(2026, 7, 3), days: 1).topServers.first?.name == "web 1")
    }

    @Test func nawiasWNazwie() {
        let l = ["2026-07-03 09:00:00  CONNECTED    sshd (Pęk kluczy) (127.0.0.1:2222) user=a",
                 "2026-07-03 09:00:00  CONNECTED    bez portu"]
        #expect(ConnectionStats.compute(l, now: date(2026, 7, 3), days: 1).topServers.map(\.name).sorted()
                == ["bez portu", "sshd (Pęk kluczy)"])
    }

    @Test func dopisywanieDoPliku() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("wp-log-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: dir) }
        ConnectionLog.append("CONNECTED", server: Server(name: "a", host: "h"), dir: dir)
        ConnectionLog.append("FAILED", server: Server(name: "b", host: "h"), dir: dir)
        let l = ConnectionLog.readLines(dir: dir)
        #expect(l.count == 2 && l[1].contains("FAILED"))
    }
}

@Suite struct ServerListToolsTests {
    let a = Server(id: "a", name: "a", group: "G1")
    let b = Server(id: "b", name: "b", group: "G1")
    let c = Server(id: "c", name: "c", group: "G2")
    let p = Server(id: "p", name: "p", group: "G2", pinned: true)

    @Test func zmianaNazwyGrupy() {
        let r = ServerList.renameGroup([a, b, c], from: "G1", to: " Nowa ")
        #expect(r.map(\.group) == ["Nowa", "Nowa", "G2"])
        #expect(ServerList.renameGroup([a], from: "G1", to: "  ").map(\.group) == ["G1"])
    }

    @Test func przeciaganie() {
        #expect(ServerList.move([a, b, c], id: "a", relativeTo: "b", after: true).map(\.id) == ["b", "a", "c"])
        let r = ServerList.move([a, b, c], id: "c", relativeTo: "a", after: false)
        #expect(r.map(\.id) == ["c", "a", "b"] && r[0].group == "G1")   // inna grupa = przeniesienie
        let pin = ServerList.move([a, p], id: "a", relativeTo: "p", after: true)
        #expect(pin[1].pinned && pin[1].group == "G1")                 // do „Przypiętych" — grupa zostaje
    }

    @Test func onMoveWSekcji() {
        let list = [a, b, c, Server(id: "d", name: "d", group: "G1")]
        let g1 = ServerList.sections(list).first { $0.kind == .group("G1") }!
        // [a, b, d] — „a" na koniec (dest = 3).
        #expect(ServerList.move(list, in: g1, from: [0], to: 3).filter { $0.group == "G1" }.map(\.id) == ["b", "d", "a"])
        // „d" na początek.
        #expect(ServerList.move(list, in: g1, from: [2], to: 0).filter { $0.group == "G1" }.map(\.id) == ["d", "a", "b"])
        // a i b za d.
        #expect(ServerList.move(list, in: g1, from: [0, 1], to: 3).filter { $0.group == "G1" }.map(\.id) == ["d", "a", "b"])
    }

    @Test func ostatnie() {
        var s = MacSettings()
        for id in ["a", "b", "a", "x"] { s.recordRecent(id, max: 3) }
        #expect(s.recentIds == ["x", "a", "b"])
        #expect(ServerList.recents([a, b], ids: s.recentIds).map(\.id) == ["a", "b"])   // usunięte „x" pominięte
    }

    @Test func ustawieniaZakresy() throws {
        let d = try JSONDecoder().decode(MacSettings.self, from: Data(#"{"ReachabilityIntervalSec":1,"ProbeTimeoutSeconds":999}"#.utf8))
        #expect(d.reachabilityIntervalSec == 5 && d.probeTimeoutSeconds == 60 && d.reachabilityEnabled && !d.showLatency)
    }
}

@Suite struct TcpProbeTests {
    @Test func zamknietyPort() {
        // Port 1 na pętli zwrotnej: natychmiastowe RST.
        #expect(TcpProbe.probe(host: "127.0.0.1", port: 1, timeout: 1) == nil)
        #expect(TcpProbe.probe(host: "", port: 22, timeout: 1) == nil)
        #expect(TcpProbe.probe(host: "nie-ma-takiego-hosta.invalid", port: 22, timeout: 1) == nil)
    }

    @Test func otwartyPort() throws {
        // Własny nasłuch na losowym porcie — bez zależności od sshd.
        let fd = socket(AF_INET, Int32(SOCK_STREAM_VALUE), 0)
        #expect(fd >= 0)
        defer { close(fd) }
        var addr = sockaddr_in()
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_addr.s_addr = inet_addr("127.0.0.1")
        addr.sin_port = 0
        var len = socklen_t(MemoryLayout<sockaddr_in>.size)
        withUnsafeMutablePointer(to: &addr) { p in
            p.withMemoryRebound(to: sockaddr.self, capacity: 1) { sa in
                _ = bind(fd, sa, len)
                _ = listen(fd, 4)
                _ = getsockname(fd, sa, &len)
            }
        }
        let port = Int(UInt16(bigEndian: addr.sin_port))
        let ms = TcpProbe.probe(host: "127.0.0.1", port: port, timeout: 2)
        #expect(ms != nil && ms! < 1000)
    }
}

#if canImport(Glibc)
import Glibc
private let SOCK_STREAM_VALUE = SOCK_STREAM.rawValue
#else
private let SOCK_STREAM_VALUE = SOCK_STREAM
#endif

@Suite struct CredentialsTests {
    @Test func plikZWindows() throws {
        let json = #"[{"Id":"p1","Name":"ACME admin","Domain":"ACME","Username":"admin","Nowe":1}]"#
        let list = try CredentialProfileStore.decode(Data(json.utf8))
        #expect(list.count == 1 && list[0].login == "ACME\\admin" && list[0].keychainAccount == "profile:p1")
        let back = String(decoding: try JSONEncoder().encode(list), as: UTF8.self)
        #expect(back.contains("\"Nowe\":1") && back.contains("\"Username\":\"admin\""))
    }

    @Test func zapisIOdczyt() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("wp-cred-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: dir) }
        let st = CredentialProfileStore(directory: dir)
        #expect(st.load().isEmpty)
        try st.save([CredentialProfile(id: "a", name: "A", username: "u")])
        try st.save([CredentialProfile(id: "a", name: "A", username: "u"), CredentialProfile(id: "b")])
        #expect(st.load().map(\.id) == ["a", "b"])
    }

    @Test func rozwiazanieLoginu() {
        var s = Server(id: "s1", name: "web", host: "h", username: "wlasny", proto: .rdp)
        s.credentialProfileId = "p1"
        let p = CredentialProfile(id: "p1", name: "ACME", domain: "ACME", username: "admin")
        let r = Credentials.resolve(s, profiles: [p])
        #expect(r.username == "admin" && r.domain == "ACME" && r.keychainAccount == "profile:p1")
        // Usunięty profil → własny login serwera i własne hasło.
        let gone = Credentials.resolve(s, profiles: [])
        #expect(gone.username == "wlasny" && gone.keychainAccount == "s1")
        let a = Credentials.connectAs(s, user: " root ", domain: "")
        #expect(a.username == "root" && a.credentialProfileId.isEmpty && a.keychainAccount == "s1")
        #expect(Credentials.splitLogin("FIRMA\\jan") == ("jan", "FIRMA"))
        let d = Credentials.detach([s, Server(id: "x")], profileId: "p1")
        #expect(d.changed == 1 && d.servers[0].credentialProfileId.isEmpty)
        // Pole zapisuje się pod nazwą z Windows.
        let json = String(decoding: try! ServerStore.encode([s]), as: UTF8.self)
        #expect(json.contains("\"CredentialProfileId\" : \"p1\""))
    }

    @Test func scalanieImportu() {
        let r = CredentialProfileStore.merge([CredentialProfile(id: "a", name: "stary")],
                                             [CredentialProfile(id: "a", name: "nowy"), CredentialProfile(id: "b")])
        #expect(r.added == 1 && r.updated == 1 && r.list[0].name == "nowy")
    }
}

/// Te same własności co PasswordGenTests w Windows.
@Suite struct PasswordGenTests {
    @Test func dlugoscIKlasy() {
        var o = PasswordGen.Options()
        o.length = 4
        for _ in 0..<200 {
            let p = PasswordGen.password(o)
            #expect(p.count == 4)
            #expect(p.contains(where: PasswordGen.upper.contains) && p.contains(where: PasswordGen.lower.contains)
                    && p.contains(where: PasswordGen.digits.contains) && p.contains(where: PasswordGen.symbols.contains))
        }
    }

    @Test func bezMylacych() {
        var o = PasswordGen.Options()
        o.length = 200; o.excludeAmbiguous = true
        #expect(!PasswordGen.password(o).contains(where: PasswordGen.ambiguous.contains))
    }

    @Test func brzegowe() {
        var o = PasswordGen.Options()
        o.upper = false; o.lower = false; o.digits = false; o.symbols = false
        #expect(PasswordGen.password(o).isEmpty)
        o.digits = true; o.length = 0
        #expect(PasswordGen.password(o).isEmpty)
        #expect(PasswordGen.hexToken(bytes: 16).count == 32)
        #expect(PasswordGen.hexToken(bytes: 16).allSatisfy { "0123456789abcdef".contains($0) })
        #expect(PasswordGen.guid().count == 36)
        #expect(abs(PasswordGen.entropyBits(length: 10, poolSize: 64) - 60) < 0.001)
        #expect(PasswordGen.entropyBits(length: 10, poolSize: 1) == 0)
    }

    @Test func rozne() {
        let o = PasswordGen.Options()
        #expect(Set((0..<50).map { _ in PasswordGen.password(o) }).count == 50)
    }
}
