import Foundation
#if canImport(Darwin)
import Darwin
#elseif canImport(Glibc)
import Glibc
#endif

/// Pytania OpenSSH (hasło, passphrase klucza, potwierdzenie klucza hosta) obsługiwane przez Waypointa.
///
/// Terminal uruchamia systemowe `ssh` z `SSH_ASKPASS=<plik wykonywalny Waypointa>` i
/// `SSH_ASKPASS_REQUIRE=force`. Gdy ssh czegoś potrzebuje, uruchamia ten program z treścią pytania
/// w argumencie; Waypoint w trybie askpass łączy się gniazdem Unix z oknem, które otworzyło sesję,
/// i zwraca odpowiedź na stdout. Okno odpowiada od razu hasłem z Pęku kluczy (pierwsza próba) albo
/// pokazuje arkusz nad kartą terminala.
///
/// Zaleta względem wpisywania hasła „na ekran": ssh pyta askpass WYŁĄCZNIE o uwierzytelnienie. Monit
/// wypisany przez serwer po zalogowaniu (np. podrobione „Password:") nigdy tu nie trafi, więc zapisane
/// hasło nie da się wyłudzić.
public enum Askpass {
    /// Zmienne środowiskowe przekazywane do ssh (i dalej do procesu askpass).
    public static let socketEnv = "WAYPOINT_ASKPASS_SOCKET"
    public static let tokenEnv = "WAYPOINT_ASKPASS_TOKEN"

    public enum Kind: Equatable, Sendable {
        /// Hasło do serwera („user@host's password:", „(user@host) Password:").
        case password
        /// Passphrase klucza prywatnego.
        case passphrase
        /// Pytanie tak/nie (klucz hosta: „Are you sure you want to continue connecting (yes/no/[fingerprint])?").
        case confirm
        /// Inne pytanie keyboard-interactive (np. kod 2FA) — odpowiedź jawna, bez zapisu.
        case other
    }

    /// Rodzaj pytania z jego treści. `SSH_ASKPASS_PROMPT=confirm` (ustawiane przez ssh dla potwierdzeń)
    /// rozstrzyga od razu; w pozostałych przypadkach liczy się tekst.
    public static func classify(_ prompt: String, promptHint: String? = nil) -> Kind {
        if promptHint == "confirm" { return .confirm }
        let p = prompt.lowercased()
        if p.contains("(yes/no") || p.contains("continue connecting") { return .confirm }
        if p.contains("passphrase") { return .passphrase }
        let lastLine = p.split(whereSeparator: \.isNewline).last.map(String.init) ?? p
        if lastLine.range(of: #"password\s*:\s*$"#, options: .regularExpression) != nil { return .password }
        return .other
    }

    public struct Request: Codable, Equatable, Sendable {
        public var token: String
        public var prompt: String
        public var hint: String?
        public init(token: String, prompt: String, hint: String?) {
            self.token = token; self.prompt = prompt; self.hint = hint
        }
    }

    /// Odpowiedź; `nil` = użytkownik anulował (ssh dostaje kod błędu i przerywa tę metodę logowania).
    public struct Reply: Codable, Equatable, Sendable {
        public var value: String?
        public init(value: String?) { self.value = value }
    }

    // MARK: Strona askpass (krótko żyjący proces uruchamiany przez ssh)

    /// Tryb askpass: wysyła pytanie do okna i zwraca kod wyjścia procesu. Wołane na samym początku
    /// `main`, zanim wystartuje interfejs.
    public static func runClient(arguments: [String], environment: [String: String],
                                 output: (String) -> Void = { print($0) }) -> Int32 {
        guard let path = environment[socketEnv], let token = environment[tokenEnv] else { return 2 }
        let prompt = arguments.dropFirst().joined(separator: " ")
        let req = Request(token: token, prompt: prompt, hint: environment["SSH_ASKPASS_PROMPT"])
        guard let reply = exchange(path: path, request: req), let value = reply.value else { return 1 }
        output(value)
        return 0
    }

    static func exchange(path: String, request: Request) -> Reply? {
        guard let fd = UnixSocket.connect(path: path) else { return nil }
        defer { close(fd) }
        guard var data = try? JSONEncoder().encode(request) else { return nil }
        data.append(0x0A)
        guard UnixSocket.writeAll(fd, data), let line = UnixSocket.readLine(fd) else { return nil }
        return try? JSONDecoder().decode(Reply.self, from: line)
    }
}

/// Serwer pytań askpass dla jednej sesji terminala: gniazdo Unix w prywatnym katalogu (0700),
/// plik gniazda 0600 i losowy token — inny proces tego samego użytkownika musiałby znać jedno i drugie.
public final class AskpassServer: @unchecked Sendable {
    public let socketPath: String
    public let token: String
    private let directory: String
    private var listenFD: Int32 = -1
    private let lock = NSLock()
    private var stopped = false
    private let handler: @Sendable (Askpass.Request, @escaping @Sendable (Askpass.Reply) -> Void) -> Void

    /// `handler` dostaje pytanie i funkcję odpowiedzi — może odpowiedzieć od razu albo po decyzji
    /// użytkownika (z dowolnego wątku). Do tego czasu proces askpass, a więc i ssh, czeka.
    public init(handler: @escaping @Sendable (Askpass.Request, @escaping @Sendable (Askpass.Reply) -> Void) -> Void) throws {
        self.handler = handler
        self.token = UUID().uuidString + UUID().uuidString
        // Krótka ścieżka: sun_path ma ~104 bajty, a $TMPDIR na macOS jest długi.
        let base = FileManager.default.fileExists(atPath: "/tmp") ? "/tmp" : NSTemporaryDirectory()
        directory = base + "/wp-" + String(UUID().uuidString.prefix(8)).lowercased()
        socketPath = directory + "/a.sock"
        guard mkdir(directory, 0o700) == 0 else { throw POSIXError(.EACCES) }
        guard let fd = UnixSocket.listen(path: socketPath) else {
            rmdir(directory)
            throw POSIXError(.EADDRINUSE)
        }
        chmod(socketPath, 0o600)
        listenFD = fd
        let t = Thread { [weak self] in self?.acceptLoop(fd) }
        t.name = "waypoint.askpass"
        t.start()
    }

    deinit { stop() }

    public func stop() {
        lock.lock()
        let fd = listenFD
        let wasStopped = stopped
        stopped = true
        listenFD = -1
        lock.unlock()
        if wasStopped { return }
        if fd >= 0 {
            shutdown(fd, Int32(SHUT_RDWR))
            close(fd)
        }
        unlink(socketPath)
        rmdir(directory)
    }

    /// Środowisko dla procesu ssh.
    public func environment(askpassExecutable: String) -> [String: String] {
        [
            "SSH_ASKPASS": askpassExecutable,
            "SSH_ASKPASS_REQUIRE": "force",
            "DISPLAY": ":0",   // starsze OpenSSH bez SSH_ASKPASS_REQUIRE użyją askpass tylko z DISPLAY
            Askpass.socketEnv: socketPath,
            Askpass.tokenEnv: token,
        ]
    }

    // poll z krótkim limitem zamiast blokującego accept: na macOS zamknięcie gniazda z innego wątku
    // nie budzi wiszącego accept(), więc pętla sama sprawdza, czy serwer zatrzymano.
    private func acceptLoop(_ fd: Int32) {
        while true {
            lock.lock(); let done = stopped; lock.unlock()
            if done { return }
            var p = pollfd(fd: fd, events: Int16(POLLIN), revents: 0)
            let r = poll(&p, 1, 250)
            if r == 0 || (r < 0 && errno == EINTR) { continue }
            if r < 0 { return }
            let client = accept(fd, nil, nil)
            if client < 0 {
                lock.lock(); let done = stopped; lock.unlock()
                if done { return }
                if errno == EINTR { continue }
                return
            }
            handle(client)
        }
    }

    private func handle(_ client: Int32) {
        guard let line = UnixSocket.readLine(client),
              let req = try? JSONDecoder().decode(Askpass.Request.self, from: line),
              req.token == token else {
            close(client)
            return
        }
        let once = Once()
        handler(req) { reply in
            guard once.claim() else { return }
            if var data = try? JSONEncoder().encode(reply) {
                data.append(0x0A)
                _ = UnixSocket.writeAll(client, data)
            }
            close(client)
        }
    }
}

private final class Once: @unchecked Sendable {
    private let lock = NSLock()
    private var done = false
    func claim() -> Bool {
        lock.lock(); defer { lock.unlock() }
        if done { return false }
        done = true
        return true
    }
}

/// Minimalne opakowanie gniazd Unix (POSIX) — wspólne dla macOS i Linuksa (testy).
enum UnixSocket {
    #if canImport(Darwin)
    static let streamType = SOCK_STREAM
    #else
    static let streamType = Int32(SOCK_STREAM.rawValue)
    #endif

    private static func address(_ path: String) -> sockaddr_un? {
        var addr = sockaddr_un()
        addr.sun_family = sa_family_t(AF_UNIX)
        let bytes = Array(path.utf8)
        let capacity = MemoryLayout.size(ofValue: addr.sun_path)
        guard bytes.count < capacity else { return nil }
        withUnsafeMutableBytes(of: &addr.sun_path) { buf in
            for (i, b) in bytes.enumerated() { buf[i] = b }
            buf[bytes.count] = 0
        }
        return addr
    }

    static func listen(path: String) -> Int32? {
        guard var addr = address(path) else { return nil }
        let fd = socket(AF_UNIX, streamType, 0)
        guard fd >= 0 else { return nil }
        let ok = withUnsafePointer(to: &addr) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                bind(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size)) == 0
            }
        }
        guard ok, sysListen(fd, 8) == 0 else { close(fd); return nil }
        return fd
    }

    static func connect(path: String) -> Int32? {
        guard var addr = address(path) else { return nil }
        let fd = socket(AF_UNIX, streamType, 0)
        guard fd >= 0 else { return nil }
        let ok = withUnsafePointer(to: &addr) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                posixConnect(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size)) == 0
            }
        }
        guard ok else { close(fd); return nil }
        return fd
    }

    static func writeAll(_ fd: Int32, _ data: Data) -> Bool {
        data.withUnsafeBytes { raw -> Bool in
            var off = 0
            while off < raw.count {
                let n = write(fd, raw.baseAddress! + off, raw.count - off)
                if n <= 0 { if n < 0 && errno == EINTR { continue }; return false }
                off += n
            }
            return true
        }
    }

    /// Czyta do znaku nowej linii (bez niego); limit 64 KB, żeby nikt nie zapchał pamięci.
    static func readLine(_ fd: Int32) -> Data? {
        var out = Data()
        var byte: UInt8 = 0
        while out.count < 65_536 {
            let n = read(fd, &byte, 1)
            if n < 0 && errno == EINTR { continue }
            if n <= 0 { return out.isEmpty ? nil : out }
            if byte == 0x0A { return out }
            out.append(byte)
        }
        return nil
    }
}

// `listen` i `connect` kolidują z nazwami metod powyżej — jawne odwołania do funkcji systemowych.
#if canImport(Darwin)
private func sysListen(_ fd: Int32, _ backlog: Int32) -> Int32 { Darwin.listen(fd, backlog) }
private func posixConnect(_ fd: Int32, _ a: UnsafePointer<sockaddr>, _ l: socklen_t) -> Int32 { Darwin.connect(fd, a, l) }
#else
private func sysListen(_ fd: Int32, _ backlog: Int32) -> Int32 { Glibc.listen(fd, backlog) }
private func posixConnect(_ fd: Int32, _ a: UnsafePointer<sockaddr>, _ l: socklen_t) -> Int32 { Glibc.connect(fd, a, l) }
#endif
