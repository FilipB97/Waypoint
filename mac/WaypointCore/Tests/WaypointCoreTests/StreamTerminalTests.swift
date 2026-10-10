import Foundation
import Testing
@testable import WaypointCore
import CurlShim
#if canImport(Glibc)
import Glibc
#endif

@Suite struct TelnetCodecTests {
    typealias T = TelnetCodec

    @Test func daneIIacIac() {
        var c = T()
        let r = c.receive([65, T.IAC, T.IAC, 66, 13, 0, 10])
        #expect(r.data == [65, 255, 66, 13, 10] && r.reply.isEmpty)
    }

    @Test func negocjacja() {
        var c = T()
        // Serwer: WILL ECHO, WILL SGA, DO TTYPE(24), DO SGA, WILL 99.
        let r = c.receive([T.IAC, T.WILL, T.ECHO, T.IAC, T.WILL, T.SGA, T.IAC, T.DO, 24, T.IAC, T.DO, T.SGA, T.IAC, T.WILL, 99])
        #expect(r.reply == [T.IAC, T.DO, T.ECHO, T.IAC, T.DO, T.SGA, T.IAC, T.WONT, 24, T.IAC, T.WILL, T.SGA, T.IAC, T.DONT, 99])
        #expect(r.data.isEmpty)
        // Powtórzone WILL ECHO — bez odpowiedzi (brak pętli); WONT ECHO — potwierdzenie raz.
        #expect(c.receive([T.IAC, T.WILL, T.ECHO]).reply.isEmpty)
        #expect(c.receive([T.IAC, T.WONT, T.ECHO]).reply == [T.IAC, T.DONT, T.ECHO])
        #expect(c.receive([T.IAC, T.WONT, T.ECHO]).reply.isEmpty)
    }

    @Test func przezGraniceBuforowISubnegocjacja() {
        var c = T()
        #expect(c.receive([72, T.IAC]).data == [72])
        #expect(c.receive([T.WILL]).reply.isEmpty)
        #expect(c.receive([T.ECHO, 73]).reply == [T.IAC, T.DO, T.ECHO])
        let sb = c.receive([T.IAC, T.SB, 24, 1, T.IAC, T.SE, 74])
        #expect(sb.data == [74])
    }

    @Test func kodowanieWejscia() {
        #expect(T.encode([97, 13]) == [97, 13, 0])
        #expect(T.encode([13, 10]) == [13, 10])
        #expect(T.encode([255]) == [255, 255])
    }
}

@Suite struct StreamHelperTests {
    @Test func argumentyIAdresy() {
        #expect(StreamHelper.telnetArguments(host: "sw1", port: 23) == ["--waypoint-helper", "telnet", "sw1", "23"])
        #expect(ExternalLinks.webURL("grafana.local:3000/d")?.absoluteString == "https://grafana.local:3000/d")
        #expect(ExternalLinks.webURL("http://x.pl")?.absoluteString == "http://x.pl")
        #expect(ExternalLinks.webURL("file:///etc/passwd") == nil)
        #expect(ExternalLinks.webURL("javascript:alert(1)") == nil)
        #expect(ExternalLinks.webURL("  ") == nil)
        #expect(ExternalLinks.vncURL(Server(host: "10.0.0.5", proto: .vnc))?.absoluteString == "vnc://10.0.0.5")
        #expect(ExternalLinks.vncURL(Server(host: "pc", port: 5901, username: "jan", proto: .vnc))?.absoluteString == "vnc://jan@pc:5901")
        #expect(ExternalLinks.vncURL(Server(host: "a/b", proto: .vnc)) == nil)
    }

    /// Prawdziwy serwer TCP: negocjuje, wysyła tekst, odbiera to, co „wpisano", i zamyka.
    @Test func telnetPrzezGniazdo() throws {
        let lfd = socket(AF_INET, Int32(SOCK_STREAM_VALUE2), 0)
        defer { close(lfd) }
        var addr = sockaddr_in()
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_addr.s_addr = inet_addr("127.0.0.1")
        var len = socklen_t(MemoryLayout<sockaddr_in>.size)
        withUnsafeMutablePointer(to: &addr) { p in
            p.withMemoryRebound(to: sockaddr.self, capacity: 1) { sa in
                _ = bind(lfd, sa, len); _ = listen(lfd, 1); _ = getsockname(lfd, sa, &len)
            }
        }
        let port = Int(UInt16(bigEndian: addr.sin_port))
        let received = Box()
        let server = Thread {
            let c = accept(lfd, nil, nil)
            _ = StreamHelper.writeAll(c, [255, 251, 1] + Array("login: ".utf8))
            var buf = [UInt8](repeating: 0, count: 256)
            var got: [UInt8] = []
            while got.count < 9 { let n = read(c, &buf, 256); if n <= 0 { break }; got += buf[0..<n] }
            received.value = got
            close(c)
        }
        server.start()

        guard let fd = TcpProbe.connect(host: "127.0.0.1", port: port, timeout: 2) else { Issue.record("brak połączenia"); return }
        var input: [Int32] = [0, 0], output: [Int32] = [0, 0]
        _ = pipe(&input); _ = pipe(&output)
        _ = StreamHelper.writeAll(input[1], Array("root\r".utf8))
        var codec = TelnetCodec()
        StreamHelper.pump(local: input[0], output: output[1], remote: fd,
                          fromRemote: { codec.receive($0) }, fromLocal: { TelnetCodec.encode($0) })
        close(fd); close(output[1])
        var buf = [UInt8](repeating: 0, count: 256)
        let n = read(output[0], &buf, 256)
        #expect(String(decoding: buf[0..<max(0, n)], as: UTF8.self) == "login: ")
        // Odpowiedź DO ECHO i „root" + CR NUL — w dowolnej kolejności (wejście mogło pójść przed banerem).
        let neg: [UInt8] = [255, 253, 1], typed: [UInt8] = Array("root".utf8) + [13, 0]
        #expect(received.value == neg + typed || received.value == typed + neg)
        [input[0], input[1], output[0]].forEach { close($0) }
    }

    /// Port szeregowy na pseudo-terminalu (strona „urządzenia" to master).
    @Test func portSzeregowyNaPty() throws {
        var name = [CChar](repeating: 0, count: 128)
        let master = wp_open_pty(&name, 128)
        #expect(master >= 0)
        defer { close(master) }
        let path = String(cString: name)
        guard case .success(let fd) = SerialPort.open(path, baud: 115_200) else { Issue.record("open"); return }
        defer { close(fd) }
        _ = StreamHelper.writeAll(master, Array("AT\r".utf8))
        var buf = [UInt8](repeating: 0, count: 16)
        let n = read(fd, &buf, 16)
        #expect(Array(buf[0..<max(0, n)]) == Array("AT\r".utf8))   // surowo — bez zamiany CR
        #if !canImport(Darwin)   // macOS przyjmuje dowolną prędkość, Linux tylko stałe B…
        if case .failure = SerialPort.open(path, baud: 123) {} else { Issue.record("dziwna prędkość powinna być odrzucona na Linuksie") }
        #endif
        if case .success = SerialPort.open("/dev/nie-ma-takiego", baud: 9600) { Issue.record("nieistniejące urządzenie") }
    }
}

final class Box: @unchecked Sendable { var value: [UInt8] = [] }

#if canImport(Glibc)
private let SOCK_STREAM_VALUE2 = SOCK_STREAM.rawValue
#else
private let SOCK_STREAM_VALUE2 = SOCK_STREAM
#endif
