import Foundation
#if canImport(Darwin)
import Darwin
#elseif canImport(Glibc)
import Glibc
#endif

/// Telnet i port szeregowy w terminalu. Karta uruchamia ten sam plik wykonywalny Waypointa w trybie
/// pomocniczym (`--waypoint-helper telnet|serial …`) w pseudo-terminalu — tak jak `ssh` dla SSH — więc
/// karty, szukanie, czcionka, snippety i „Połącz ponownie" działają bez osobnego kodu. Pomocnik
/// przełącza swój terminal w tryb surowy i przekazuje bajty w obie strony.
public enum StreamHelper {
    public static let flag = "--waypoint-helper"
    /// Kod wyjścia, gdy połączenie się nie udało (jak 255 w ssh) — dziennik zapisze FAILED.
    public static let failedExit: Int32 = 255

    public static func telnetArguments(host: String, port: Int) -> [String] { [flag, "telnet", host, String(port)] }
    public static func serialArguments(device: String, baud: Int) -> [String] { [flag, "serial", device, String(baud)] }

    /// Punkt wejścia trybu pomocniczego (argumenty bez nazwy programu). Zwraca kod wyjścia.
    public static func main(_ args: [String]) -> Int32 {
        guard args.count >= 4, args[0] == flag else { return 2 }
        let restore = RawMode.enable(STDIN_FILENO)
        defer { restore() }
        switch args[1] {
        case "telnet":
            let port = Int(args[3]) ?? 23
            say(String(format: L("stream.connecting"), args[2], port))
            guard let fd = TcpProbe.connect(host: args[2], port: port, timeout: 15) else {
                say(String(format: L("stream.failed"), args[2], port, String(cString: strerror(errno))))
                return failedExit
            }
            say(L("stream.connected"))
            var codec = TelnetCodec()
            pump(local: STDIN_FILENO, output: STDOUT_FILENO, remote: fd,
                 fromRemote: { codec.receive($0) }, fromLocal: { TelnetCodec.encode($0) })
            close(fd)
            say(L("stream.closed"))
            return 0
        case "serial":
            let baud = Int(args[3]) ?? 115_200
            switch SerialPort.open(args[2], baud: baud) {
            case .failure(let msg):
                say(String(format: L("stream.serial.failed"), args[2], msg))
                return failedExit
            case .success(let fd):
                say(String(format: L("stream.serial.open"), args[2], baud))
                pump(local: STDIN_FILENO, output: STDOUT_FILENO, remote: fd,
                     fromRemote: { ($0, []) }, fromLocal: { $0 })
                close(fd)
                say(L("stream.closed"))
                return 0
            }
        default:
            return 2
        }
    }

    private static func say(_ s: String) {
        writeAll(STDOUT_FILENO, Array(("\r\n\u{1b}[2m[Waypoint] " + s + "\u{1b}[0m\r\n").utf8))
    }

    /// Przekazywanie bajtów: lokalne wejście → zdalna strona (po `fromLocal`), zdalna strona →
    /// wyjście (dane) + odpowiedzi protokołu z powrotem. Kończy się, gdy któraś strona się zamknie.
    public static func pump(local: Int32, output: Int32, remote: Int32,
                            fromRemote: ([UInt8]) -> (data: [UInt8], reply: [UInt8]),
                            fromLocal: ([UInt8]) -> [UInt8]) {
        var fds = [pollfd(fd: local, events: Int16(POLLIN), revents: 0),
                   pollfd(fd: remote, events: Int16(POLLIN), revents: 0)]
        var buf = [UInt8](repeating: 0, count: 16 * 1024)
        while true {
            fds[0].revents = 0; fds[1].revents = 0
            let r = poll(&fds, 2, -1)
            if r < 0 { if errno == EINTR { continue } else { return } }
            let bad = Int16(POLLERR | POLLHUP | POLLNVAL)
            if fds[1].revents & Int16(POLLIN) != 0 || fds[1].revents & bad != 0 {
                let n = read(remote, &buf, buf.count)
                if n <= 0 { return }
                let (data, reply) = fromRemote(Array(buf[0..<n]))
                if !data.isEmpty, !writeAll(output, data) { return }
                if !reply.isEmpty, !writeAll(remote, reply) { return }
            }
            if fds[0].revents & Int16(POLLIN) != 0 || fds[0].revents & bad != 0 {
                let n = read(local, &buf, buf.count)
                if n <= 0 { return }
                if !writeAll(remote, fromLocal(Array(buf[0..<n]))) { return }
            }
        }
    }

    @discardableResult
    static func writeAll(_ fd: Int32, _ bytes: [UInt8]) -> Bool {
        var off = 0
        while off < bytes.count {
            let n = bytes[off...].withUnsafeBytes { write(fd, $0.baseAddress, $0.count) }
            if n < 0 { if errno == EINTR { continue }; return false }
            off += n
        }
        return true
    }
}

/// Tryb surowy terminala pomocnika: bez echa i buforowania linii — o tym decyduje zdalna strona.
enum RawMode {
    static func enable(_ fd: Int32) -> () -> Void {
        guard isatty(fd) == 1 else { return {} }
        var old = termios()
        guard tcgetattr(fd, &old) == 0 else { return {} }
        var raw = old
        cfmakeraw(&raw)
        tcsetattr(fd, TCSANOW, &raw)
        return { var o = old; tcsetattr(fd, TCSANOW, &o) }
    }
}

/// Protokół Telnet (RFC 854) — port maszyny stanów z wersji Windows, z rozsądniejszą negocjacją:
/// zgadzamy się na ECHO i SUPPRESS-GO-AHEAD serwera (bez tego linuksowy telnetd nie pokazuje
/// wpisywanych znaków), wszystkich innych opcji odmawiamy. Odpowiadamy tylko na zmianę stanu, więc
/// nie ma pętli negocjacji. Subnegocjacje są pomijane.
public struct TelnetCodec: Sendable {
    public static let IAC: UInt8 = 255, DONT: UInt8 = 254, DO: UInt8 = 253, WONT: UInt8 = 252, WILL: UInt8 = 251
    public static let SB: UInt8 = 250, SE: UInt8 = 240
    public static let ECHO: UInt8 = 1, SGA: UInt8 = 3

    private var state = 0          // 0 dane, 1 po IAC, 2 po IAC+czasownik, 3 subnegocjacja, 4 subneg. po IAC
    private var verb: UInt8 = 0
    private var lastWasCR = false
    /// Opcje włączone po stronie serwera (on WILL, my DO) i po naszej (on DO, my WILL).
    private var remoteOn = Set<UInt8>()
    private var localOn = Set<UInt8>()

    public init() {}

    static func acceptRemote(_ opt: UInt8) -> Bool { opt == ECHO || opt == SGA }
    static func acceptLocal(_ opt: UInt8) -> Bool { opt == SGA }

    /// Bajty z sieci → (dane dla terminala, odpowiedź do serwera). Działa przez granice buforów.
    public mutating func receive(_ bytes: [UInt8]) -> (data: [UInt8], reply: [UInt8]) {
        var data: [UInt8] = [], reply: [UInt8] = []
        for b in bytes {
            switch state {
            case 0:
                if b == Self.IAC { state = 1; continue }
                // CR NUL (NVT) → samo CR.
                if lastWasCR && b == 0 { lastWasCR = false; continue }
                lastWasCR = b == 13
                data.append(b)
            case 1:
                if b == Self.IAC { data.append(Self.IAC); state = 0 }
                else if (Self.WILL...Self.DONT).contains(b) { verb = b; state = 2 }
                else if b == Self.SB { state = 3 }
                else { state = 0 }   // NOP, GA i inne polecenia bez argumentu
            case 2:
                reply += negotiate(verb, b)
                state = 0
            case 3:
                if b == Self.IAC { state = 4 }
            default:
                state = b == Self.SE ? 0 : 3
            }
        }
        return (data, reply)
    }

    private mutating func negotiate(_ verb: UInt8, _ opt: UInt8) -> [UInt8] {
        switch verb {
        case Self.WILL:
            if remoteOn.contains(opt) { return [] }
            if Self.acceptRemote(opt) { remoteOn.insert(opt); return [Self.IAC, Self.DO, opt] }
            return [Self.IAC, Self.DONT, opt]
        case Self.WONT:
            return remoteOn.remove(opt) != nil ? [Self.IAC, Self.DONT, opt] : []
        case Self.DO:
            if localOn.contains(opt) { return [] }
            if Self.acceptLocal(opt) { localOn.insert(opt); return [Self.IAC, Self.WILL, opt] }
            return [Self.IAC, Self.WONT, opt]
        default:   // DONT
            return localOn.remove(opt) != nil ? [Self.IAC, Self.WONT, opt] : []
        }
    }

    /// Bajty z klawiatury → sieć: 255 podwojone (IAC IAC), samotne CR jako CR NUL (NVT).
    public static func encode(_ bytes: [UInt8]) -> [UInt8] {
        var out: [UInt8] = []
        out.reserveCapacity(bytes.count + 4)
        for (i, b) in bytes.enumerated() {
            out.append(b)
            if b == IAC { out.append(IAC) }
            if b == 13 && (i + 1 >= bytes.count || bytes[i + 1] != 10) { out.append(0) }
        }
        return out
    }
}

/// Port szeregowy (8N1, bez kontroli przepływu — jak w Windows) i wykrywanie urządzeń.
public enum SerialPort {
    public static let commonBauds = [1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600]

    public enum OpenResult { case success(Int32), failure(String) }

    public static func open(_ path: String, baud: Int) -> OpenResult {
        #if canImport(Darwin)
        let fd = Darwin.open(path, O_RDWR | O_NOCTTY | O_NONBLOCK)
        #else
        let fd = Glibc.open(path, O_RDWR | O_NOCTTY | O_NONBLOCK)
        #endif
        guard fd >= 0 else { return .failure(String(cString: strerror(errno))) }
        var t = termios()
        guard tcgetattr(fd, &t) == 0 else { let e = String(cString: strerror(errno)); close(fd); return .failure(e) }
        cfmakeraw(&t)
        t.c_cflag |= tcflag_t(CLOCAL | CREAD)
        #if canImport(Darwin)
        t.c_cflag &= ~tcflag_t(CRTSCTS | CSTOPB | PARENB)
        #else
        t.c_cflag &= ~tcflag_t(UInt32(CRTSCTS) | UInt32(CSTOPB) | UInt32(PARENB))
        #endif
        guard let speed = speedValue(baud) else { close(fd); return .failure(String(format: L("stream.serial.baud"), baud)) }
        cfsetispeed(&t, speed)
        cfsetospeed(&t, speed)
        guard tcsetattr(fd, TCSANOW, &t) == 0 else { let e = String(cString: strerror(errno)); close(fd); return .failure(e) }
        _ = fcntl(fd, F_SETFL, fcntl(fd, F_GETFL, 0) & ~O_NONBLOCK)
        return .success(fd)
    }

    /// macOS przyjmuje dowolną prędkość wprost; Linux — tylko stałe B….
    static func speedValue(_ baud: Int) -> speed_t? {
        #if canImport(Darwin)
        return baud > 0 ? speed_t(baud) : nil
        #else
        let table: [Int: Int32] = [1200: B1200, 2400: B2400, 4800: B4800, 9600: B9600, 19200: B19200, 38400: B38400,
                                   57600: B57600, 115200: B115200, 230400: B230400, 460800: B460800, 921600: B921600]
        return table[baud].map { speed_t($0) }
        #endif
    }

    /// Urządzenia do wyboru: na macOS `/dev/cu.*` (wywołujące — nie czekają na DCD), na Linuksie USB/ACM.
    public static func devices(in dir: String = "/dev") -> [String] {
        let names = (try? FileManager.default.contentsOfDirectory(atPath: dir)) ?? []
        return names.filter { $0.hasPrefix("cu.") || $0.hasPrefix("ttyUSB") || $0.hasPrefix("ttyACM") }
            .filter { $0 != "cu.Bluetooth-Incoming-Port" }
            .sorted().map { dir + "/" + $0 }
    }
}

/// VNC przez systemowe Udostępnianie ekranu i strony WWW w domyślnej przeglądarce.
public enum ExternalLinks {
    /// Port `UrlValidation.TryNormalizeWebUrl`: bez schematu → https://; tylko http/https (host
    /// z innym schematem uruchomiłby inną aplikację bez pytania).
    public static func webURL(_ raw: String) -> URL? {
        var t = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !t.isEmpty else { return nil }
        if !t.contains("://") { t = "https://" + t }
        guard let u = URL(string: t), let scheme = u.scheme?.lowercased(), scheme == "http" || scheme == "https",
              let host = u.host, !host.isEmpty else { return nil }
        return u
    }

    /// `vnc://user@host:port` dla Udostępniania ekranu (hasło poda ono samo). IPv6 w nawiasach.
    public static func vncURL(_ s: Server) -> URL? {
        let h = s.host.trimmingCharacters(in: .whitespaces)
        guard !h.isEmpty, !h.contains("/"), !h.contains("@") else { return nil }
        var c = URLComponents()
        c.scheme = "vnc"
        c.host = h.contains(":") && !h.hasPrefix("[") ? "[" + h + "]" : h
        if s.port != 5900 { c.port = s.port }
        if !s.username.isEmpty { c.user = s.username }
        return c.url
    }
}
