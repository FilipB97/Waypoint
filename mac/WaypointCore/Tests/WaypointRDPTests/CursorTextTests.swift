import Testing
import RdpShim

/// wp_cursor_is_text: 1, gdy prostokąt otaczający piksele "negatyw tła" jest co najmniej 2× wyższy niż szeroki
/// (bboxWidth * 2 <= bboxHeight); brak takich pikseli lub inne proporcje → 0.
@Suite struct CursorTextTests {
    /// Pusta maska w × h (same zera).
    func emptyMask(_ w: Int, _ h: Int) -> [UInt8] {
        [UInt8](repeating: 0, count: w * h)
    }

    /// Ustawia piksel (x, y) jako odwrócony.
    func set(_ mask: inout [UInt8], _ w: Int, _ x: Int, _ y: Int, _ value: UInt8 = 1) {
        mask[y * w + x] = value
    }

    /// Wypełnia prostokąt [x0, x0+rw) × [y0, y0+rh).
    func fillRect(_ mask: inout [UInt8], _ w: Int, _ x0: Int, _ y0: Int, _ rw: Int, _ rh: Int) {
        for y in y0..<(y0 + rh) {
            for x in x0..<(x0 + rw) {
                set(&mask, w, x, y)
            }
        }
    }

    /// Wywołuje funkcję C.
    func isText(_ mask: [UInt8], _ w: Int, _ h: Int) -> Int32 {
        precondition(mask.count == w * h)
        return mask.withUnsafeBufferPointer { p in
            wp_cursor_is_text(p.baseAddress, Int32(w), Int32(h))
        }
    }

    /// Model ze specyfikacji, niezależny od implementacji.
    func expected(_ mask: [UInt8], _ w: Int, _ h: Int) -> Int32 {
        var minX = w
        var maxX = -1
        var minY = h
        var maxY = -1
        for y in 0..<h {
            for x in 0..<w where mask[y * w + x] != 0 {
                minX = min(minX, x)
                maxX = max(maxX, x)
                minY = min(minY, y)
                maxY = max(maxY, y)
            }
        }
        if maxX < 0 { return 0 }
        let bw = maxX - minX + 1
        let bh = maxY - minY + 1
        return bw * 2 <= bh ? 1 : 0
    }

    // MARK: brak pikseli

    @Test func R1_empty_mask_is_not_text() {
        #expect(isText(emptyMask(32, 32), 32, 32) == 0)
    }

    @Test func R1_empty_single_pixel_mask_is_not_text() {
        #expect(isText(emptyMask(1, 1), 1, 1) == 0)
    }

    // MARK: małe prostokąty i granica proporcji

    @Test func R2_single_pixel_is_not_text() {
        // bbox 1×1: 1*2 <= 1 nie zachodzi.
        var m = emptyMask(8, 8)
        set(&m, 8, 3, 4)
        #expect(isText(m, 8, 8) == 0)
    }

    @Test func R2_vertical_column_height_one_is_not_text() {
        var m = emptyMask(4, 4)
        fillRect(&m, 4, 2, 1, 1, 1)
        #expect(isText(m, 4, 4) == 0)
    }

    @Test func R2_vertical_column_height_two_is_text() {
        // bbox 1×2: 1*2 <= 2.
        var m = emptyMask(4, 4)
        fillRect(&m, 4, 1, 1, 1, 2)
        #expect(isText(m, 4, 4) == 1)
    }

    @Test func R2_long_one_pixel_column_is_text() {
        var m = emptyMask(32, 32)
        fillRect(&m, 32, 15, 4, 1, 20)
        #expect(isText(m, 32, 32) == 1)
    }

    @Test func R2_boundary_width4_height8_is_text() {
        var m = emptyMask(16, 16)
        fillRect(&m, 16, 5, 3, 4, 8)
        #expect(isText(m, 16, 16) == 1)
    }

    @Test func R2_boundary_width4_height7_is_not_text() {
        var m = emptyMask(16, 16)
        fillRect(&m, 16, 5, 3, 4, 7)
        #expect(isText(m, 16, 16) == 0)
    }

    @Test func R2_boundary_width3_height6_and_5() {
        var a = emptyMask(10, 10)
        fillRect(&a, 10, 0, 0, 3, 6)
        #expect(isText(a, 10, 10) == 1)

        var b = emptyMask(10, 10)
        fillRect(&b, 10, 0, 0, 3, 5)
        #expect(isText(b, 10, 10) == 0)
    }

    // MARK: realne kształty kursorów

    @Test func R3_ibeam_with_serifs_7x16_is_text() {
        // I-beam Windows: pozioma kreska 7 px u góry i u dołu, pionowa kolumna 1 px pośrodku.
        let w = 32, h = 32
        var m = emptyMask(w, h)
        let x0 = 12, y0 = 8
        fillRect(&m, w, x0, y0, 7, 1)          // górny szeryf
        fillRect(&m, w, x0, y0 + 15, 7, 1)     // dolny szeryf
        fillRect(&m, w, x0 + 3, y0, 1, 16)     // trzon
        #expect(isText(m, w, h) == 1)
        #expect(expected(m, w, h) == 1)
    }

    @Test func R3_ibeam_with_two_pixel_serifs_is_text() {
        // Wariant z szeryfami z dwóch pikseli po bokach trzonu (szerokość bbox 5, wysokość 16).
        let w = 32, h = 32
        var m = emptyMask(w, h)
        fillRect(&m, w, 14, 8, 1, 16)
        fillRect(&m, w, 12, 8, 2, 1)
        fillRect(&m, w, 15, 8, 2, 1)
        fillRect(&m, w, 12, 23, 2, 1)
        fillRect(&m, w, 15, 23, 2, 1)
        #expect(isText(m, w, h) == 1)
    }

    @Test func R3_crosshair_15x15_is_not_text() {
        // Krzyżyk precyzyjnego zaznaczania: bbox kwadratowy.
        let w = 32, h = 32
        var m = emptyMask(w, h)
        fillRect(&m, w, 8, 15, 15, 1)    // poziome ramię
        fillRect(&m, w, 15, 8, 1, 15)    // pionowe ramię
        #expect(isText(m, w, h) == 0)
    }

    @Test func R3_horizontal_bar_is_not_text() {
        var m = emptyMask(32, 32)
        fillRect(&m, 32, 4, 10, 20, 1)
        #expect(isText(m, 32, 32) == 0)
    }

    @Test func R3_thick_horizontal_bar_is_not_text() {
        var m = emptyMask(32, 32)
        fillRect(&m, 32, 4, 10, 20, 3)
        #expect(isText(m, 32, 32) == 0)
    }

    @Test func R3_filled_square_is_not_text() {
        var m = emptyMask(32, 32)
        fillRect(&m, 32, 0, 0, 32, 32)
        #expect(isText(m, 32, 32) == 0)
    }

    // MARK: bbox decyduje, nie liczba pikseli

    @Test func R4_two_pixels_far_apart_vertically_are_text() {
        // Dwa pojedyncze piksele w tej samej kolumnie, daleko od siebie: bbox 1×21.
        var m = emptyMask(32, 32)
        set(&m, 32, 10, 5)
        set(&m, 32, 10, 25)
        #expect(isText(m, 32, 32) == 1)
    }

    @Test func R4_two_pixels_diagonal_tall_bbox_is_text() {
        // Różne kolumny: bbox 3×10.
        var m = emptyMask(16, 16)
        set(&m, 16, 4, 2)
        set(&m, 16, 6, 11)
        #expect(isText(m, 16, 16) == 1)
    }

    @Test func R4_two_pixels_far_apart_horizontally_are_not_text() {
        var m = emptyMask(32, 32)
        set(&m, 32, 2, 10)
        set(&m, 32, 29, 10)
        #expect(isText(m, 32, 32) == 0)
    }

    @Test func R4_stray_pixel_widens_bbox_and_cancels_text() {
        // Kolumna 1×16, ale jeden odległy piksel w bok poszerza bbox do 9×16 → już nie tekst.
        var m = emptyMask(32, 32)
        fillRect(&m, 32, 10, 8, 1, 16)
        set(&m, 32, 18, 12)
        #expect(isText(m, 32, 32) == 0)
    }

    @Test func R4_any_nonzero_value_counts_as_inverted() {
        var m = emptyMask(4, 8)
        set(&m, 4, 1, 0, 0xFF)
        set(&m, 4, 1, 7, 7)
        #expect(isText(m, 4, 8) == 1)
    }

    // MARK: krawędzie i rogi maski

    @Test func R5_full_height_column_at_left_edge_is_text() {
        var m = emptyMask(8, 16)
        fillRect(&m, 8, 0, 0, 1, 16)
        #expect(isText(m, 8, 16) == 1)
    }

    @Test func R5_full_height_column_at_right_edge_is_text() {
        var m = emptyMask(8, 16)
        fillRect(&m, 8, 7, 0, 1, 16)
        #expect(isText(m, 8, 16) == 1)
    }

    @Test func R5_top_left_and_bottom_left_corners_are_text() {
        // bbox 1×16, piksele w (0,0) i (0,15).
        var m = emptyMask(8, 16)
        set(&m, 8, 0, 0)
        set(&m, 8, 0, 15)
        #expect(isText(m, 8, 16) == 1)
    }

    @Test func R5_top_right_and_bottom_right_corners_are_text() {
        var m = emptyMask(8, 16)
        set(&m, 8, 7, 0)
        set(&m, 8, 7, 15)
        #expect(isText(m, 8, 16) == 1)
    }

    @Test func R5_opposite_corners_of_square_mask_are_not_text() {
        // (0,0) i (w-1,h-1) w kwadratowej masce: bbox cała maska.
        var m = emptyMask(16, 16)
        set(&m, 16, 0, 0)
        set(&m, 16, 15, 15)
        #expect(isText(m, 16, 16) == 0)
    }

    @Test func R5_opposite_corners_of_tall_mask_are_text() {
        // Maska 4×8: rogi (0,0) i (3,7) dają bbox 4×8 → granica, tekst.
        var m = emptyMask(4, 8)
        set(&m, 4, 0, 0)
        set(&m, 4, 3, 7)
        #expect(isText(m, 4, 8) == 1)
    }

    @Test func R5_last_pixel_of_buffer_is_seen() {
        // Kolumna kończy się w ostatnim bajcie bufora; bez niego bbox byłby 1×1.
        var m = emptyMask(5, 5)
        set(&m, 5, 4, 3)
        set(&m, 5, 4, 4)
        #expect(isText(m, 5, 5) == 1)
    }

    @Test func R5_rows_do_not_wrap() {
        // Piksele (w-1, 0) i (0, 1) leżą obok siebie w pamięci, ale bbox to 4×2 → nie tekst.
        var m = emptyMask(4, 8)
        set(&m, 4, 3, 0)
        set(&m, 4, 0, 1)
        #expect(isText(m, 4, 8) == 0)
    }

    @Test func R5_width_one_mask() {
        #expect(isText([1], 1, 1) == 0)
        #expect(isText([1, 1], 1, 2) == 1)
        #expect(isText([1, 0, 1], 1, 3) == 1)
    }

    @Test func R5_height_one_mask() {
        #expect(isText([1, 1, 1], 3, 1) == 0)
        #expect(isText([0, 1, 0], 3, 1) == 0)
    }

    // MARK: duża maska i model

    @Test func R6_large_256_mask_column_is_text_and_rerun_is_stable() {
        // 256×256 (maksymalny kursor RDP), wynik nie zależy od powtórzenia ani nie modyfikuje maski.
        let w = 256, h = 256
        var m = emptyMask(w, h)
        fillRect(&m, w, 128, 0, 1, h)
        let copy = m
        #expect(isText(m, w, h) == 1)
        #expect(isText(m, w, h) == 1)
        #expect(m == copy)
    }

    @Test func R6_random_masks_match_spec_model() {
        var seed: UInt32 = 4242
        func next() -> Int {
            seed = seed &* 1_103_515_245 &+ 12345
            return Int((seed >> 16) & 0x7FFF)
        }
        for _ in 0..<200 {
            let w = next() % 32 + 1
            let h = next() % 32 + 1
            var m = emptyMask(w, h)
            let count = next() % 4
            for _ in 0..<count {
                set(&m, w, next() % w, next() % h, UInt8(next() % 255 + 1))
            }
            #expect(isText(m, w, h) == expected(m, w, h), "w=\(w) h=\(h)")
        }
    }
}
