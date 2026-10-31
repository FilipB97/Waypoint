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

@Suite struct RdpDisplayTests {
    @Test func dopasowanieDoOknaRetina() {
        let m = RdpDisplay.mode(points: (1001, 700), backing: 2, resolution: RdpDisplay.fit, scale: 0, retina: true)
        #expect(m == RdpDisplay.Mode(width: 2002, height: 1400, scalePercent: 200))
        let bez = RdpDisplay.mode(points: (1001, 700), backing: 2, resolution: RdpDisplay.fit, scale: 0, retina: false)
        #expect(bez == RdpDisplay.Mode(width: 1000, height: 700, scalePercent: 100))   // szerokość parzysta
        let skala = RdpDisplay.mode(points: (1000, 700), backing: 2, resolution: RdpDisplay.fit, scale: 150, retina: true)
        #expect(skala.scalePercent == 150)
    }

    @Test func stalaRozdzielczoscIZaMaleOkno() {
        let m = RdpDisplay.mode(points: (500, 400), backing: 2, resolution: "1920x1080", scale: 0, retina: true)
        #expect(m == RdpDisplay.Mode(width: 1920, height: 1080, scalePercent: 100))
        let maly = RdpDisplay.mode(points: (50, 50), backing: 1, resolution: RdpDisplay.fit, scale: 0, retina: true)
        #expect(maly == RdpDisplay.Mode(width: 1280, height: 800, scalePercent: 100))
        #expect(RdpDisplay.parse("abc") == nil)
        #expect(RdpDisplay.parse("fit") == nil)
    }

    @Test func ustawieniaINadpisanieSerwera() throws {
        var s = MacSettings()
        s.rdpResolution = "1600x900"; s.rdpScale = 125; s.rdpRetina = false
        let back = try JSONDecoder().decode(MacSettings.self, from: JSONEncoder().encode(s))
        #expect(back.rdpResolution == "1600x900" && back.rdpScale == 125 && back.rdpRetina == false)
        let zle = try JSONDecoder().decode(MacSettings.self, from: Data(#"{"MacRdpResolution":"x","MacRdpScale":7}"#.utf8))
        #expect(zle.rdpResolution == RdpDisplay.fit && zle.rdpScale == 0 && zle.rdpRetina)
        var srv = Server(name: "a", host: "h", proto: .rdp)
        srv.macRdpResolution = "1920x1080"; srv.macRdpScale = 150
        #expect(srv.extra["MacRdpResolution"] == .string("1920x1080"))
        srv.macRdpResolution = ""; srv.macRdpScale = 0
        #expect(srv.extra["MacRdpResolution"] == nil && srv.extra["MacRdpScale"] == nil)
    }
}
