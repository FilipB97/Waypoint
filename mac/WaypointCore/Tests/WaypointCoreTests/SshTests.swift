import Foundation
import Testing
@testable import WaypointCore

@Suite struct SshCommandTests {
    let home = "/Users/filip"

    @Test func podstawowePolecenie() {
        let s = Server(host: "web01.example.com", port: 22, username: "deploy", proto: .ssh)
        let l = SshCommand.build(s, homeDirectory: home, fileExists: { _ in true })
        #expect(l.arguments == ["-o", "ServerAliveInterval=30", "-o", "ServerAliveCountMax=4",
                                "-l", "deploy", "--", "web01.example.com"])
        #expect(l.warnings.isEmpty)
    }

    @Test func portKluczTunele() {
        let s = Server(host: "h", port: 2222, proto: .ssh, privateKeyPath: "~/.ssh/id_ed25519",
                       tunnels: ["8080:localhost:80", "127.0.0.1:5433:db.internal:5432", "zle"])
        let l = SshCommand.build(s, homeDirectory: home, fileExists: { $0 == "/Users/filip/.ssh/id_ed25519" })
        #expect(l.arguments.contains("-i"))
        #expect(l.arguments[l.arguments.firstIndex(of: "-i")! + 1] == "/Users/filip/.ssh/id_ed25519")
        #expect(l.arguments.filter { $0 == "-L" }.count == 2)
        #expect(l.arguments.suffix(4) == ["-p", "2222", "--", "h"])
        #expect(l.warnings == ["ssh.warn.tunnel"])
    }

    @Test func kluczZWindowsIBrakujacy() {
        var s = Server(host: "h", proto: .ssh, privateKeyPath: #"C:\Users\filip\.ssh\id_rsa"#)
        var l = SshCommand.build(s, homeDirectory: home, fileExists: { _ in true })
        #expect(!l.arguments.contains("-i"))
        #expect(l.warnings == ["ssh.warn.winkey"])

        s.privateKeyPath = "/nie/ma"
        l = SshCommand.build(s, homeDirectory: home, fileExists: { _ in false })
        #expect(!l.arguments.contains("-i"))
        #expect(l.warnings == ["ssh.warn.nokey"])
    }

    @Test func hostZMinusemNieJestOpcja() {
        let l = SshCommand.build(Server(host: "-oProxyCommand=evil", proto: .ssh), homeDirectory: home, fileExists: { _ in true })
        #expect(l.arguments.suffix(2) == ["--", "-oProxyCommand=evil"])
    }

    @Test(arguments: [
        ("8080:localhost:80", true), ("127.0.0.1:8080:db:5432", true), ("[::1]:8080:db:5432", true),
        ("8080:[fe80::1]:22", true), ("*:8080:h:80", true),
        ("0:h:80", false), ("8080:h:70000", false), ("8080:h", false), ("a:b:c", false), ("8080 :h:80", false),
    ])
    func tunele(spec: String, ok: Bool) {
        #expect(SshCommand.isValidTunnel(spec) == ok)
    }

    @Test func srodowisko() {
        let env = SshCommand.environment(base: ["HOME": "/Users/f", "SSH_AUTH_SOCK": "/tmp/agent", "SECRET": "x", "LANG": "pl_PL"],
                                         extra: ["SSH_ASKPASS": "/A"])
        #expect(env.contains("TERM=xterm-256color"))
        #expect(env.contains("SSH_AUTH_SOCK=/tmp/agent"))
        #expect(env.contains("LANG=en_US.UTF-8"))      // pl_PL bez UTF-8 → zastąpione
        #expect(env.contains("SSH_ASKPASS=/A"))
        #expect(!env.contains { $0.hasPrefix("SECRET=") })   // nie przenosimy całego środowiska aplikacji
    }
}

@Suite struct AskpassTests {
    @Test(arguments: [
        ("deploy@web01's password: ", Askpass.Kind.password),
        ("(deploy@web01) Password: ", .password),
        ("Password:", .password),
        ("Enter passphrase for key '/Users/f/.ssh/id_ed25519': ", .passphrase),
        ("The authenticity of host 'h (1.2.3.4)' can't be established.\nED25519 key fingerprint is SHA256:abc.\nAre you sure you want to continue connecting (yes/no/[fingerprint])? ", .confirm),
        ("Verification code: ", .other),
        ("password expired, change now? ", .other),
    ])
    func rodzajPytania(prompt: String, kind: Askpass.Kind) {
        #expect(Askpass.classify(prompt) == kind)
    }

    @Test func podpowiedzConfirmWygrywa() {
        #expect(Askpass.classify("Allow use of key?", promptHint: "confirm") == .confirm)
    }

    @Test func pelnaWymianaPrzezGniazdo() throws {
        let server = try AskpassServer { req, reply in
            reply(Askpass.Reply(value: req.prompt.contains("password") ? "tajne-hasło" : nil))
        }
        defer { server.stop() }
        let env = server.environment(askpassExecutable: "/x")
        #expect(env["SSH_ASKPASS_REQUIRE"] == "force")

        var out: [String] = []
        let code = Askpass.runClient(arguments: ["askpass", "deploy@h's", "password:"], environment: env) { out.append($0) }
        #expect(code == 0)
        #expect(out == ["tajne-hasło"])

        // Anulowanie (nil) → kod błędu, nic na stdout.
        out = []
        #expect(Askpass.runClient(arguments: ["askpass", "Verification code:"], environment: env) { out.append($0) } == 1)
        #expect(out.isEmpty)
    }

    @Test func odpowiedzZInnegoWatkuPoChwili() throws {
        let server = try AskpassServer { _, reply in
            DispatchQueue.global().asyncAfter(deadline: .now() + 0.2) { reply(Askpass.Reply(value: "yes")) }
        }
        defer { server.stop() }
        var out: [String] = []
        #expect(Askpass.runClient(arguments: ["a", "continue connecting (yes/no)?"],
                                  environment: server.environment(askpassExecutable: "/x")) { out.append($0) } == 0)
        #expect(out == ["yes"])
    }

    @Test func zlyTokenJestOdrzucany() throws {
        let server = try AskpassServer { _, reply in reply(Askpass.Reply(value: "nie-powinno-wyjść")) }
        defer { server.stop() }
        var env = server.environment(askpassExecutable: "/x")
        env[Askpass.tokenEnv] = "zgadywany"
        var out: [String] = []
        #expect(Askpass.runClient(arguments: ["a", "Password:"], environment: env) { out.append($0) } == 1)
        #expect(out.isEmpty)
    }

    @Test func gniazdoPrywatneISprzatane() throws {
        let server = try AskpassServer { _, reply in reply(Askpass.Reply(value: nil)) }
        let attrs = try FileManager.default.attributesOfItem(atPath: (server.socketPath as NSString).deletingLastPathComponent)
        #expect((attrs[.posixPermissions] as? Int) == 0o700)
        server.stop()
        #expect(!FileManager.default.fileExists(atPath: server.socketPath))
    }

    /// Regresja: zatrzymany serwer nie może przejąć połączenia do nowego, który dostał ten sam numer
    /// deskryptora (tak wygląda zamknięcie karty i otwarcie kolejnej).
    @Test func zatrzymanySerwerNiePrzejmujePolaczenNowego() throws {
        for i in 0..<40 {
            let old = try AskpassServer { _, reply in reply(Askpass.Reply(value: "stary")) }
            old.stop()
            let fresh = try AskpassServer { _, reply in reply(Askpass.Reply(value: "nowy-\(i)")) }
            defer { fresh.stop() }
            var out: [String] = []
            let code = Askpass.runClient(arguments: ["a", "Password:"], environment: fresh.environment(askpassExecutable: "/x")) { out.append($0) }
            #expect(code == 0 && out == ["nowy-\(i)"], "próba \(i)")
        }
    }

    @Test func bezZmiennychTrybAskpassKonczySieBledem() {
        #expect(Askpass.runClient(arguments: ["a", "Password:"], environment: [:]) { _ in } == 2)
    }
}
