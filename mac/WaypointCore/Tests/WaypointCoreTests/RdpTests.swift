import Foundation
import Testing
@testable import WaypointCore

@Suite struct RdpFileTests {
    @Test func serializacjaJakMstsc() throws {
        var s = try JSONDecoder().decode(Server.self, from: Data(windowsServer.utf8))
        s.proto = .rdp
        s.port = 3390
        s.domain = "FIRMA"
        let text = RdpFile.serialize(s)
        #expect(text.hasSuffix("\r\n"))
        let lines = text.components(separatedBy: "\r\n")
        #expect(lines.contains("full address:s:web01.example.com:3390"))
        #expect(lines.contains("username:s:FIRMA\\deploy"))
        #expect(lines.contains("redirectclipboard:i:1"))
        #expect(lines.contains("authentication level:i:2"))
        // Pola z wersji Windows (RemoteApp, brama) trafiają do pliku.
        #expect(lines.contains("remoteapplicationmode:i:1"))
        #expect(lines.contains("remoteapplicationprogram:s:||Excel"))
        #expect(lines.contains("gatewayhostname:s:rdg.example.com"))
        #expect(lines.contains("gatewayusagemethod:i:1"))
    }

    @Test func domyslnyPortIIPv6() {
        #expect(RdpFile.serialize(Server(host: "h", proto: .rdp)).hasPrefix("full address:s:h\r\n"))
        #expect(RdpFile.serialize(Server(host: "fe80::1", port: 3390, proto: .rdp)).hasPrefix("full address:s:[fe80::1]:3390\r\n"))
    }

    @Test func odczytIZapisSaSpojne() {
        var s = Server(name: "Biuro", host: "rdp.example.com", port: 3391, username: "filip", domain: "FIRMA", proto: .rdp)
        s.rdpAdminSession = true
        s.rdpRedirectClipboard = false
        s.rdpGatewayHostname = "gw.example.com"
        s.rdpGatewayUsageMethod = 2
        let back = RdpFile.parse(RdpFile.serialize(s))
        #expect(back.host == "rdp.example.com")
        #expect(back.port == 3391)
        #expect(back.username == "filip")
        #expect(back.domain == "FIRMA")
        #expect(back.rdpAdminSession)
        #expect(!back.rdpRedirectClipboard)
        #expect(back.rdpGatewayHostname == "gw.example.com")
        #expect(back.rdpGatewayUsageMethod == 2)
    }

    @Test func plikZPortaluFirmy() {
        let s = RdpFile.parse("screen mode id:i:2\r\nfull address:s:[::1]:4000\r\nusername:s:jan@firma.pl\r\nprompt for credentials:i:1\r\n")
        #expect(s.host == "::1")
        #expect(s.port == 4000)
        #expect(s.username == "jan")
        #expect(s.domain == "firma.pl")
        #expect(s.proto == .rdp)
    }

    @Test func polaRdpZapisujaSieJakWWindows() throws {
        var s = Server(host: "h", proto: .rdp)
        s.rdpAudioMode = 7          // przycięte do 0…2
        s.rdpRedirectDrives = true
        let json = String(decoding: try ServerStore.encode([s]), as: UTF8.self)
        #expect(json.contains("\"AudioMode\" : 2"))
        #expect(json.contains("\"RedirectDrives\" : true"))
    }

    @Test func nazwaPliku() {
        #expect(RdpFile.fileName(for: Server(name: "Biuro / pulpit: główny", host: "h")) == "Biuro _ pulpit_ główny.rdp")
        #expect(RdpFile.fileName(for: Server(name: "", host: "")) == "Waypoint.rdp")
    }
}
