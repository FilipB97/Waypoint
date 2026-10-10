import Foundation
#if canImport(Darwin)
import Darwin
#elseif canImport(Glibc)
import Glibc
#endif

/// Sonda osiągalności: nieblokujące TCP connect do host:port z limitem czasu. Wynik = czas nawiązania
/// połączenia w ms (nil = zamknięty / nieosiągalny / limit czasu). Port Windows ReachabilityService.ProbeAsync.
/// Funkcja blokuje wątek — wołać poza głównym (Task.detached).
public enum TcpProbe {
    public static func probe(host: String, port: Int, timeout: TimeInterval) -> Int? {
        let start = Date()
        guard let fd = connect(host: host, port: port, timeout: timeout) else { return nil }
        close(fd)
        return max(0, Int((Date().timeIntervalSince(start) * 1000).rounded()))
    }

    /// Połączenie TCP z limitem czasu; zwraca gniazdo w trybie blokującym (nil = nie udało się).
    /// Wspólne dla sondy i klienta Telnet.
    public static func connect(host: String, port: Int, timeout: TimeInterval) -> Int32? {
        var err: Int32 = 0
        return connect(host: host, port: port, timeout: timeout, error: &err)
    }

    /// Jak wyżej, z przyczyną niepowodzenia (errno: ECONNREFUSED, ETIMEDOUT…; EAI_* jako ujemne dla DNS).
    public static func connect(host: String, port: Int, timeout: TimeInterval, error: inout Int32) -> Int32? {
        error = 0
        let h = host.trimmingCharacters(in: .whitespaces)
        guard !h.isEmpty, (1...65535).contains(port) else { return nil }
        let start = Date()
        var hints = addrinfo()
        hints.ai_family = AF_UNSPEC
        #if canImport(Glibc)
        hints.ai_socktype = Int32(SOCK_STREAM.rawValue)
        #else
        hints.ai_socktype = SOCK_STREAM
        #endif
        var res: UnsafeMutablePointer<addrinfo>?
        let gai = getaddrinfo(h, String(port), &hints, &res)
        guard gai == 0, let first = res else { error = gai == 0 ? EHOSTUNREACH : -abs(gai); return nil }
        defer { freeaddrinfo(first) }

        var ai: UnsafeMutablePointer<addrinfo>? = first
        while let a = ai {
            let left = timeout - Date().timeIntervalSince(start)
            if left <= 0 { error = ETIMEDOUT; return nil }
            if let fd = tryConnect(a.pointee, timeoutMs: Int32(left * 1000), error: &error) { return fd }
            ai = a.pointee.ai_next
        }
        return nil
    }

    /// Opis przyczyny z `connect(…, error:)`.
    public static func describe(_ error: Int32) -> String {
        guard error < 0 else { return String(cString: strerror(error)) }
        // Kody EAI_* są dodatnie w macOS, a ujemne w glibc — zapisane zawsze jako ujemne.
        #if canImport(Glibc)
        return String(cString: gai_strerror(error))
        #else
        return String(cString: gai_strerror(-error))
        #endif
    }

    private static func tryConnect(_ a: addrinfo, timeoutMs: Int32, error: inout Int32) -> Int32? {
        let fd = socket(a.ai_family, a.ai_socktype, a.ai_protocol)
        guard fd >= 0 else { error = errno; return nil }
        #if canImport(Darwin)
        var one: Int32 = 1   // zamknięte gniazdo nie może zabić procesu sygnałem SIGPIPE
        setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &one, socklen_t(MemoryLayout<Int32>.size))
        #endif
        let flags = fcntl(fd, F_GETFL, 0)
        _ = fcntl(fd, F_SETFL, flags | O_NONBLOCK)
        var ok = false
        if sysConnect(fd, a) == 0 {
            ok = true
        } else if errno == EINPROGRESS {
            var p = pollfd(fd: fd, events: Int16(POLLOUT), revents: 0)
            if poll(&p, 1, max(1, timeoutMs)) == 1 {
                var err: Int32 = 0
                var len = socklen_t(MemoryLayout<Int32>.size)
                ok = getsockopt(fd, SOL_SOCKET, SO_ERROR, &err, &len) == 0 && err == 0
                if !ok { error = err }
            } else {
                error = ETIMEDOUT
            }
        } else {
            error = errno
        }
        guard ok else { close(fd); return nil }
        _ = fcntl(fd, F_SETFL, flags)   // z powrotem blokujące
        return fd
    }

    // `connect` z libc — nazwa zasłonięta przez statyczną metodę `connect(host:port:timeout:)`.
    private static func sysConnect(_ fd: Int32, _ a: addrinfo) -> Int32 {
        #if canImport(Darwin)
        return Darwin.connect(fd, a.ai_addr, a.ai_addrlen)
        #else
        return Glibc.connect(fd, a.ai_addr, a.ai_addrlen)
        #endif
    }
}

extension RemoteProtocol {
    /// Czy sonda TCP host:port ma sens: COM, WWW i REST (adres URL) — nie (jak w Windows).
    public var probeable: Bool {
        switch self {
        case .serial, .http, .rest: return false
        default: return true
        }
    }
}
