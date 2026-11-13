import Testing
import RdpShim

/// wp_cursor_outline_inverted: piksele "negatyw tła" stają się czarne, przezroczyści sąsiedzi (8 kierunków) — biali.
@Suite struct CursorOutlineTests {
    typealias Pixel = [UInt8]   // B, G, R, A
    static let black: Pixel = [0, 0, 0, 255]
    static let white: Pixel = [255, 255, 255, 255]
    static let clear: Pixel = [0, 0, 0, 0]

    /// Uruchamia funkcję C na kopii obrazu i zwraca wynik jako tablicę pikseli.
    func outline(_ pixels: [Pixel], _ inverted: [UInt8], _ w: Int, _ h: Int) -> [Pixel] {
        precondition(pixels.count == w * h && inverted.count == w * h)
        var bgra = pixels.flatMap { $0 }
        bgra.withUnsafeMutableBufferPointer { b in
            inverted.withUnsafeBufferPointer { inv in
                wp_cursor_outline_inverted(b.baseAddress, inv.baseAddress, Int32(w), Int32(h))
            }
        }
        return stride(from: 0, to: bgra.count, by: 4).map { Array(bgra[$0..<$0 + 4]) }
    }

    /// Model ze specyfikacji, niezależny od implementacji.
    func expected(_ pixels: [Pixel], _ inverted: [UInt8], _ w: Int, _ h: Int) -> [Pixel] {
        var out = pixels
        for i in 0..<(w * h) where inverted[i] != 0 { out[i] = Self.black }
        var result = out
        for y in 0..<h {
            for x in 0..<w {
                let i = y * w + x
                guard out[i][3] == 0 else { continue }
                var near = false
                for dy in -1...1 {
                    for dx in -1...1 where !(dx == 0 && dy == 0) {
                        let nx = x + dx, ny = y + dy
                        if nx >= 0, nx < w, ny >= 0, ny < h, inverted[ny * w + nx] != 0 { near = true }
                    }
                }
                if near { result[i] = Self.white }
            }
        }
        return result
    }

    @Test func R1_inverted_pixel_becomes_opaque_black() {
        // Kolor i alfa piksela odwróconego bez znaczenia — zawsze czarny, nieprzezroczysty.
        let out = outline([[10, 20, 30, 0]], [1], 1, 1)
        #expect(out == [Self.black])
        let out2 = outline([[200, 100, 50, 255]], [0xFF], 1, 1)
        #expect(out2 == [Self.black])
    }

    @Test func R1_any_nonzero_marks_inverted() {
        let out = outline([Self.clear, Self.clear, Self.clear], [0, 7, 0], 3, 1)
        #expect(out == [Self.white, Self.black, Self.white])
    }

    @Test func R2_transparent_neighbours_in_all_8_directions_become_white() {
        let pixels = Array(repeating: Self.clear, count: 9)
        var inv = [UInt8](repeating: 0, count: 9); inv[4] = 1
        let out = outline(pixels, inv, 3, 3)
        var want = Array(repeating: Self.white, count: 9); want[4] = Self.black
        #expect(out == want)
    }

    @Test func R2_transparent_with_nonzero_rgb_also_becomes_white() {
        // A == 0 decyduje, niezależnie od RGB.
        let out = outline([[9, 8, 7, 0], Self.clear], [0, 1], 2, 1)
        #expect(out == [Self.white, Self.black])
    }

    @Test func R3_opaque_non_inverted_neighbour_keeps_colour() {
        let arrow: Pixel = [12, 34, 56, 255]
        let out = outline([arrow, Self.clear, Self.clear], [0, 1, 0], 3, 1)
        #expect(out == [arrow, Self.black, Self.white])
    }

    @Test func R3_semi_transparent_neighbour_keeps_colour() {
        // Tylko w pełni przezroczyste (A == 0) dostają obwódkę.
        let shadow: Pixel = [0, 0, 0, 1]
        let half: Pixel = [40, 50, 60, 128]
        let out = outline([shadow, Self.clear, half], [0, 1, 0], 3, 1)
        #expect(out == [shadow, Self.black, half])
    }

    @Test func R4_transparent_pixels_not_adjacent_stay_unchanged() {
        let far: Pixel = [1, 2, 3, 0]
        let pixels = [Self.clear, Self.clear, far, far]
        let out = outline(pixels, [1, 0, 0, 0], 4, 1)
        #expect(out == [Self.black, Self.white, far, far])
    }

    @Test func R5_all_zero_inverted_changes_nothing() {
        let w = 5, h = 4
        let pixels: [Pixel] = (0..<(w * h)).map { i in
            [UInt8(i), UInt8(i * 3 & 0xFF), UInt8(255 - i), i % 3 == 0 ? 0 : UInt8(i * 11 & 0xFF)]
        }
        let out = outline(pixels, [UInt8](repeating: 0, count: w * h), w, h)
        #expect(out == pixels)
    }

    @Test func R6_single_pixel_image() {
        #expect(outline([Self.clear], [0], 1, 1) == [Self.clear])
        #expect(outline([Self.clear], [1], 1, 1) == [Self.black])
    }

    @Test func R6_width_one_column() {
        let out = outline(Array(repeating: Self.clear, count: 5), [0, 0, 1, 0, 0], 1, 5)
        #expect(out == [Self.clear, Self.white, Self.black, Self.white, Self.clear])
    }

    @Test func R6_height_one_row_edges() {
        // Odwrócone piksele na samych krańcach wiersza — bez wyjścia poza bufor.
        let out = outline(Array(repeating: Self.clear, count: 5), [1, 0, 0, 0, 1], 5, 1)
        #expect(out == [Self.black, Self.white, Self.clear, Self.white, Self.black])
    }

    @Test func R6_corners_do_not_wrap_rows() {
        // Odwrócony piksel w prawym górnym rogu nie może "przeciec" na lewą krawędź sąsiedniego wiersza.
        let w = 4, h = 4
        var inv = [UInt8](repeating: 0, count: w * h)
        inv[w - 1] = 1                      // (3,0)
        inv[(h - 1) * w] = 1                // (0,3)
        let pixels = Array(repeating: Self.clear, count: w * h)
        let out = outline(pixels, inv, w, h)
        #expect(out == expected(pixels, inv, w, h))
        #expect(out[w] == Self.clear)           // (0,1) - indeks (3,0)+1, sąsiad tylko przy zawijaniu
        #expect(out[3 * w - 1] == Self.clear)   // (3,2) - indeks (0,3)-1, sąsiad tylko przy zawijaniu
        #expect(out[2 * w - 1] == Self.white)   // (3,1)
        #expect(out[2 * w + 1] == Self.white)   // (1,2)
    }

    @Test func R7_ibeam_column_gets_black_bar_with_white_halo() {
        // Typowy kursor tekstowy Windows: pionowa kolumna 1 px "negatyw tła".
        let w = 5, h = 7
        var inv = [UInt8](repeating: 0, count: w * h)
        for y in 1...5 { inv[y * w + 2] = 1 }
        let out = outline(Array(repeating: Self.clear, count: w * h), inv, w, h)
        for y in 0..<h {
            for x in 0..<w {
                let p = out[y * w + x]
                if x == 2 && (1...5).contains(y) {
                    #expect(p == Self.black, "(\(x),\(y))")
                } else if (1...3).contains(x) {
                    #expect(p == Self.white, "(\(x),\(y))")   // boki, góra, dół i ukosy na końcach
                } else {
                    #expect(p == Self.clear, "(\(x),\(y))")
                }
            }
        }
    }

    @Test func R8_mixed_cursor_matches_spec_model() {
        // Mieszany obraz: nieprzezroczysta strzałka, półprzezroczysty cień, odwrócone piksele w różnych miejscach.
        let w = 16, h = 12
        var seed: UInt32 = 12345
        func next() -> UInt32 { seed = seed &* 1_103_515_245 &+ 12345; return (seed >> 16) & 0x7FFF }
        var pixels: [Pixel] = []
        var inv: [UInt8] = []
        for _ in 0..<(w * h) {
            let kind = next() % 4
            let a: UInt8 = kind == 0 ? 255 : kind == 1 ? UInt8(next() % 255 + 1) : 0
            pixels.append([UInt8(next() & 0xFF), UInt8(next() & 0xFF), UInt8(next() & 0xFF), a])
            inv.append(next() % 7 == 0 ? UInt8(next() % 255 + 1) : 0)
        }
        #expect(outline(pixels, inv, w, h) == expected(pixels, inv, w, h))
    }

    @Test func R9_large_cursor_rerun_is_stable() {
        // 256×256 (maksymalny kursor RDP): drugi przebieg nic już nie zmienia (wszystko wokół jest już nieprzezroczyste).
        let w = 256, h = 256
        var inv = [UInt8](repeating: 0, count: w * h)
        for y in 0..<h { inv[y * w + 128] = 1 }
        for x in 0..<w { inv[x] = 1 }
        let pixels = Array(repeating: Self.clear, count: w * h)
        let once = outline(pixels, inv, w, h)
        #expect(once == expected(pixels, inv, w, h))
        #expect(outline(once, inv, w, h) == once)
    }
}
