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
        guard getaddrinfo(h, String(port), &hints, &res) == 0, let first = res else { return nil }
        defer { freeaddrinfo(first) }

        var ai: UnsafeMutablePointer<addrinfo>? = first
        while let a = ai {
            let left = timeout - Date().timeIntervalSince(start)
            if left <= 0 { return nil }
            if tryConnect(a.pointee, timeoutMs: Int32(left * 1000)) {
                return max(0, Int((Date().timeIntervalSince(start) * 1000).rounded()))
            }
            ai = a.pointee.ai_next
        }
        return nil
    }

    private static func tryConnect(_ a: addrinfo, timeoutMs: Int32) -> Bool {
        let fd = socket(a.ai_family, a.ai_socktype, a.ai_protocol)
        guard fd >= 0 else { return false }
        defer { close(fd) }
        #if canImport(Darwin)
        var one: Int32 = 1   // zamknięte gniazdo nie może zabić procesu sygnałem SIGPIPE
        setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &one, socklen_t(MemoryLayout<Int32>.size))
        #endif
        let flags = fcntl(fd, F_GETFL, 0)
        _ = fcntl(fd, F_SETFL, flags | O_NONBLOCK)
        let r = connect(fd, a.ai_addr, a.ai_addrlen)
        if r == 0 { return true }
        guard errno == EINPROGRESS else { return false }
        var p = pollfd(fd: fd, events: Int16(POLLOUT), revents: 0)
        guard poll(&p, 1, max(1, timeoutMs)) == 1 else { return false }
        var err: Int32 = 0
        var len = socklen_t(MemoryLayout<Int32>.size)
        guard getsockopt(fd, SOL_SOCKET, SO_ERROR, &err, &len) == 0 else { return false }
        return err == 0
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
