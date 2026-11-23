#if canImport(Carbon)
import Testing
import WaypointRDP

/// Prawy ⌘ jako AltGr w RDP: znak, który układ klawiatury Maca daje dla klawisza z Option (i ewentualnie Shift),
/// bez składania martwych klawiszy. Nieznany układ, pusty wynik albo znak sterujący → nil.
@Suite struct OptionCharacterTests {
    let polishPro = "com.apple.keylayout.PolishPro"

    func char(_ keyCode: UInt16, shift: Bool = false) -> String? {
        RdpKeyboard.optionCharacter(macKeyCode: keyCode, shift: shift, inputSourceID: polishPro)
    }

    // MARK: Polskie litery w Polish Pro

    @Test func R1_polish_pro_option_e_gives_e_ogonek() {
        #expect(char(14) == "ę")
    }

    @Test func R1_polish_pro_shift_option_e_gives_capital_e_ogonek() {
        #expect(char(14, shift: true) == "Ę")
    }

    @Test func R1_polish_pro_option_a_gives_a_ogonek() {
        #expect(char(0) == "ą")
    }

    @Test func R1_polish_pro_shift_option_a_gives_capital_a_ogonek() {
        #expect(char(0, shift: true) == "Ą")
    }

    @Test func R1_polish_pro_option_c_gives_c_acute() {
        #expect(char(8) == "ć")
    }

    @Test func R1_polish_pro_option_l_gives_l_stroke() {
        #expect(char(37) == "ł")
    }

    @Test func R1_polish_pro_option_n_gives_n_acute() {
        #expect(char(45) == "ń")
    }

    @Test func R1_polish_pro_option_o_gives_o_acute() {
        #expect(char(31) == "ó")
    }

    @Test func R1_polish_pro_option_s_gives_s_acute() {
        #expect(char(1) == "ś")
    }

    @Test func R1_polish_pro_option_x_gives_z_acute() {
        #expect(char(7) == "ź")
    }

    @Test func R1_polish_pro_option_z_gives_z_dot() {
        #expect(char(6) == "ż")
    }

    @Test func R1_lowercase_and_uppercase_differ_for_every_polish_letter() {
        let keys: [UInt16] = [0, 8, 14, 37, 45, 31, 1, 7, 6]
        for key in keys {
            let lower = char(key)
            let upper = char(key, shift: true)
            #expect(lower != nil && upper != nil)
            #expect(lower?.uppercased() == upper)
        }
    }

    @Test func R1_repeated_calls_give_same_result() {
        #expect(char(14) == char(14))
        #expect(char(14) == "ę")
    }

    // MARK: Klawisze niedrukowalne → nil

    @Test func R1_return_gives_nil() {
        #expect(char(36) == nil)
        #expect(char(36, shift: true) == nil)
    }

    @Test func R1_escape_gives_nil() {
        #expect(char(53) == nil)
    }

    @Test func R1_left_arrow_gives_nil() {
        #expect(char(123) == nil)
        #expect(char(123, shift: true) == nil)
    }

    // MARK: Nieznany układ → nil

    @Test func R1_unknown_input_source_gives_nil() {
        #expect(RdpKeyboard.optionCharacter(macKeyCode: 14, shift: false,
                                            inputSourceID: "com.apple.keylayout.DoesNotExist") == nil)
    }

    @Test func R1_empty_input_source_gives_nil() {
        #expect(RdpKeyboard.optionCharacter(macKeyCode: 14, shift: false, inputSourceID: "") == nil)
    }
}
#endif
