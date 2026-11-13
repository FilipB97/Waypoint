import Testing
import RdpShim

/// wp_cursor_is_arrow: 1, gdy (a) punkt aktywny leży w lewym górnym rogu prostokąta otaczającego kształt
/// (piksele z alfą >= 0x80; hot_x - minX i hot_y - minY w [-1, 2]) oraz (b) bboxWidth * 4 <= bboxHeight * 3.
/// Brak pikseli kształtu albo niespełniony warunek → 0.
@Suite struct CursorArrowTests {
    /// Obraz BGRA w × h, początkowo całkowicie przezroczysty.
    struct Image {
        let w: Int
        let h: Int
        var bgra: [UInt8]

        init(_ w: Int, _ h: Int) {
            self.w = w
            self.h = h
            bgra = [UInt8](repeating: 0, count: w * h * 4)
        }

        /// Ustawia piksel (x, y); kolor nie ma znaczenia dla funkcji, alfa — tak.
        mutating func set(_ x: Int, _ y: Int, alpha: UInt8 = 0xFF, color: UInt8 = 0) {
            let i = (y * w + x) * 4
            bgra[i] = color
            bgra[i + 1] = color
            bgra[i + 2] = color
            bgra[i + 3] = alpha
        }

        /// Wypełnia prostokąt [x0, x0+rw) × [y0, y0+rh).
        mutating func fillRect(_ x0: Int, _ y0: Int, _ rw: Int, _ rh: Int, alpha: UInt8 = 0xFF, color: UInt8 = 0) {
            for y in y0..<(y0 + rh) {
                for x in x0..<(x0 + rw) {
                    set(x, y, alpha: alpha, color: color)
                }
            }
        }

        /// Strzałka Windows z czubkiem w (x0, y0): trójkąt 11 px szeroki, potem ogonek; bbox 11 × 19.
        mutating func drawArrow(_ x0: Int, _ y0: Int, alpha: UInt8 = 0xFF) {
            for r in 0..<16 {
                // Czarny kontur na brzegach, biały środek — jak prawdziwa strzałka.
                let rowWidth = min(r + 1, 11)
                for c in 0..<rowWidth {
                    let edge = c == 0 || c == rowWidth - 1
                    set(x0 + c, y0 + r, alpha: alpha, color: edge ? 0 : 0xFF)
                }
            }
            fillRect(x0 + 5, y0 + 16, 3, 3, alpha: alpha)
        }

        /// Klepsydra w × h z lewym górnym rogiem w (x0, y0): poziome belki i zwężający się środek.
        mutating func drawHourglass(_ x0: Int, _ y0: Int, _ hw: Int, _ hh: Int) {
            fillRect(x0, y0, hw, 2)
            fillRect(x0, y0 + hh - 2, hw, 2)
            let half = hh / 2
            for r in 2..<(hh - 2) {
                let inset = min(r, hh - 1 - r) * (hw / 2) / half
                fillRect(x0 + inset, y0 + r, max(hw - 2 * inset, 1), 1)
            }
        }

        /// Kółko (spinner) o średnicy d z lewym górnym rogiem prostokąta w (x0, y0).
        mutating func drawRing(_ x0: Int, _ y0: Int, _ d: Int) {
            let c = Double(d - 1) / 2
            for y in 0..<d {
                for x in 0..<d {
                    let dx = Double(x) - c, dy = Double(y) - c
                    let dist = (dx * dx + dy * dy).squareRoot()
                    if dist <= c + 0.5 && dist >= c - 2.5 { set(x0 + x, y0 + y) }
                }
            }
        }
    }

    /// Wywołuje funkcję C.
    func isArrow(_ img: Image, _ hx: Int, _ hy: Int) -> Int32 {
        img.bgra.withUnsafeBufferPointer { p in
            wp_cursor_is_arrow(p.baseAddress, Int32(img.w), Int32(img.h), Int32(hx), Int32(hy))
        }
    }

    /// Model ze specyfikacji, niezależny od implementacji.
    func expected(_ img: Image, _ hx: Int, _ hy: Int) -> Int32 {
        var minX = img.w
        var maxX = -1
        var minY = img.h
        var maxY = -1
        for y in 0..<img.h {
            for x in 0..<img.w where img.bgra[(y * img.w + x) * 4 + 3] >= 0x80 {
                minX = min(minX, x)
                maxX = max(maxX, x)
                minY = min(minY, y)
                maxY = max(maxY, y)
            }
        }
        if maxX < 0 { return 0 }
        let dx = hx - minX
        let dy = hy - minY
        let corner = (-1...2).contains(dx) && (-1...2).contains(dy)
        let bw = maxX - minX + 1
        let bh = maxY - minY + 1
        return corner && bw * 4 <= bh * 3 ? 1 : 0
    }

    // MARK: brak kształtu

    @Test func R0_fully_transparent_image_is_not_arrow() {
        #expect(isArrow(Image(32, 32), 0, 0) == 0)
    }

    @Test func R0_single_transparent_pixel_is_not_arrow() {
        #expect(isArrow(Image(1, 1), 0, 0) == 0)
    }

    @Test func R0_only_semi_transparent_pixels_is_not_arrow() {
        // Sam cień (alfa 0x7F) — strzałkowy kształt, ale poniżej progu: brak kształtu.
        var img = Image(32, 32)
        img.drawArrow(0, 0, alpha: 0x7F)
        #expect(isArrow(img, 0, 0) == 0)
    }

    @Test func R0_opaque_color_with_zero_alpha_is_not_shape() {
        // Bajty B, G, R = 0xFF, alfa (bajt 3) = 0: piksel przezroczysty, kolor nie tworzy kształtu.
        var img = Image(16, 16)
        img.fillRect(0, 0, 3, 8, alpha: 0, color: 0xFF)
        #expect(isArrow(img, 0, 0) == 0)
    }

    // MARK: zwykła strzałka

    @Test func R1_arrow_at_top_left_with_hotspot_0_0_is_arrow() {
        var img = Image(32, 32)
        img.drawArrow(0, 0)
        #expect(isArrow(img, 0, 0) == 1)
        #expect(expected(img, 0, 0) == 1)
    }

    @Test func R1_arrow_with_margin_tip_10_10_is_arrow() {
        // Starsze zestawy kursorów: strzałka z marginesem, punkt aktywny 10,10 w 32×32.
        var img = Image(32, 32)
        img.drawArrow(10, 10)
        #expect(isArrow(img, 10, 10) == 1)
    }

    @Test func R1_arrow_in_non_square_image_is_arrow() {
        // Obraz 24 × 40 — wykrywa pomylenie szerokości z wysokością przy indeksowaniu wierszy.
        var img = Image(24, 40)
        img.drawArrow(13, 17)
        #expect(isArrow(img, 13, 17) == 1)
        #expect(isArrow(img, 17, 13) == 0)
    }

    @Test func R1_pixel_color_does_not_matter() {
        var black = Image(32, 32)
        black.fillRect(4, 4, 6, 10, color: 0)
        var white = Image(32, 32)
        white.fillRect(4, 4, 6, 10, color: 0xFF)
        #expect(isArrow(black, 4, 4) == 1)
        #expect(isArrow(white, 4, 4) == 1)
    }

    // MARK: tolerancja punktu aktywnego (a)

    @Test func R2_hotspot_x_tolerance_boundaries() {
        var img = Image(32, 32)
        img.drawArrow(10, 10)
        #expect(isArrow(img, 8, 10) == 0)    // -2
        #expect(isArrow(img, 9, 10) == 1)    // -1
        #expect(isArrow(img, 10, 10) == 1)   //  0
        #expect(isArrow(img, 11, 10) == 1)   // +1
        #expect(isArrow(img, 12, 10) == 1)   // +2
        #expect(isArrow(img, 13, 10) == 0)   // +3
    }

    @Test func R2_hotspot_y_tolerance_boundaries() {
        var img = Image(32, 32)
        img.drawArrow(10, 10)
        #expect(isArrow(img, 10, 8) == 0)    // -2
        #expect(isArrow(img, 10, 9) == 1)    // -1
        #expect(isArrow(img, 10, 12) == 1)   // +2
        #expect(isArrow(img, 10, 13) == 0)   // +3
    }

    @Test func R2_hotspot_both_axes_at_tolerance_limits() {
        var img = Image(32, 32)
        img.drawArrow(10, 10)
        #expect(isArrow(img, 9, 9) == 1)
        #expect(isArrow(img, 12, 12) == 1)
        #expect(isArrow(img, 9, 12) == 1)
        #expect(isArrow(img, 12, 9) == 1)
        #expect(isArrow(img, 13, 13) == 0)
        #expect(isArrow(img, 8, 8) == 0)
        // Jedna oś w tolerancji nie wystarcza.
        #expect(isArrow(img, 10, 13) == 0)
        #expect(isArrow(img, 13, 10) == 0)
    }

    @Test func R2_shape_offset_by_one_from_hotspot_0_0_is_arrow() {
        // Kształt zaczyna się w 1,1, punkt aktywny 0,0: różnica -1 w obu osiach.
        var img = Image(32, 32)
        img.drawArrow(1, 1)
        #expect(isArrow(img, 0, 0) == 1)
    }

    @Test func R2_hotspot_minus_one_outside_image_is_within_tolerance() {
        // Kształt od 0,0, punkt aktywny -1,-1: różnica -1 — według specyfikacji w tolerancji.
        var img = Image(32, 32)
        img.drawArrow(0, 0)
        #expect(isArrow(img, -1, -1) == 1)
    }

    @Test func R2_far_hotspot_is_not_arrow() {
        var img = Image(32, 32)
        img.drawArrow(0, 0)
        #expect(isArrow(img, 31, 31) == 0)
        #expect(isArrow(img, 5, 9) == 0)
    }

    // MARK: proporcje (b)

    @Test func R3_ratio_3x4_boundary_is_arrow() {
        // 3*4 = 12 <= 4*3 = 12.
        var img = Image(16, 16)
        img.fillRect(2, 2, 3, 4)
        #expect(isArrow(img, 2, 2) == 1)
    }

    @Test func R3_ratio_6x8_boundary_is_arrow_and_6x7_is_not() {
        var a = Image(16, 16)
        a.fillRect(0, 0, 6, 8)
        #expect(isArrow(a, 0, 0) == 1)        // 24 <= 24

        var b = Image(16, 16)
        b.fillRect(0, 0, 6, 7)
        #expect(isArrow(b, 0, 0) == 0)        // 24 <= 21 nie
    }

    @Test func R3_ratio_just_over_boundary_is_not_arrow() {
        // 4 × 5: 16 <= 15 nie zachodzi.
        var img = Image(16, 16)
        img.fillRect(2, 2, 4, 5)
        #expect(isArrow(img, 2, 2) == 0)
    }

    @Test func R3_ratio_9x12_and_10x13() {
        var a = Image(32, 32)
        a.fillRect(5, 5, 9, 12)
        #expect(isArrow(a, 5, 5) == 1)        // 36 <= 36

        var b = Image(32, 32)
        b.fillRect(5, 5, 10, 13)
        #expect(isArrow(b, 5, 5) == 0)        // 40 <= 39 nie
    }

    @Test func R3_square_is_not_arrow() {
        var img = Image(32, 32)
        img.fillRect(4, 4, 12, 12)
        #expect(isArrow(img, 4, 4) == 0)
    }

    @Test func R3_single_pixel_is_not_arrow() {
        // bbox 1×1: 4 <= 3 nie zachodzi.
        var img = Image(8, 8)
        img.set(3, 3)
        #expect(isArrow(img, 3, 3) == 0)
    }

    @Test func R3_one_by_two_column_is_arrow() {
        // bbox 1×2: 4 <= 6.
        var img = Image(8, 8)
        img.fillRect(3, 3, 1, 2)
        #expect(isArrow(img, 3, 3) == 1)
    }

    @Test func R3_wide_bar_with_corner_hotspot_is_not_arrow() {
        var img = Image(32, 32)
        img.fillRect(0, 0, 20, 3)
        #expect(isArrow(img, 0, 0) == 0)
    }

    // MARK: kursory pracy w tle

    @Test func R4_arrow_with_hourglass_beside_is_not_arrow() {
        // Czubek nadal w lewym górnym rogu bbox, ale kształt jest szeroki.
        var img = Image(32, 32)
        img.drawArrow(0, 0)
        img.drawHourglass(12, 8, 12, 16)
        #expect(isArrow(img, 0, 0) == 0)
        #expect(expected(img, 0, 0) == 0)
    }

    @Test func R4_arrow_with_hourglass_beside_with_margin_is_not_arrow() {
        var img = Image(48, 48)
        img.drawArrow(10, 10)
        img.drawHourglass(22, 18, 12, 16)
        #expect(isArrow(img, 10, 10) == 0)
    }

    @Test func R4_arrow_with_spinner_above_right_is_not_arrow() {
        // Kółko nad czubkiem: górna krawędź bbox jest wyżej niż punkt aktywny. Proporcje bbox
        // (14 × 29) same w sobie przechodzą — decyduje warunek (a).
        var img = Image(32, 32)
        img.drawArrow(2, 10)
        img.drawRing(8, 0, 8)
        #expect(expected(img, 2, 10) == 0)
        #expect(isArrow(img, 2, 10) == 0)
    }

    // MARK: inne kształty z punktem aktywnym w środku

    @Test func R5_hourglass_with_centre_hotspot_is_not_arrow() {
        // Klepsydra 11 × 16 jest wyższa niż szersza (proporcje przechodzą), punkt aktywny w środku.
        var img = Image(32, 32)
        img.drawHourglass(10, 8, 11, 16)
        #expect(isArrow(img, 15, 15) == 0)
    }

    @Test func R5_spinner_with_centre_hotspot_is_not_arrow() {
        var img = Image(32, 32)
        img.drawRing(8, 8, 16)
        #expect(isArrow(img, 15, 15) == 0)
    }

    @Test func R5_vertical_resize_arrow_with_centre_hotspot_is_not_arrow() {
        // Pionowa strzałka zmiany rozmiaru 7 × 21: proporcje przechodzą, punkt aktywny w środku.
        var img = Image(32, 32)
        img.fillRect(15, 5, 1, 21)
        for r in 0..<4 {
            img.fillRect(15 - r, 5 + r, 2 * r + 1, 1)
            img.fillRect(15 - r, 25 - r, 2 * r + 1, 1)
        }
        #expect(isArrow(img, 15, 15) == 0)
    }

    @Test func R5_horizontal_resize_arrow_with_centre_hotspot_is_not_arrow() {
        var img = Image(32, 32)
        img.fillRect(5, 15, 21, 1)
        for r in 0..<4 {
            img.fillRect(5 + r, 15 - r, 1, 2 * r + 1)
            img.fillRect(25 - r, 15 - r, 1, 2 * r + 1)
        }
        #expect(isArrow(img, 15, 15) == 0)
    }

    @Test func R5_hand_with_fingertip_hotspot_is_not_arrow() {
        // Dłoń: palec wskazujący na górze, 5 px od lewej krawędzi bbox (dłoń 15 × 22 — proporcje przechodzą).
        var img = Image(32, 32)
        img.fillRect(13, 4, 3, 10)        // palec wskazujący, od y = 4
        img.fillRect(16, 9, 3, 5)         // pozostałe palce
        img.fillRect(19, 10, 3, 4)
        img.fillRect(8, 13, 15, 13)       // dłoń, x 8…22, y 13…25
        #expect(expected(img, 13, 4) == 0)
        #expect(isArrow(img, 13, 4) == 0)
    }

    // MARK: cień i próg alfy

    @Test func R6_soft_shadow_right_and_down_does_not_change_arrow() {
        // Cień alfa 0x40 przesunięty o 2 px i szeroki pas w prawo — gdyby się liczył, bbox byłby szeroki.
        var img = Image(48, 48)
        img.fillRect(12, 12, 13, 21, alpha: 0x40)
        img.fillRect(12, 30, 34, 4, alpha: 0x40)
        img.drawArrow(10, 10)
        #expect(isArrow(img, 10, 10) == 1)
    }

    @Test func R6_soft_shadow_does_not_change_non_arrow() {
        // Kwadrat z cieniem w dół, który (gdyby się liczył) robiłby z niego wysoki kształt.
        var img = Image(48, 48)
        img.fillRect(4, 4, 12, 12)
        img.fillRect(6, 16, 12, 30, alpha: 0x40)
        #expect(isArrow(img, 4, 4) == 0)
    }

    @Test func R6_shadow_above_left_does_not_move_corner() {
        // Półprzezroczyste piksele (0x7F) nad i na lewo od czubka nie przesuwają minX/minY.
        var img = Image(32, 32)
        img.fillRect(0, 0, 10, 10, alpha: 0x7F)
        img.drawArrow(10, 10)
        #expect(isArrow(img, 10, 10) == 1)
    }

    @Test func R6_alpha_0x80_counts_and_0x7F_does_not() {
        // Daleki piksel po prawej poszerza bbox tylko, gdy ma alfę >= 0x80.
        var counted = Image(48, 32)
        counted.drawArrow(0, 0)
        counted.set(40, 5, alpha: 0x80)
        #expect(isArrow(counted, 0, 0) == 0)

        var ignored = Image(48, 32)
        ignored.drawArrow(0, 0)
        ignored.set(40, 5, alpha: 0x7F)
        #expect(isArrow(ignored, 0, 0) == 1)
    }

    @Test func R6_arrow_drawn_with_alpha_0x80_is_arrow() {
        var img = Image(32, 32)
        img.drawArrow(3, 3, alpha: 0x80)
        #expect(isArrow(img, 3, 3) == 1)
    }

    // MARK: krawędzie bufora, duży obraz, model

    @Test func R7_shape_reaching_last_pixel_of_buffer() {
        // Kolumna 2 × 3 w prawym dolnym rogu; bez ostatniego wiersza bbox 2 × 2 nie byłby strzałką.
        var img = Image(8, 8)
        img.fillRect(6, 5, 2, 3)
        #expect(isArrow(img, 6, 5) == 1)
    }

    @Test func R7_rows_do_not_wrap() {
        // (w-1, 0) i (0, 1) sąsiadują w pamięci, ale bbox to całe 8 × 2 — szeroki.
        var img = Image(8, 8)
        img.set(7, 0)
        img.set(0, 1)
        #expect(isArrow(img, 0, 0) == 0)
    }

    @Test func R7_large_256_image_rerun_is_stable_and_input_unchanged() {
        var img = Image(256, 256)
        img.drawArrow(100, 60)
        img.fillRect(110, 70, 120, 120, alpha: 0x40)
        let copy = img.bgra
        #expect(isArrow(img, 100, 60) == 1)
        #expect(isArrow(img, 100, 60) == 1)
        #expect(img.bgra == copy)
    }

    @Test func R7_random_images_match_spec_model() {
        var seed: UInt32 = 2024
        func next() -> Int {
            seed = seed &* 1_103_515_245 &+ 12345
            return Int((seed >> 16) & 0x7FFF)
        }
        let alphas: [UInt8] = [0x00, 0x40, 0x7F, 0x80, 0xC0, 0xFF]
        for _ in 0..<300 {
            let w = next() % 24 + 1
            let h = next() % 24 + 1
            var img = Image(w, h)
            for _ in 0..<(next() % 4) {
                let x0 = next() % w, y0 = next() % h
                let rw = next() % (w - x0) + 1, rh = next() % (h - y0) + 1
                img.fillRect(x0, y0, rw, rh, alpha: alphas[next() % alphas.count])
            }
            let hx = next() % (w + 2) - 1
            let hy = next() % (h + 2) - 1
            #expect(isArrow(img, hx, hy) == expected(img, hx, hy), "w=\(w) h=\(h) hot=\(hx),\(hy)")
        }
    }
}
