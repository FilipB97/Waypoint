import AppKit
import WaypointCore
import WaypointRDP

/// Pulpit zdalny: obraz z FreeRDP jako zawartość warstwy (CGImage z bufora BGRA) i wejście
/// z klawiatury/myszy przeliczane na współrzędne pulpitu.
///
/// Klawiatura po macowemu:
///  * ⌘ działa jak Ctrl po stronie Windows (⌘C/⌘V kopiują i wklejają w sesji); ⌘W, ⌘Q i ⌘1–9 zostają
///    przy Waypoincie (monitor skrótów w AppModel / menu);
///  * znaki pisane z Option (ą, ę, ł…) idą jako Unicode — działają niezależnie od układu klawiatury
///    na serwerze; Option z klawiszami specjalnymi (Tab, F4, strzałki) działa jak Alt.
final class RdpDisplayView: NSView {
    weak var session: RdpSession?
    var connection: RdpConnection? {
        didSet {
            if connection == nil { releaseAll(); remoteCursor = .standard; builtCursor = nil }
            window?.invalidateCursorRects(for: self)
        }
    }

    private var desktop = CGSize.zero
    private var resizeWork: DispatchWorkItem?
    private var tracking: NSTrackingArea?
    private var pressed = Set<UInt32>()          // kody skanowania wciśnięte po stronie serwera
    private var altSent = false                  // Option wysłany jako Alt (dopiero przy klawiszu specjalnym)
    private var scrollY: CGFloat = 0, scrollX: CGFloat = 0
    private var started = false
    var lastMode: RdpDisplay.Mode?            // ostatnio wysłany tryb (bez zbędnych zmian rozdzielczości)

    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        layer?.backgroundColor = NSColor.black.cgColor
        layer?.contentsGravity = .resizeAspect
        layer?.magnificationFilter = .linear
        layer?.minificationFilter = .linear
        registerForDraggedTypes([.fileURL])
    }

    required init?(coder: NSCoder) { fatalError() }

    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { true }

    // MARK: Rozmiar sesji

    var backing: CGFloat { window?.backingScaleFactor ?? NSScreen.main?.backingScaleFactor ?? 2 }

    /// Rozdzielczość i skala sesji: Ustawienia (⌘,) albo nadpisanie w edycji serwera.
    func displayMode() -> RdpDisplay.Mode {
        let st = AppModel.shared.settings
        let srv = session?.server
        let res = srv.map { $0.macRdpResolution.isEmpty ? st.rdpResolution : $0.macRdpResolution } ?? st.rdpResolution
        let scale = srv.map { $0.macRdpScale == 0 ? st.rdpScale : $0.macRdpScale } ?? st.rdpScale
        return RdpDisplay.mode(points: (Double(bounds.width), Double(bounds.height)), backing: Double(backing),
                               resolution: res, scale: scale, retina: st.rdpRetina)
    }

    /// Nowy tryb do sesji (zmiana okna albo ustawień) — tylko gdy coś się zmieniło.
    func applyDisplayMode() {
        guard let s = session, s.state == .connected else { return }
        let m = displayMode()
        guard m != lastMode else { return }
        lastMode = m
        s.requestResize(width: m.width, height: m.height, scale: m.scalePercent)
    }

    /// Okno przeniesione między ekranem Retina a zwykłym.
    override func viewDidChangeBackingProperties() {
        super.viewDidChangeBackingProperties()
        applyDisplayMode()
    }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        startIfReady()
    }

    override func setFrameSize(_ newSize: NSSize) {
        super.setFrameSize(newSize)
        startIfReady()
        // Nowa rozdzielczość dopiero po ustabilizowaniu okna (przeciąganie krawędzi = wiele zmian).
        resizeWork?.cancel()
        let work = DispatchWorkItem { [weak self] in self?.applyDisplayMode() }
        resizeWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.6, execute: work)
    }

    /// Pierwsze połączenie, gdy widok ma już okno i rozmiar — wtedy sesja ma od razu właściwą rozdzielczość.
    private func startIfReady() {
        guard !started, window != nil, bounds.width > 100, bounds.height > 100, let s = session else { return }
        started = true
        s.connect()
    }

    // MARK: Obraz

    func desktopResized(_ w: Int, _ h: Int) {
        desktop = CGSize(width: w, height: h)
        frameChanged()
    }

    /// Powiadomienia o klatkach z wątku połączenia, scalane w jedno odświeżenie na wątku głównym.
    private(set) lazy var ticker = FrameTicker { [weak self] in self?.render() }

    func frameChanged() { ticker.tick() }

    private func render() {
        guard let c = connection else { return }
        let image: CGImage? = c.withFrame { data, w, h, stride in
            guard let data, w > 0, h > 0 else { return nil }
            let bytes = Data(bytes: data, count: stride * h)
            guard let provider = CGDataProvider(data: bytes as CFData) else { return nil }
            return CGImage(width: w, height: h, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: stride,
                           space: CGColorSpace(name: CGColorSpace.sRGB) ?? CGColorSpaceCreateDeviceRGB(),
                           bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.noneSkipFirst.rawValue
                                                    | CGBitmapInfo.byteOrder32Little.rawValue),
                           provider: provider, decode: nil, shouldInterpolate: true, intent: .defaultIntent)
        }
        guard let image else { return }
        desktop = CGSize(width: image.width, height: image.height)
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        layer?.contents = image
        CATransaction.commit()
    }

    // MARK: Kursor z sesji

    /// Ostatni kursor z serwera (zajęty, ładowanie, tekstowy…) i jego wersja w rozmiarze widoku.
    private var remoteCursor: RdpConnection.Cursor = .standard
    private var builtCursor: (cursor: NSCursor, scale: CGFloat)?
    private static let hiddenCursor = NSCursor(image: NSImage(size: NSSize(width: 1, height: 1)), hotSpot: .zero)

    func setRemoteCursor(_ c: RdpConnection.Cursor) {
        remoteCursor = c
        builtCursor = nil
        window?.invalidateCursorRects(for: self)
        // Zmiana bez ruchu myszą (np. kursor ładowania po kliknięciu) — od razu, gdy mysz jest nad pulpitem.
        if let w = window, bounds.contains(convert(w.mouseLocationOutsideOfEventStream, from: nil)) {
            currentCursor().set()
        }
    }

    /// Kursor w punktach widoku: piksele sesji × skala wyświetlania (proporcje pulpitu zachowane).
    private func currentCursor() -> NSCursor {
        guard desktop.width > 0, bounds.width > 0 else { return .arrow }
        let scale = min(bounds.width / desktop.width, bounds.height / desktop.height)
        if let b = builtCursor, b.scale == scale { return b.cursor }
        let cursor: NSCursor
        switch remoteCursor {
        case .standard: cursor = .arrow
        case .hidden: cursor = Self.hiddenCursor
        case .image(let w, let h, let hx, let hy, let bgra):
            guard let provider = CGDataProvider(data: bgra as CFData),
                  let img = CGImage(width: w, height: h, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: w * 4,
                                    space: CGColorSpace(name: CGColorSpace.sRGB) ?? CGColorSpaceCreateDeviceRGB(),
                                    bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.first.rawValue
                                                             | CGBitmapInfo.byteOrder32Little.rawValue),
                                    provider: provider, decode: nil, shouldInterpolate: true, intent: .defaultIntent)
            else { cursor = .arrow; break }
            let size = NSSize(width: CGFloat(w) * scale, height: CGFloat(h) * scale)
            cursor = NSCursor(image: NSImage(cgImage: img, size: size),
                              hotSpot: NSPoint(x: CGFloat(hx) * scale, y: CGFloat(hy) * scale))
        }
        builtCursor = (cursor, scale)
        return cursor
    }

    override func resetCursorRects() {
        addCursorRect(bounds, cursor: connection == nil ? .arrow : currentCursor())
    }

    // MARK: Przeciąganie plików z Findera

    private func droppedFiles(_ info: NSDraggingInfo) -> [URL] {
        (info.draggingPasteboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL]) ?? []
    }

    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation {
        connection != nil && !droppedFiles(sender).isEmpty ? .copy : []
    }

    override func draggingUpdated(_ sender: NSDraggingInfo) -> NSDragOperation {
        guard connection != nil else { return [] }
        let (x, y) = desktopPoint(window: sender.draggingLocation)
        connection?.mouse(flags: RdpMouse.move, x: x, y: y)   // kursor w sesji pokazuje, gdzie trafią pliki
        return .copy
    }

    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        let urls = droppedFiles(sender)
        guard !urls.isEmpty, let s = session else { return false }
        window?.makeFirstResponder(self)
        s.dropFiles(urls, at: desktopPoint(window: sender.draggingLocation))
        return true
    }

    /// Klik w miejscu upuszczenia (fokus na oknie / folderze pod kursorem) i Ctrl+V.
    func pasteAt(_ point: (Int, Int)) {
        guard let c = connection else { return }
        let (x, y) = point
        c.mouse(flags: RdpMouse.move, x: x, y: y)
        c.mouse(flags: RdpMouse.down | RdpMouse.left, x: x, y: y)
        c.mouse(flags: RdpMouse.left, x: x, y: y)
        c.key(down: true, scancode: 0x1D)    // Ctrl
        c.key(down: true, scancode: 0x2F)    // V
        c.key(down: false, scancode: 0x2F)
        c.key(down: false, scancode: 0x1D)
    }

    // MARK: Mysz

    /// Prostokąt obrazu w widoku (proporcje zachowane, jak .resizeAspect) i przeliczenie punktu.
    private func desktopPoint(_ event: NSEvent) -> (Int, Int) { desktopPoint(window: event.locationInWindow) }

    private func desktopPoint(window location: NSPoint) -> (Int, Int) {
        let p = convert(location, from: nil)
        guard desktop.width > 0, desktop.height > 0, bounds.width > 0, bounds.height > 0 else { return (0, 0) }
        let scale = min(bounds.width / desktop.width, bounds.height / desktop.height)
        let w = desktop.width * scale, h = desktop.height * scale
        let ox = (bounds.width - w) / 2, oy = (bounds.height - h) / 2
        return (Int(((p.x - ox) / scale).rounded(.down)), Int(((p.y - oy) / scale).rounded(.down)))
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let t = tracking { removeTrackingArea(t) }
        let t = NSTrackingArea(rect: .zero, options: [.mouseMoved, .activeInKeyWindow, .inVisibleRect], owner: self)
        addTrackingArea(t)
        tracking = t
    }

    private func send(_ flags: UInt16, _ event: NSEvent) {
        let (x, y) = desktopPoint(event)
        connection?.mouse(flags: flags, x: x, y: y)
    }

    override func mouseMoved(with e: NSEvent) { send(RdpMouse.move, e) }
    override func mouseDragged(with e: NSEvent) { send(RdpMouse.move, e) }
    override func rightMouseDragged(with e: NSEvent) { send(RdpMouse.move, e) }
    override func otherMouseDragged(with e: NSEvent) { send(RdpMouse.move, e) }
    override func mouseDown(with e: NSEvent) {
        window?.makeFirstResponder(self)
        // Ctrl+klik na Macu = prawy przycisk (gdy nie ma drugiego przycisku).
        send(RdpMouse.down | (e.modifierFlags.contains(.control) ? RdpMouse.right : RdpMouse.left), e)
    }
    override func mouseUp(with e: NSEvent) {
        send(e.modifierFlags.contains(.control) ? RdpMouse.right : RdpMouse.left, e)
    }
    override func rightMouseDown(with e: NSEvent) { send(RdpMouse.down | RdpMouse.right, e) }
    override func rightMouseUp(with e: NSEvent) { send(RdpMouse.right, e) }
    override func otherMouseDown(with e: NSEvent) { other(e, down: true) }
    override func otherMouseUp(with e: NSEvent) { other(e, down: false) }

    private func other(_ e: NSEvent, down: Bool) {
        let (x, y) = desktopPoint(e)
        switch e.buttonNumber {
        case 2: connection?.mouse(flags: (down ? RdpMouse.down : 0) | RdpMouse.middle, x: x, y: y)
        case 3: connection?.mouseExtended(flags: (down ? RdpMouse.xDown : 0) | RdpMouse.xButton1, x: x, y: y)
        case 4: connection?.mouseExtended(flags: (down ? RdpMouse.xDown : 0) | RdpMouse.xButton2, x: x, y: y)
        default: break
        }
    }

    override func scrollWheel(with e: NSEvent) {
        // Kółko: 120 jednostek na ząbek; gładzik (precyzyjny) — sumowanie małych przesunięć.
        let factor: CGFloat = e.hasPreciseScrollingDeltas ? 4 : 120
        scrollY += e.scrollingDeltaY * factor
        scrollX += e.scrollingDeltaX * factor
        let (x, y) = desktopPoint(e)
        while abs(scrollY) >= 1 {
            let step = Int(max(-255, min(255, scrollY)))
            if step == 0 { break }
            connection?.mouse(flags: RdpMouse.wheelFlags(delta: step), x: x, y: y)
            scrollY -= CGFloat(step)
        }
        while abs(scrollX) >= 1 {
            let step = Int(max(-255, min(255, scrollX)))
            if step == 0 { break }
            // Poziomo: w RDP dodatnia wartość = w prawo, na Macu odwrotnie.
            connection?.mouse(flags: RdpMouse.wheelFlags(delta: -step, horizontal: true), x: x, y: y)
            scrollX -= CGFloat(step)
        }
    }

    // MARK: Klawiatura

    /// Kod klawisza macOS → kod skanowania; ⌘ jako Ctrl.
    private func scancode(_ keyCode: UInt16) -> UInt32 {
        switch keyCode {
        case 55: return 0x1D      // lewy ⌘ → lewy Ctrl
        case 54: return 0x11D     // prawy ⌘ → prawy Ctrl
        default: return RdpConnection.scancode(macKeyCode: keyCode)
        }
    }

    private func key(_ code: UInt32, down: Bool) {
        guard code != 0, let c = connection else { return }
        if down { pressed.insert(code) } else { pressed.remove(code) }
        c.key(down: down, scancode: code)
    }

    /// Znak pisany z Option (np. ą) — wysyłany jako Unicode.
    private func optionText(_ e: NSEvent) -> String? {
        let mods = e.modifierFlags.intersection([.option, .command, .control])
        guard mods == .option, let chars = e.characters, !chars.isEmpty else { return nil }
        for u in chars.unicodeScalars where u.value < 0x20 || u.value == 0x7F || (0xF700...0xF8FF).contains(u.value) {
            return nil   // klawisz specjalny (strzałka, F1…), nie znak
        }
        return chars
    }

    override func keyDown(with e: NSEvent) {
        if let text = optionText(e) {
            for u in text.utf16 { connection?.unicode(down: true, u); connection?.unicode(down: false, u) }
            return
        }
        if e.modifierFlags.contains(.option) && !altSent {
            key(0x38, down: true)   // Option + klawisz specjalny = Alt
            altSent = true
        }
        key(scancode(e.keyCode), down: true)
    }

    override func keyUp(with e: NSEvent) {
        let code = scancode(e.keyCode)
        if pressed.contains(code) { key(code, down: false) }
    }

    override func flagsChanged(with e: NSEvent) {
        let raw = e.modifierFlags.rawValue
        // Maski urządzenia (lewy/prawy) z IOKit: NX_DEVICEL*KEYMASK.
        let masks: [UInt16: UInt] = [56: 0x02, 60: 0x04, 59: 0x01, 62: 0x2000, 55: 0x08, 54: 0x10, 58: 0x20, 61: 0x40]
        switch e.keyCode {
        case 57:   // Caps Lock: przełącznik — wyślij naciśnięcie i puszczenie
            key(0x3A, down: true); key(0x3A, down: false)
        case 58, 61:
            // Option nie idzie od razu jako Alt (znaki z Option); puszczenie zwalnia Alt, jeśli był wysłany.
            if raw & masks[e.keyCode]! == 0 && altSent && raw & 0x60 == 0 {
                key(0x38, down: false)
                altSent = false
            }
        default:
            guard let m = masks[e.keyCode] else { return }
            key(scancode(e.keyCode), down: raw & m != 0)
        }
    }

    override func performKeyEquivalent(with e: NSEvent) -> Bool {
        guard window?.firstResponder === self, e.type == .keyDown else { return super.performKeyEquivalent(with: e) }
        return handleCommandKey(e) || super.performKeyEquivalent(with: e)
    }

    /// ⌘+klawisz do sesji jako Ctrl+klawisz (⌘C, ⌘V, ⌘A, ⌘Z…). Wołane też z monitora klawiszy
    /// w AppModel — zanim skrót trafi do menu Edycja aplikacji (inaczej ⌘V wklejało „w Waypoincie").
    /// Skróty Waypointa i systemu (⌘Q, ⌘W, ⌘H, ⌘1–9…) zostają lokalne → false.
    func handleCommandKey(_ e: NSEvent) -> Bool {
        guard connection != nil, e.modifierFlags.contains(.command), !e.modifierFlags.contains(.option) else { return false }
        let ch = e.charactersIgnoringModifiers?.lowercased() ?? ""
        let local: Set<String> = ["q", "w", "h", "m", ",", "`", "k", "n", "d"]
        if local.contains(ch) || Int(ch) != nil || e.modifierFlags.contains(.shift) && ["[", "]", "{", "}"].contains(ch) {
            return false
        }
        if !pressed.contains(0x1D) && !pressed.contains(0x11D) {
            key(0x1D, down: true)   // ⌘ wciśnięty zanim sesja dostała fokus — Ctrl jeszcze nie poszedł
        }
        let code = scancode(e.keyCode)
        // Wklejenie: najpierw aktualny schowek Maca do sesji, potem Ctrl+V — inaczej (sprawdzanie co
        // 0,5 s) świeżo skopiowany tekst docierał po wklejeniu i Windows wklejał poprzednią zawartość.
        if ch == "v", session?.preparePaste() == true {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { [weak self] in
                guard let self, self.connection != nil else { return }
                self.connection?.key(down: true, scancode: code)
                self.connection?.key(down: false, scancode: code)
            }
            return true
        }
        key(code, down: true)
        key(code, down: false)   // AppKit nie przysyła keyUp dla skrótów z ⌘
        return true
    }

    override func becomeFirstResponder() -> Bool {
        _ = session?.syncPasteboard()   // skopiowane w innej aplikacji tuż przed kliknięciem w pulpit
        return super.becomeFirstResponder()
    }

    override func resignFirstResponder() -> Bool {
        releaseAll()
        return super.resignFirstResponder()
    }

    /// Puszcza wszystko, co serwer uważa za wciśnięte (utrata fokusu, rozłączenie) — bez „zaciętego" Ctrl.
    private func releaseAll() {
        if let c = connection {
            for code in pressed { c.key(down: false, scancode: code) }
            if altSent { c.key(down: false, scancode: 0x38) }
        }
        pressed.removeAll()
        altSent = false
    }
}

/// Scala wiele „nowa klatka" z wątku FreeRDP w jedno wywołanie na wątku głównym (bez zalewania kolejki).
final class FrameTicker: @unchecked Sendable {
    private let lock = NSLock()
    private var scheduled = false
    private let fire: @MainActor () -> Void

    init(_ fire: @escaping @MainActor () -> Void) { self.fire = fire }

    func tick() {
        lock.lock()
        let schedule = !scheduled
        scheduled = true
        lock.unlock()
        guard schedule else { return }
        DispatchQueue.main.async {
            self.lock.lock(); self.scheduled = false; self.lock.unlock()
            MainActor.assumeIsolated { self.fire() }
        }
    }
}
