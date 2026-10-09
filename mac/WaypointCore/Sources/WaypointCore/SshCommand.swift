import Foundation

/// Polecenie `ssh` dla serwera. Terminal na Macu uruchamia systemowe `/usr/bin/ssh` (OpenSSH) zamiast
/// własnej implementacji protokołu — dzięki temu działają od razu `~/.ssh/config`, agent (także klucze
/// z Pęku kluczy), `known_hosts` i ProxyJump, dokładnie tak jak w Terminalu.
public enum SshCommand {
    public static let executable = "/usr/bin/ssh"

    public struct Launch: Equatable, Sendable {
        public var arguments: [String]
        /// Klucze tekstów z ostrzeżeniami do pokazania nad terminalem (np. ścieżka klucza z Windows).
        public var warnings: [String]
    }

    /// Polecenie dla panelu plików: podsystem sftp przez ten sam ssh (bez tuneli — te otwiera terminal).
    public static func buildSftp(_ s: Server, homeDirectory: String, fileExists: (String) -> Bool) -> Launch {
        var noTunnels = s
        noTunnels.tunnels = []
        var l = build(noTunnels, homeDirectory: homeDirectory, fileExists: fileExists)
        // „-- host" → „-T -o ClearAllForwardings=yes -s -- host sftp"
        let tail = l.arguments.suffix(2)
        l.arguments.removeLast(2)
        l.arguments += ["-T", "-o", "ClearAllForwardings=yes", "-s"] + tail + ["sftp"]
        return l
    }

    public static func build(_ s: Server, homeDirectory: String, fileExists: (String) -> Bool) -> Launch {
        var args: [String] = []
        var warnings: [String] = []

        // Utrzymanie połączenia przy bezczynności (NAT, firewalle) — jak keep-alive w wersji Windows.
        args += ["-o", "ServerAliveInterval=30", "-o", "ServerAliveCountMax=4"]

        let key = s.privateKeyPath.trimmingCharacters(in: .whitespaces)
        if !key.isEmpty {
            if looksLikeWindowsPath(key) {
                // Import z Windows: „C:\Users\…\id_ed25519" nie istnieje na Macu. Łączymy bez -i
                // (ssh spróbuje kluczy z agenta i domyślnych), zamiast kończyć się błędem.
                warnings.append("ssh.warn.winkey")
            } else {
                let path = expandTilde(key, home: homeDirectory)
                if fileExists(path) { args += ["-i", path] }
                else { warnings.append("ssh.warn.nokey") }
            }
        }

        for t in s.tunnels {
            let spec = t.trimmingCharacters(in: .whitespaces)
            if spec.isEmpty { continue }
            if isValidTunnel(spec) { args += ["-L", spec] } else { warnings.append("ssh.warn.tunnel") }
        }

        if s.port != 22 { args += ["-p", String(s.port)] }
        let user = s.username.trimmingCharacters(in: .whitespaces)
        if !user.isEmpty { args += ["-l", user] }
        // „--" chroni przed hostem zaczynającym się od „-" (traktowanym przez ssh jak opcja).
        args += ["--", s.host]
        var seen = Set<String>()
        return Launch(arguments: args, warnings: warnings.filter { seen.insert($0).inserted })
    }

    static func looksLikeWindowsPath(_ p: String) -> Bool {
        p.contains("\\") || (p.count >= 2 && p.dropFirst().first == ":" && p.first!.isLetter)
    }

    static func expandTilde(_ p: String, home: String) -> String {
        if p == "~" { return home }
        if p.hasPrefix("~/") { return home + String(p.dropFirst(1)) }
        return p
    }

    /// Składnia `ssh -L`: `[adres:]portLokalny:host:portZdalny` (host może być w [nawiasach] dla IPv6).
    public static func isValidTunnel(_ spec: String) -> Bool {
        let pattern = #"^(?:(?:\[[0-9A-Fa-f:.]+\]|[A-Za-z0-9.\-*]+):)?(\d{1,5}):(\[[0-9A-Fa-f:.]+\]|[A-Za-z0-9.\-_]+):(\d{1,5})$"#
        guard let re = try? NSRegularExpression(pattern: pattern),
              let m = re.firstMatch(in: spec, range: NSRange(spec.startIndex..., in: spec)) else { return false }
        for i in [1, 3] {
            guard let r = Range(m.range(at: i), in: spec), let n = Int(spec[r]), (1...65535).contains(n) else { return false }
        }
        return true
    }

    /// Środowisko procesu ssh: TERM dla pełnych kolorów, UTF-8, gniazdo agenta z sesji użytkownika.
    public static func environment(base: [String: String], extra: [String: String] = [:]) -> [String] {
        var env: [String: String] = [:]
        for k in ["HOME", "USER", "LOGNAME", "PATH", "SSH_AUTH_SOCK", "TMPDIR", "SHELL"] {
            if let v = base[k] { env[k] = v }
        }
        env["TERM"] = "xterm-256color"
        env["COLORTERM"] = "truecolor"
        env["LANG"] = base["LANG"].flatMap { $0.contains("UTF-8") ? $0 : nil } ?? "en_US.UTF-8"
        if env["PATH"] == nil { env["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin" }
        for (k, v) in extra { env[k] = v }
        return env.keys.sorted().map { "\($0)=\(env[$0]!)" }
    }
}
