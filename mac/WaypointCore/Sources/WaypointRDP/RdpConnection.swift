import Foundation
import RdpShim

/// Wbudowany klient RDP (FreeRDP 3) — jedno połączenie. Wywołania zwrotne przychodzą z wątku
/// połączenia; interfejs sam przerzuca je na główny wątek. Obraz czyta się przez `withFrame`.
public final class RdpConnection: @unchecked Sendable {
    public struct Config: Sendable {
        public var host: String
        public var port: Int = 3389
        public var username = ""
        public var domain = ""
        public var password = ""
        public var width = 1280
        public var height = 800
        public var scalePercent = 100
        public var adminSession = false
        public var redirectClipboard = true
        public var configDirectory = ""
        public var clientHostname = ""
        public var gatewayHost = ""
        public var gatewayPort = 443
        public var gatewayUsername = ""
        public var gatewayDomain = ""
        public var gatewayPassword = ""
        public var gatewaySameCredentials = true
        /// Tylko testy — normalnie certyfikat potwierdza użytkownik (`onVerifyCertificate`).
        public var ignoreCertificate = false
        /// Folder Maca jako dysk w sesji (pusty = bez udostępniania).
        public var drivePath = ""
        public var driveName = "Mac"
        /// Katalog dziennika diagnostycznego (pusty = tylko błędy). Plik waypoint-rdp.log.
        public var logDirectory = ""
        /// Układ klawiatury sesji (Windows KLID, np. 0x415 — polski programisty); 0 = domyślny serwera.
        public var keyboardLayout: UInt32 = 0
        public init(host: String) { self.host = host }
    }

    public enum State: Equatable, Sendable {
        case connecting
        case connected
        /// error == 0: zwykłe rozłączenie / wylogowanie.
        case disconnected(error: UInt32, message: String)
    }

    public struct Certificate: Sendable {
        public var host: String
        public var port: Int
        public var commonName: String
        public var subject: String
        public var issuer: String
        public var fingerprint: String
        public var changed: Bool
        public var oldFingerprint: String
        public var gateway: Bool
    }

    public enum CertificateAnswer: Sendable { case reject, acceptAlways, acceptOnce }

    public struct Credentials: Sendable {
        public var username: String
        public var password: String
        public var domain: String
        public init(username: String, password: String, domain: String) {
            self.username = username; self.password = password; self.domain = domain
        }
    }

    public struct Rect: Equatable, Sendable { public var x, y, width, height: Int }

    public var onFrame: (@Sendable (Rect) -> Void)?
    public var onResize: (@Sendable (Int, Int) -> Void)?
    public var onState: (@Sendable (State) -> Void)?
    /// Wątek połączenia czeka na odpowiedź (może pokazać pytanie i blokować).
    public var onVerifyCertificate: (@Sendable (Certificate) -> CertificateAnswer)?
    /// Brak lub odrzucone hasło: nowe dane albo nil = przerwij.
    public var onAuthenticate: (@Sendable (_ gateway: Bool, _ username: String, _ domain: String) -> Credentials?)?
    /// Tekst skopiowany w sesji (końce linii już jako \n). Wątek kanału.
    public var onClipboardText: (@Sendable (String) -> Void)?
    /// Pliki skopiowane w sesji (lista; treść przez `downloadClipboardFiles`). Wątek kanału.
    public var onClipboardFiles: (@Sendable ([RemoteFile]) -> Void)?

    /// Kursor z sesji. Wątek połączenia.
    public enum Cursor: Sendable {
        case image(width: Int, height: Int, hotX: Int, hotY: Int, bgra: Data)   // nieprzemnożona alfa
        case hidden, standard
    }
    public var onCursor: (@Sendable (Cursor) -> Void)?
    /// Serwer chce ogłoszonej (`announceClipboard`) treści — odczytaj schowek i podaj ją przez
    /// `setClipboardText` / `setClipboardFiles`. Wątek kanału; wolno blokować (krótko).
    public var onClipboardProvide: (@Sendable () -> Void)?

    /// Plik w schowku sesji: ścieżka względna z „/", rozmiar (nil = nieznany), katalog.
    public struct RemoteFile: Equatable, Sendable {
        public var path: String
        public var size: Int64?
        public var isDirectory: Bool
        public init(path: String, size: Int64?, isDirectory: Bool) { self.path = path; self.size = size; self.isDirectory = isDirectory }
    }

    /// Plik z Maca dla schowka sesji: nazwa względna („folder/plik.txt"), pełna ścieżka, rozmiar.
    public struct LocalFile: Equatable, Sendable {
        public var name: String
        public var path: String
        public var size: Int64
        public var isDirectory: Bool
    }

    public enum TransferError: Error, Equatable { case notConnected, failed(String), cancelled, timeout }

    // Jedna prośba o fragment pliku naraz (FileContents) — odpowiedź z wątku kanału.
    private let fileLock = NSLock()
    private var fileWait: (stream: UInt32, done: DispatchSemaphore, ok: Bool, data: Data)?
    private var nextStream: UInt32 = 1

    private var handle: OpaquePointer?
    private let lock = NSLock()

    public static var isAvailable: Bool { wp_rdp_available() != 0 }

    /// Wpis do dziennika diagnostycznego RDP (gdy włączony w Ustawieniach).
    public static func log(_ message: String) { wp_rdp_log(message) }

    public init() {}

    deinit { stop() }

    /// Czy kod błędu oznacza odrzucone poświadczenia (wtedy warto zapytać o hasło i spróbować ponownie).
    public static func isAuthenticationError(_ code: UInt32) -> Bool { wp_rdp_error_is_auth(code) != 0 }

    /// Kod klawisza macOS (NSEvent.keyCode) → kod skanowania RDP (0 = nieznany).
    public static func scancode(macKeyCode: UInt16) -> UInt32 { wp_rdp_scancode_from_mac_keycode(UInt32(macKeyCode)) }

    @discardableResult
    public func start(_ c: Config) -> Bool {
        stop()
        // Napisy C żyją do końca wp_rdp_start (FreeRDP kopiuje ustawienia).
        var owned: [UnsafeMutablePointer<CChar>] = []
        func s(_ v: String) -> UnsafePointer<CChar>? {
            guard !v.isEmpty, let p = strdup(v) else { return nil }
            owned.append(p)
            return UnsafePointer(p)
        }
        defer {
            for p in owned { memset(p, 0, strlen(p)); free(p) }   // hasła nie zostają w pamięci
        }
        var cfg = wp_rdp_config()
        cfg.host = s(c.host)
        cfg.port = UInt16(clamping: c.port)
        cfg.username = s(c.username)
        cfg.domain = s(c.domain)
        cfg.password = s(c.password)
        cfg.width = Int32(c.width)
        cfg.height = Int32(c.height)
        cfg.scale_percent = Int32(c.scalePercent)
        cfg.admin_session = c.adminSession ? 1 : 0
        cfg.redirect_clipboard = c.redirectClipboard ? 1 : 0
        cfg.config_dir = s(c.configDirectory)
        cfg.client_hostname = s(c.clientHostname)
        cfg.gateway_host = s(c.gatewayHost)
        cfg.gateway_port = UInt16(clamping: c.gatewayPort)
        cfg.gateway_username = s(c.gatewayUsername)
        cfg.gateway_domain = s(c.gatewayDomain)
        cfg.gateway_password = s(c.gatewayPassword)
        cfg.gateway_same_credentials = c.gatewaySameCredentials ? 1 : 0
        cfg.ignore_certificate = c.ignoreCertificate ? 1 : 0
        cfg.drive_path = s(c.drivePath)
        cfg.drive_name = s(c.driveName)
        cfg.log_dir = s(c.logDirectory)
        cfg.keyboard_layout = c.keyboardLayout

        var cb = wp_rdp_callbacks()
        cb.user = Unmanaged.passUnretained(self).toOpaque()
        cb.frame = { user, x, y, w, h in
            RdpConnection.from(user).onFrame?(Rect(x: Int(x), y: Int(y), width: Int(w), height: Int(h)))
        }
        cb.resized = { user, w, h in RdpConnection.from(user).onResize?(Int(w), Int(h)) }
        cb.state = { user, state, error, message in
            let me = RdpConnection.from(user)
            switch state {
            case Int32(WP_RDP_CONNECTING): me.onState?(.connecting)
            case Int32(WP_RDP_CONNECTED): me.onState?(.connected)
            default: me.onState?(.disconnected(error: error, message: message.map { String(cString: $0) } ?? ""))
            }
        }
        cb.verify_certificate = { user, host, port, cn, subject, issuer, fp, changed, oldFp, gateway in
            let me = RdpConnection.from(user)
            guard let ask = me.onVerifyCertificate else { return Int32(WP_CERT_REJECT) }
            func str(_ p: UnsafePointer<CChar>?) -> String { p.map { String(cString: $0) } ?? "" }
            let answer = ask(Certificate(host: str(host), port: Int(port), commonName: str(cn), subject: str(subject),
                                         issuer: str(issuer), fingerprint: str(fp), changed: changed != 0,
                                         oldFingerprint: str(oldFp), gateway: gateway != 0))
            switch answer {
            case .reject: return Int32(WP_CERT_REJECT)
            case .acceptAlways: return Int32(WP_CERT_ACCEPT_ALWAYS)
            case .acceptOnce: return Int32(WP_CERT_ACCEPT_ONCE)
            }
        }
        cb.authenticate = { user, uBuf, uLen, pBuf, pLen, dBuf, dLen, gateway in
            let me = RdpConnection.from(user)
            guard let ask = me.onAuthenticate, let uBuf, let pBuf, let dBuf else { return 0 }
            guard let cred = ask(gateway != 0, String(cString: uBuf), String(cString: dBuf)) else { return 0 }
            func put(_ v: String, _ buf: UnsafeMutablePointer<CChar>, _ len: Int32) {
                let bytes = Array(v.utf8.prefix(Int(len) - 1))
                for (i, b) in bytes.enumerated() { buf[i] = CChar(bitPattern: b) }
                buf[bytes.count] = 0
            }
            put(cred.username, uBuf, uLen)
            put(cred.password, pBuf, pLen)
            put(cred.domain, dBuf, dLen)
            return 1
        }
        cb.clipboard_text = { user, text in
            guard let text else { return }
            RdpConnection.from(user).onClipboardText?(String(cString: text).replacingOccurrences(of: "\r\n", with: "\n"))
        }
        cb.clipboard_files = { user, count, names, sizes, dirs in
            guard let names, let sizes, let dirs else { return }
            var list: [RemoteFile] = []
            for i in 0..<Int(count) {
                guard let n = names[i] else { continue }
                list.append(RemoteFile(path: String(cString: n), size: sizes[i] >= 0 ? sizes[i] : nil, isDirectory: dirs[i] != 0))
            }
            RdpConnection.from(user).onClipboardFiles?(list)
        }
        cb.clipboard_file_data = { user, stream, ok, data, length in
            let me = RdpConnection.from(user)
            me.fileLock.lock()
            if var w = me.fileWait, w.stream == stream {
                w.ok = ok != 0
                w.data = (ok != 0 && data != nil) ? Data(bytes: data!, count: Int(length)) : Data()
                me.fileWait = w
                w.done.signal()
            }
            me.fileLock.unlock()
        }
        cb.cursor = { user, kind, w, h, hx, hy, bgra in
            let me = RdpConnection.from(user)
            switch kind {
            case Int32(WP_CURSOR_IMAGE):
                guard let bgra, w > 0, h > 0 else { return }
                me.onCursor?(.image(width: Int(w), height: Int(h), hotX: Int(hx), hotY: Int(hy),
                                    bgra: Data(bytes: bgra, count: Int(w) * Int(h) * 4)))
            case Int32(WP_CURSOR_HIDDEN): me.onCursor?(.hidden)
            default: me.onCursor?(.standard)
            }
        }
        cb.clipboard_provide = { user in RdpConnection.from(user).onClipboardProvide?() }
        let h = wp_rdp_start(&cfg, &cb)
        lock.lock(); handle = h; lock.unlock()
        return h != nil
    }

    private static func from(_ user: UnsafeMutableRawPointer?) -> RdpConnection {
        Unmanaged<RdpConnection>.fromOpaque(user!).takeUnretainedValue()
    }

    /// Rozłącza i czeka na koniec wątku połączenia. Bezpieczne wielokrotnie.
    public func stop() {
        lock.lock()
        let h = handle
        handle = nil
        lock.unlock()
        if let h { wp_rdp_stop(h) }
    }

    private var current: OpaquePointer? { lock.lock(); defer { lock.unlock() }; return handle }

    /// Dostęp do obrazu (BGRA, premultiplied, little-endian = kCGImageAlphaNoneSkipFirst | byteOrder32Little).
    public func withFrame<T>(_ body: (UnsafePointer<UInt8>?, _ width: Int, _ height: Int, _ stride: Int) -> T) -> T {
        guard let h = current else { return body(nil, 0, 0, 0) }
        var data: UnsafePointer<UInt8>?
        var w: Int32 = 0, hh: Int32 = 0, st: Int32 = 0
        _ = wp_rdp_lock_frame(h, &data, &w, &hh, &st)
        defer { wp_rdp_unlock_frame(h) }
        return body(data, Int(w), Int(hh), Int(st))
    }

    public func key(down: Bool, scancode: UInt32) { if let h = current { wp_rdp_key(h, down ? 1 : 0, scancode) } }
    public func unicode(down: Bool, _ code: UInt16) { if let h = current { wp_rdp_unicode(h, down ? 1 : 0, code) } }
    public func mouse(flags: UInt16, x: Int, y: Int) { if let h = current { wp_rdp_mouse(h, flags, Int32(x), Int32(y)) } }
    public func mouseExtended(flags: UInt16, x: Int, y: Int) { if let h = current { wp_rdp_mouse_extended(h, flags, Int32(x), Int32(y)) } }
    public func resize(width: Int, height: Int, scalePercent: Int) {
        if let h = current { wp_rdp_resize(h, Int32(width), Int32(height), Int32(scalePercent)) }
    }
    /// Czy serwer otworzył kanał schowka (bez tego wklejanie nie działa w żadną stronę).
    public var isClipboardReady: Bool { current.map { wp_rdp_clipboard_ready($0) != 0 } ?? false }

    public func syncLocks(_ flags: UInt32) { if let h = current { wp_rdp_sync_locks(h, flags) } }
    /// Pliki z Maca do wklejenia w sesji (zastępuje tekst w schowku sesji).
    public func setClipboardFiles(_ files: [LocalFile]) {
        guard let h = current else { return }
        var owned: [UnsafeMutablePointer<CChar>?] = []
        defer { owned.forEach { free($0) } }
        let names = files.map { f -> UnsafePointer<CChar>? in let p = strdup(f.name); owned.append(p); return UnsafePointer(p) }
        let paths = files.map { f -> UnsafePointer<CChar>? in let p = strdup(f.path); owned.append(p); return UnsafePointer(p) }
        let sizes = files.map { $0.size }
        let dirs = files.map { Int32($0.isDirectory ? 1 : 0) }
        names.withUnsafeBufferPointer { n in
            paths.withUnsafeBufferPointer { p in
                wp_rdp_set_clipboard_files(h, Int32(files.count), n.baseAddress, p.baseAddress, sizes, dirs)
            }
        }
    }

    /// Lista do schowka sesji z zaznaczonych w Finderze plików i folderów (foldery z zawartością,
    /// katalog przed swoimi plikami — tak oczekuje Eksplorator). Dowiązania symboliczne pomijane.
    public static func clipboardEntries(for urls: [URL]) -> [LocalFile] {
        var out: [LocalFile] = []
        let fm = FileManager.default
        func add(_ url: URL, name: String) {
            guard let attrs = try? fm.attributesOfItem(atPath: url.path) else { return }
            let type = attrs[.type] as? FileAttributeType
            if type == .typeSymbolicLink { return }
            if type == .typeDirectory {
                out.append(LocalFile(name: name, path: url.path, size: 0, isDirectory: true))
                let children = ((try? fm.contentsOfDirectory(atPath: url.path)) ?? []).sorted()
                for c in children { add(url.appendingPathComponent(c), name: name + "/" + c) }
            } else if type == .typeRegular {
                let size = (attrs[.size] as? NSNumber)?.int64Value ?? 0
                out.append(LocalFile(name: name, path: url.path, size: size, isDirectory: false))
            }
        }
        for u in urls where u.isFileURL { add(u.standardizedFileURL, name: u.lastPathComponent) }
        return out
    }

    /// Ścieżka z sesji → bezpieczna ścieżka względna (bez „..", „." i pustych członów); nil = odrzucona.
    public static func safeRelativePath(_ remote: String) -> String? {
        let parts = remote.replacingOccurrences(of: "\\", with: "/").split(separator: "/")
            .map(String.init).filter { !$0.isEmpty && $0 != "." }
        guard !parts.isEmpty, !parts.contains("..") else { return nil }
        return parts.joined(separator: "/")
    }

    /// Pobiera pliki ze schowka sesji do `directory` (blokuje — wołać poza wątkiem głównym).
    /// Zwraca elementy najwyższego poziomu (do schowka Finderu). `progress(pobrane, razem)`.
    public func downloadClipboardFiles(_ files: [RemoteFile], to directory: URL, chunk: Int = 1 << 20,
                                       progress: (Int64, Int64) -> Void = { _, _ in },
                                       isCancelled: () -> Bool = { false }) throws -> [URL] {
        let fm = FileManager.default
        try fm.createDirectory(at: directory, withIntermediateDirectories: true)
        var total: Int64 = files.reduce(0) { $0 + ($1.isDirectory ? 0 : ($1.size ?? 0)) }
        var done: Int64 = 0
        var top: [URL] = []
        for (index, f) in files.enumerated() {
            if isCancelled() { throw TransferError.cancelled }
            guard let rel = Self.safeRelativePath(f.path) else { continue }
            let dest = directory.appendingPathComponent(rel)
            if !rel.contains("/") { top.append(dest) }
            if f.isDirectory {
                try fm.createDirectory(at: dest, withIntermediateDirectories: true)
                continue
            }
            try fm.createDirectory(at: dest.deletingLastPathComponent(), withIntermediateDirectories: true)
            var size = f.size ?? -1
            if size < 0 {
                let d = try fileRequest(index: index, sizeOnly: true, position: 0, length: 8)
                guard d.count >= 8 else { throw TransferError.failed(f.path) }
                size = d.withUnsafeBytes { Int64(littleEndian: $0.loadUnaligned(as: Int64.self)) }
                total += size
            }
            guard fm.createFile(atPath: dest.path, contents: nil), let out = FileHandle(forWritingAtPath: dest.path) else {
                throw TransferError.failed(dest.path)
            }
            defer { try? out.close() }
            var pos: Int64 = 0
            while pos < size {
                if isCancelled() { throw TransferError.cancelled }
                let want = Int(min(Int64(chunk), size - pos))
                let d = try fileRequest(index: index, sizeOnly: false, position: UInt64(pos), length: UInt32(want))
                if d.isEmpty { throw TransferError.failed(f.path) }
                try out.write(contentsOf: d)
                pos += Int64(d.count)
                done += Int64(d.count)
                progress(done, total)
            }
        }
        return top
    }

    private func fileRequest(index: Int, sizeOnly: Bool, position: UInt64, length: UInt32) throws -> Data {
        guard let h = current else { throw TransferError.notConnected }
        let sem = DispatchSemaphore(value: 0)
        fileLock.lock()
        let stream = nextStream
        nextStream &+= 1
        fileWait = (stream, sem, false, Data())
        fileLock.unlock()
        defer { fileLock.lock(); fileWait = nil; fileLock.unlock() }
        // Prośba czeka, aż wątek połączenia wyśle poprzednią (jedna naraz).
        var sent = false
        for _ in 0..<200 {
            if wp_rdp_clipboard_request_file(h, stream, Int32(index), sizeOnly ? 1 : 0, position, length) != 0 { sent = true; break }
            Thread.sleep(forTimeInterval: 0.01)
        }
        guard sent else { throw TransferError.notConnected }
        guard sem.wait(timeout: .now() + 60) == .success else { throw TransferError.timeout }
        fileLock.lock()
        let w = fileWait
        fileLock.unlock()
        guard let w, w.ok else { throw TransferError.failed("FileContents") }
        return w.data
    }

    /// Ogłoszenie zawartości schowka Maca bez jej odczytu (treść na żądanie — `onClipboardProvide`).
    public func announceClipboard(text: Bool, files: Bool) {
        if let h = current { wp_rdp_announce_clipboard(h, text ? 1 : 0, files ? 1 : 0) }
    }

    /// Tekst ze schowka Maca dla sesji (nil = pusty schowek / nie tekst). Końce linii → \r\n jak w Windows.
    public func setClipboardText(_ text: String?) {
        guard let h = current else { return }
        guard let text, !text.isEmpty else { wp_rdp_set_clipboard_text(h, nil); return }
        let crlf = text.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\n", with: "\r\n")
        wp_rdp_set_clipboard_text(h, crlf)
    }
}

/// Flagi myszy i klawiatury RDP (wartości z protokołu, jak PTR_FLAGS_* w FreeRDP).
public enum RdpMouse {
    public static let move: UInt16 = 0x0800
    public static let down: UInt16 = 0x8000
    public static let left: UInt16 = 0x1000
    public static let right: UInt16 = 0x2000
    public static let middle: UInt16 = 0x4000
    public static let wheel: UInt16 = 0x0200
    public static let hwheel: UInt16 = 0x0400
    public static let wheelNegative: UInt16 = 0x0100
    /// Dodatkowe przyciski (PTR_XFLAGS_*): wstecz / dalej.
    public static let xDown: UInt16 = 0x8000
    public static let xButton1: UInt16 = 0x0001
    public static let xButton2: UInt16 = 0x0002

    /// Obrót kółka (dodatni = od siebie) → flagi PTR_FLAGS_WHEEL z wartością 9-bitową w uzupełnieniu do dwóch.
    public static func wheelFlags(delta: Int, horizontal: Bool = false) -> UInt16 {
        let d = max(-255, min(255, delta))
        var f: UInt16 = horizontal ? hwheel : wheel
        if d < 0 { f |= wheelNegative | UInt16((0x100 + d) & 0xFF) } else { f |= UInt16(d & 0xFF) }
        return f
    }
}

public enum RdpKeyboard {
    /// Lewy / prawy Alt (prawy = AltGr w układach europejskich, m.in. polskim programisty).
    public static let leftAlt: UInt32 = 0x38
    public static let rightAlt: UInt32 = 0x138

    /// Układ Windows dla układu macOS (identyfikator źródła wprowadzania, np. com.apple.keylayout.PolishPro).
    /// 0 = nieznany — serwer użyje swojego domyślnego.
    public static func windowsLayout(forMacInputSource id: String) -> UInt32 {
        let s = id.lowercased()
        if s.contains("polish") { return 0x415 }            // polski (programisty) — AltGr+a = ą
        if s.contains("german") { return 0x407 }
        if s.hasSuffix(".us") || s.contains("abc") { return 0x409 }
        if s.contains("british") { return 0x809 }
        return 0
    }

    public static let syncScroll: UInt32 = 0x01
    public static let syncNum: UInt32 = 0x02
    public static let syncCaps: UInt32 = 0x04
}
