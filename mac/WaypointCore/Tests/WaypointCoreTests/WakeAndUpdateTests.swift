import Foundation
import Testing
@testable import WaypointCore

@Suite struct WakeOnLanTests {
    @Test func formatyMac() {
        let mac: [UInt8] = [0xAA, 0xBB, 0xCC, 0x01, 0x02, 0x03]
        for t in ["AA:BB:CC:01:02:03", "aa-bb-cc-01-02-03", "AABB.CC01.0203", "aabbcc010203", " AA BB CC 01 02 03 "] {
            #expect(WakeOnLan.parseMac(t) == mac, "\(t)")
        }
        #expect(WakeOnLan.parseMac("AA:BB:CC:01:02") == nil)
        #expect(WakeOnLan.parseMac("GG:BB:CC:01:02:03") == nil)
        #expect(WakeOnLan.parseMac("") == nil)
    }

    @Test func pakiet() {
        let p = WakeOnLan.magicPacket([1, 2, 3, 4, 5, 6])
        #expect(p.count == 102 && p.prefix(6).allSatisfy { $0 == 0xFF } && Array(p[96...]) == [1, 2, 3, 4, 5, 6])
    }

    @Test func wysylkaNaPetle() {
        #expect(WakeOnLan.send([1, 2, 3, 4, 5, 6], address: "127.0.0.1") == nil)
    }

    @Test func poleSerwera() {
        var s = Server(host: "h")
        s.macAddress = " AA:BB:CC:01:02:03 "
        #expect(s.extra["MacAddress"] == .string("AA:BB:CC:01:02:03"))
        s.macAddress = ""
        #expect(s.extra["MacAddress"] == nil)
    }
}

@Suite struct UpdateCheckTests {
    let json = #"""
    {"tag_name":"v1.4.0","html_url":"https://github.com/FilipB97/Waypoint/releases/tag/v1.4.0","body":"- nowe",
     "assets":[{"name":"Waypoint-1.4.0-win-x64.exe","browser_download_url":"https://x/exe"},
               {"name":"Waypoint-1.4.0-mac.zip","browser_download_url":"https://x/mac.zip"}]}
    """#

    @Test func wydanie() {
        let r = UpdateCheck.parseRelease(Data(json.utf8))!
        #expect(r.version == [1, 4, 0] && r.versionText == "1.4.0" && r.macZipURL?.absoluteString == "https://x/mac.zip")
        #expect(UpdateCheck.update(from: r, current: "1.3.9") != nil)
        #expect(UpdateCheck.update(from: r, current: "1.4.0") == nil)
        #expect(UpdateCheck.update(from: r, current: "2.0") == nil)
        var noMac = r; noMac.macZipURL = nil
        #expect(UpdateCheck.update(from: noMac, current: "0.1.0") == nil)   // wydanie tylko dla Windows
        #expect(UpdateCheck.parseRelease(Data("{}".utf8)) == nil)
        #expect(UpdateCheck.parseRelease(Data("nie json".utf8)) == nil)
    }

    @Test func wersje() {
        #expect(UpdateCheck.parseVersion("v1.2") == [1, 2])
        #expect(UpdateCheck.parseVersion("1.2.x") == nil)
        #expect(UpdateCheck.isNewer([1, 2, 1], than: [1, 2]))
        #expect(!UpdateCheck.isNewer([1, 2], than: [1, 2, 0]))
        #expect(UpdateCheck.isNewer([1, 10], than: [1, 9, 9]))
        #expect(!UpdateCheck.isNewer([1, 0], than: [1, 1]))
    }
}

@Suite struct ThemeTests {
    @Test func presety() {
        #expect(ThemePreset.list(light: false).count == 6 && ThemePreset.list(light: true).count == 6)
        #expect(ThemePreset.find("TokyoNight", light: false).canvas == "#1A1B26")
        #expect(ThemePreset.find("TokyoNight", light: true).id == "Waypoint")   // brak jasnego wariantu → baza
        let t = ThemePreset.find("Nord", light: false).terminal()
        #expect(t.background == RGB(hex: "#2E3440") && t.cursor == RGB(hex: "#88C0D0") && t.selectionAlpha == 0.34)
        #expect(ThemePreset.find("Nord", light: false).terminal(accentOverride: "#FF0000").cursor == RGB(hex: "#FF0000"))
        #expect(RGB(hex: "#80FF0000") == RGB(hex: "#FF0000"))
        #expect(RGB(hex: "zly") == nil)
    }

    @Test func ustawieniaWygladu() throws {
        let d = try JSONDecoder().decode(MacSettings.self, from: Data(#"{"Theme":"Light","ThemeVariantLight":"Solarized","AccentColor":"nie-kolor"}"#.utf8))
        #expect(d.theme == "Light" && d.themeVariantLight == "Solarized" && d.themeVariantDark == "Waypoint" && d.accentColor.isEmpty)
        #expect(try JSONDecoder().decode(MacSettings.self, from: Data(#"{"Theme":"Rainbow"}"#.utf8)).theme == "System")
    }
}
