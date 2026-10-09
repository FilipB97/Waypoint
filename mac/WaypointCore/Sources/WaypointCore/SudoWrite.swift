import Foundation

/// Zapis pliku, do którego zalogowany użytkownik nie ma prawa (np. /etc/nginx/nginx.conf), przez `sudo`.
///
/// 1. Treść trafia przez SFTP do pliku tymczasowego użytkownika w /tmp (0600 — inni jej nie przeczytają).
/// 2. Jedno polecenie `ssh … sudo -S sh -c '…'` po stronie serwera: plik tymczasowy obok prawdziwego,
///    właściciel i uprawnienia skopiowane z oryginału (`chown/chmod --reference`, GNU), treść,
///    atomowa podmiana `mv`. Tam, gdzie tak się nie da (system bez GNU coreutils, katalog bez miejsca
///    na plik obok) — nadpisanie w miejscu `cat > plik`, które zachowuje właściciela i uprawnienia.
///    Cała operacja wykonuje się na serwerze, więc zerwane połączenie nie przerwie jej w połowie zapisu.
/// 3. Plik tymczasowy z /tmp jest usuwany.
///
/// Hasło sudo idzie na stdin (`-S`), nigdy w linii poleceń. Pusty prompt (`-p ''`) — żeby jego treść
/// nie mieszała się z wynikiem.
public enum SudoWrite {
    public enum Outcome: Equatable, Sendable {
        case atomicReplace
        case inPlace
    }

    public enum Failure: Error, Equatable, Sendable, LocalizedError {
        /// sudo odrzuciło hasło albo użytkownik nie ma uprawnień sudo.
        case sudoDenied(String)
        /// Polecenie się nie powiodło — plik docelowy nietknięty (nie doszło do zapisu).
        case failed(String)

        public var errorDescription: String? {
            switch self {
            case .sudoDenied(let m), .failed(let m): return m
            }
        }
    }

    /// Skrypt wykonywany jako root. Argumenty: $1 = plik z treścią, $2 = plik docelowy.
    /// Wypisuje ATOMIC albo INPLACE; kod 3 = zapis w miejscu się nie powiódł (plik mógł ucierpieć).
    public static let script = """
    set -u
    src=$1; dst=$2
    [ -f "$dst" ] || { echo "brak pliku: $dst" >&2; exit 2; }
    dir=$(dirname -- "$dst"); base=$(basename -- "$dst")
    if tmp=$(mktemp "$dir/.$base.waypoint-XXXXXXXX" 2>/dev/null); then
      if chown --reference="$dst" -- "$tmp" 2>/dev/null && chmod --reference="$dst" -- "$tmp" 2>/dev/null \\
         && cat -- "$src" > "$tmp" && mv -f -- "$tmp" "$dst"; then
        echo ATOMIC; exit 0
      fi
      rm -f -- "$tmp"
    fi
    if cat -- "$src" > "$dst"; then echo INPLACE; exit 0; fi
    exit 3
    """

    /// Polecenie zdalne (jeden napis — ssh przekazuje je powłoce użytkownika na serwerze).
    public static func remoteCommand(source: String, destination: String) -> String {
        "sudo -S -p '' -- sh -c \(shellQuote(script)) waypoint-sudo \(shellQuote(source)) \(shellQuote(destination))"
    }

    /// Cytowanie dla powłoki POSIX: '…' z podmianą ' na '\''.
    public static func shellQuote(_ s: String) -> String {
        "'" + s.replacingOccurrences(of: "'", with: "'\\''") + "'"
    }

    /// Interpretacja wyniku polecenia.
    public static func interpret(_ r: SshExec.Result) -> Swift.Result<Outcome, Failure> {
        let out = r.stdoutText
        if r.status == 0 && out.contains("ATOMIC") { return .success(.atomicReplace) }
        if r.status == 0 && out.contains("INPLACE") { return .success(.inPlace) }
        let err = r.stderrText.trimmingCharacters(in: .whitespacesAndNewlines)
        let low = err.lowercased()
        if low.contains("incorrect password") || low.contains("sorry, try again") || low.contains("is not in the sudoers")
            || low.contains("not allowed to execute") || low.contains("a password is required") || low.contains("no password was provided") {
            return .failure(.sudoDenied(err))
        }
        return .failure(.failed(err.isEmpty ? "kod \(r.status)" : err))
    }

    /// Ścieżka pliku tymczasowego w /tmp dla treści (losowa, tworzona przez SFTP z EXCL i 0600).
    public static func stagingPath() -> String {
        "/tmp/.waypoint-sudo-" + UUID().uuidString.replacingOccurrences(of: "-", with: "").prefix(12).lowercased()
    }

    /// Cały zapis: treść do /tmp przez SFTP, polecenie sudo przez `exec`, sprzątanie.
    /// `exec(command, stdin)` uruchamia polecenie na serwerze (w aplikacji: ssh z tymi samymi opcjami).
    public static func write(_ c: SftpClient, content: Data, destination: String, password: String?,
                             exec: (String, Data) throws -> SshExec.Result) throws -> Outcome {
        let staging = stagingPath()
        try c.writeFile(staging, data: content, flags: [.write, .create, .exclusive], attrs: SftpAttributes(permissions: 0o600))
        defer { try? c.remove(staging) }
        let stdin = password.map { Data(($0 + "\n").utf8) } ?? Data()
        let r = try exec(remoteCommand(source: staging, destination: destination), stdin)
        switch interpret(r) {
        case .success(let o): return o
        case .failure(let f): throw f
        }
    }
}
