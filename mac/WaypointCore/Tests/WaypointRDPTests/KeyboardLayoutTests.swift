import Testing
import WaypointRDP

/// B1 (docs/specs/mac-roadmap.md): identyfikator źródła wprowadzania macOS (com.apple.keylayout.*) mapowany
/// na KLID Windows przez jawną tabelę po dokładnym identyfikatorze, bez heurystyk na podciągach.
/// Nieznany identyfikator → 0 (serwer użyje swojego domyślnego układu).
@Suite struct KeyboardLayoutTests {
    func layout(_ id: String) -> UInt32 {
        RdpKeyboard.windowsLayout(forMacInputSource: id)
    }

    // MARK: Polski

    @Test func B1_polish_pro_maps_to_polish_programmers() {
        #expect(layout("com.apple.keylayout.PolishPro") == 0x415)
    }

    @Test func B1_polish_maps_to_polish_214() {
        #expect(layout("com.apple.keylayout.Polish") == 0x10415)
    }

    // MARK: Szwajcarskie i niemieckojęzyczne

    @Test func B1_swiss_german_maps_to_swiss_german_not_german() {
        let klid = layout("com.apple.keylayout.SwissGerman")
        #expect(klid == 0x807)
        #expect(klid != 0x407)
    }

    @Test func B1_swiss_french_maps_to_swiss_french() {
        #expect(layout("com.apple.keylayout.SwissFrench") == 0x100C)
    }

    @Test func B1_austrian_is_known() {
        #expect(layout("com.apple.keylayout.Austrian") != 0)
    }

    @Test func B1_german_maps_to_german() {
        #expect(layout("com.apple.keylayout.German") == 0x407)
    }

    // MARK: Tabela pozostałych układów

    static let table: [(String, UInt32)] = [
        ("com.apple.keylayout.PolishPro", 0x415),
        ("com.apple.keylayout.Polish", 0x10415),
        ("com.apple.keylayout.SwissGerman", 0x807),
        ("com.apple.keylayout.SwissFrench", 0x100C),
        ("com.apple.keylayout.German", 0x407),
        ("com.apple.keylayout.French", 0x40C),
        ("com.apple.keylayout.ABC-India", 0x4009),
        ("com.apple.keylayout.British", 0x809),
        ("com.apple.keylayout.British-PC", 0x809),
        ("com.apple.keylayout.US", 0x409),
        ("com.apple.keylayout.ABC", 0x409),
        ("com.apple.keylayout.USInternational-PC", 0x20409),
        ("com.apple.keylayout.Czech", 0x405),
        ("com.apple.keylayout.Slovak", 0x41B),
        ("com.apple.keylayout.Hungarian", 0x40E),
        ("com.apple.keylayout.Italian", 0x410),
        ("com.apple.keylayout.Spanish-ISO", 0x40A),
        ("com.apple.keylayout.Swedish-Pro", 0x41D),
        ("com.apple.keylayout.Norwegian", 0x414),
        ("com.apple.keylayout.Danish", 0x406),
        ("com.apple.keylayout.Finnish", 0x40B),
        ("com.apple.keylayout.Dutch", 0x413),
        ("com.apple.keylayout.Russian", 0x419),
        ("com.apple.keylayout.Ukrainian-PC", 0x422),
    ]

    @Test(arguments: table)
    func B1_known_layout_maps_to_klid(_ id: String, _ expected: UInt32) {
        #expect(layout(id) == expected, "\(id)")
    }

    // MARK: Nieznane identyfikatory

    @Test(arguments: ["com.apple.keylayout.Klingon", "", "com.example.foo"])
    func B1_unknown_id_returns_zero(_ id: String) {
        #expect(layout(id) == 0, "\(id)")
    }

    // MARK: Pułapki podciągów

    @Test func B1_abc_qwertz_is_not_us() {
        #expect(layout("com.apple.keylayout.ABC-QWERTZ") != 0x409)
    }

    @Test func B1_abc_azerty_is_not_us() {
        #expect(layout("com.apple.keylayout.ABC-AZERTY") != 0x409)
    }

    @Test func B1_abc_india_is_not_us() {
        #expect(layout("com.apple.keylayout.ABC-India") != 0x409)
    }

    @Test func B1_polish_and_polish_pro_differ() {
        #expect(layout("com.apple.keylayout.Polish") != layout("com.apple.keylayout.PolishPro"))
    }

    @Test func B1_us_international_is_not_plain_us() {
        #expect(layout("com.apple.keylayout.USInternational-PC") != 0x409)
    }

    @Test func B1_lookup_is_exact_not_prefix_or_case_insensitive() {
        #expect(layout("com.apple.keylayout.polishpro") != 0x415)
        #expect(layout("com.apple.keylayout.PolishPro-Extra") != 0x415)
        #expect(layout("com.apple.keylayout.German-Swiss") != 0x407)
    }
}
