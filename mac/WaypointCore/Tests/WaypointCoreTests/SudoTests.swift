import Foundation
import Testing
@testable import WaypointCore

@Suite struct SudoWriteUnitTests {
    @Test func cytowaniePowloki() {
        #expect(SudoWrite.shellQuote("a b") == "'a b'")
        #expect(SudoWrite.shellQuote("it's") == "'it'\\''s'")
        #expect(SudoWrite.shellQuote("$(rm -rf /)") == "'$(rm -rf /)'")
    }

    @Test func wynik() {
        func r(_ code: Int32, _ out: String, _ err: String = "") -> SshExec.Result {
            SshExec.Result(status: code, stdout: Data(out.utf8), stderr: Data(err.utf8))
        }
        #expect(SudoWrite.interpret(r(0, "ATOMIC\n")) == .success(.atomicReplace))
        #expect(SudoWrite.interpret(r(0, "INPLACE\n")) == .success(.inPlace))
        #expect(SudoWrite.interpret(r(1, "", "Sorry, try again.\nsudo: 3 incorrect password attempts")) == .failure(.sudoDenied("Sorry, try again.\nsudo: 3 incorrect password attempts")))
        #expect(SudoWrite.interpret(r(1, "", "wpt is not in the sudoers file.")) == .failure(.sudoDenied("wpt is not in the sudoers file.")))
        #expect(SudoWrite.interpret(r(2, "", "brak pliku: /x")) == .failure(.failed("brak pliku: /x")))
    }

    @Test func polecenieZdalne() {
        let c = SudoWrite.remoteCommand(source: "/tmp/.w", destination: "/etc/it's here.conf")
        #expect(c.hasPrefix("sudo -S -p '' -- sh -c '"))
        #expect(c.hasSuffix("waypoint-sudo '/tmp/.w' '/etc/it'\\''s here.conf'"))
    }
}

/// Na prawdziwym serwerze z sudo: WAYPOINT_SFTP_TEST + WAYPOINT_SUDO_PASSWORD, lokalnie jako root
/// (przygotowanie plików roota).
@Suite(.enabled(if: ProcessInfo.processInfo.environment["WAYPOINT_SFTP_TEST"] != nil
                && ProcessInfo.processInfo.environment["WAYPOINT_SUDO_PASSWORD"] != nil && getuid() == 0),
       .serialized)
struct SudoWriteIntegrationTests {
    static func exec(_ command: String, _ stdin: Data) throws -> SshExec.Result {
        let t = SftpIntegrationTests.target
        let s = Server(host: t.host, port: t.port, username: t.user, proto: .ssh, privateKeyPath: t.key)
        let l = SshCommand.buildExec(s, command: command, homeDirectory: NSHomeDirectory(), fileExists: { FileManager.default.fileExists(atPath: $0) })
        let extra = ["-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=accept-new", "-o", "UserKnownHostsFile=\(t.knownHosts)"]
        return try SshExec.run(executable: "/usr/bin/ssh", arguments: extra + l.arguments,
                               environment: ["PATH": "/usr/bin:/bin", "HOME": NSHomeDirectory()], stdin: stdin)
    }

    @Test func zapisPlikuRoota() throws {
        let dir = "/srv/wp-sudo-\(UUID().uuidString.prefix(6))"
        let f = "\(dir)/nginx it's.conf"
        SafeWriteIntegrationTests.sh("mkdir -p \(dir) && printf 'stare\\n' > \"\(f)\" && chown root:adm \"\(f)\" && chmod 640 \"\(f)\" && chmod 755 \(dir)")
        defer { SafeWriteIntegrationTests.sh("rm -rf \(dir)") }
        let pw = ProcessInfo.processInfo.environment["WAYPOINT_SUDO_PASSWORD"]!

        let c = try SftpIntegrationTests.connect()
        defer { c.close() }

        // Bez sudo: odmowa, plik nietknięty.
        let info = try SafeWrite.stat(c, f)
        #expect(throws: SafeWriteError.self) { _ = try SafeWrite.write(c, content: Data("x".utf8), original: info) }

        // Złe hasło — sudoDenied, plik nietknięty, bez resztek w /tmp.
        #expect(throws: SudoWrite.Failure.self) {
            _ = try SudoWrite.write(c, content: Data("ZLE\n".utf8), destination: f, password: "zle-haslo", exec: Self.exec)
        }
        #expect(SafeWriteIntegrationTests.shOut("cat \"\(f)\"") == "stare")

        // Poprawne — podmiana atomowa, właściciel i uprawnienia zachowane.
        let out = try SudoWrite.write(c, content: Data("nowe\nlinia\n".utf8), destination: f, password: pw, exec: Self.exec)
        #expect(out == .atomicReplace)
        #expect(SafeWriteIntegrationTests.shOut("cat \"\(f)\"") == "nowe\nlinia")
        #expect(SafeWriteIntegrationTests.shOut("stat -c '%U:%G %a' \"\(f)\"") == "root:adm 640")
        #expect(SafeWriteIntegrationTests.shOut("ls -A \(dir) | grep -c waypoint || true") == "0")
        #expect(SafeWriteIntegrationTests.shOut("ls -A /tmp | grep -c waypoint-sudo || true") == "0")
    }
}
