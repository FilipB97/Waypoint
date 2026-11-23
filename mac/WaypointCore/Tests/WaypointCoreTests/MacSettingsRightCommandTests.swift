import Foundation
import Testing
@testable import WaypointCore

/// Prawy ⌘ jako AltGr w RDP: ustawienie domyślnie włączone, klucz JSON "MacRdpRightCommandAltGr",
/// brak klucza w starym pliku ustawień → włączone.
@Suite struct RightCommandAltGrSettingTests {
    @Test func R2_default_is_true() {
        #expect(MacSettings().rdpRightCommandAltGr)
    }

    @Test func R2_encoding_writes_json_key() throws {
        var s = MacSettings(); s.rdpRightCommandAltGr = false
        let json = String(decoding: try JSONEncoder().encode(s), as: UTF8.self)
        #expect(json.contains("\"MacRdpRightCommandAltGr\":false"))
        let domyslne = String(decoding: try JSONEncoder().encode(MacSettings()), as: UTF8.self)
        #expect(domyslne.contains("\"MacRdpRightCommandAltGr\":true"))
    }

    @Test func R2_missing_key_decodes_as_true() throws {
        let stare = try JSONDecoder().decode(MacSettings.self, from: Data("{}".utf8))
        #expect(stare.rdpRightCommandAltGr)
        let inne = try JSONDecoder().decode(MacSettings.self, from: Data(#"{"MacRdpOptionAsAlt":false}"#.utf8))
        #expect(inne.rdpRightCommandAltGr)
    }

    @Test func R2_decoding_false_gives_false() throws {
        let s = try JSONDecoder().decode(MacSettings.self, from: Data(#"{"MacRdpRightCommandAltGr":false}"#.utf8))
        #expect(s.rdpRightCommandAltGr == false)
    }

    @Test func R2_round_trip_preserves_false() throws {
        var s = MacSettings(); s.rdpRightCommandAltGr = false
        let back = try JSONDecoder().decode(MacSettings.self, from: JSONEncoder().encode(s))
        #expect(back.rdpRightCommandAltGr == false)
        #expect(back.rdpOptionAsAlt)
    }

    @Test func R2_independent_of_option_as_alt() throws {
        var s = MacSettings(); s.rdpOptionAsAlt = false
        let back = try JSONDecoder().decode(MacSettings.self, from: JSONEncoder().encode(s))
        #expect(back.rdpOptionAsAlt == false && back.rdpRightCommandAltGr)
    }
}
