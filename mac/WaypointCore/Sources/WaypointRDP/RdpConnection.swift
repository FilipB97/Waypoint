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

    private var handle: OpaquePointer?
    private let lock = NSLock()

    public static var isAvailable: Bool { wp_rdp_available() != 0 }

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
    public func syncLocks(_ flags: UInt32) { if let h = current { wp_rdp_sync_locks(h, flags) } }
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
    public static let syncScroll: UInt32 = 0x01
    public static let syncNum: UInt32 = 0x02
    public static let syncCaps: UInt32 = 0x04
}
