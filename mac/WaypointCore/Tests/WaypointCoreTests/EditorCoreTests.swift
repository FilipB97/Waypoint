import Foundation
import Testing
@testable import WaypointCore

/// Te same przypadki co FileEditorCoreTests w wersji Windows — plik ma się zachowywać identycznie.
@Suite struct TextFileFormatTests {
    @Test func crlfEdytorDostajeLfZapisOdtwarzaCrlf() {
        let (f, text) = TextFileFormat.detect(Data("a\r\nb\r\n".utf8))
        #expect(text == "a\nb\n")
        #expect(f.eol == .crlf)
        #expect(f.encode(text + "c\n") == .ok(Data("a\r\nb\r\nc\r\n".utf8)))
    }

    @Test func wklejonyCrlfNieWchodziDoPlikuLinuksowego() {
        let (f, text) = TextFileFormat.detect(Data("#!/bin/sh\necho\n".utf8))
        guard case .ok(let d) = f.encode(text + "ls\r\n") else { Issue.record("encode"); return }
        #expect(!d.contains(0x0D))
    }

    @Test(arguments: [
        [UInt8]("x\r\ny\r\n".utf8),
        [0xEF, 0xBB, 0xBF, 0x61, 0x0A],
        [0xFF, 0xFE, 0x61, 0x00, 0x0A, 0x00],
        [0xFE, 0xFF, 0x00, 0x61, 0x00, 0x0A],
        [0x63, 0x61, 0x66, 0xE9, 0x0A],
        [UInt8]("zażółć\n".utf8),
        [],
    ])
    func bezZmianTeSameBajty(bytes: [UInt8]) {
        let (f, text) = TextFileFormat.detect(Data(bytes))
        #expect(f.encode(text) == .ok(Data(bytes)), "\(f.encodingName)")
    }

    @Test func latin1ZnakSpozaKodowania() {
        let (f, text) = TextFileFormat.detect(Data([0x63, 0x61, 0x66, 0xE9, 0x0A]))
        #expect(f.encodingName == TextFileFormat.latin1)
        #expect(f.encode(text + "ł\n") == .unencodable("ł"))
        #expect(f.asUTF8().encode(text + "ł\n") == .ok(Data("café\nł\n".utf8)))
    }

    @Test func mieszaneKonceLinii() {
        let (f, _) = TextFileFormat.detect(Data("a\r\nb\r\nc\n".utf8))
        #expect(f.eol == .crlf && f.mixedEol)
        #expect(!TextFileFormat.detect(Data("jedna linia".utf8)).0.mixedEol)
    }

    @Test func binarne() {
        #expect(TextFileFormat.looksBinary(Data([0x7B, 0x00, 0x7D])))
        #expect(!TextFileFormat.looksBinary(Data("zwykły tekst\n".utf8)))
        #expect(!TextFileFormat.looksBinary(Data([0xFF, 0xFE, 0x61, 0x00])))   // UTF-16 z BOM ma zera, ale to tekst
        #expect(!TextFileFormat.looksBinary(Data()))
    }
}

@Suite struct EditorLanguageTests {
    @Test(arguments: [
        ("/etc/nginx/nginx.conf", nil, "ini"), ("docker-compose.yml", nil, "yaml"), ("/srv/app/Dockerfile", nil, "dockerfile"),
        ("Dockerfile.prod", nil, "dockerfile"), (".bashrc", nil, "shell"), (".env", nil, "ini"),
        ("deploy", "#!/bin/bash -e", "shell"), ("tool", "#!/usr/bin/env python3", "python"),
        ("tool", "#!/usr/bin/env -S python3.12 -u", "python"), ("notes", "zwykły tekst", "plaintext"),
        ("appsettings.JSON", nil, "json"),
    ] as [(String, String?, String)])
    func jezyk(name: String, first: String?, expected: String) {
        #expect(EditorLanguage.language(for: name, firstLine: first) == expected)
    }
}

@Suite struct RemoteFileInfoTests {
    func info(_ mode: UInt32, uid: UInt32, len: UInt64 = 10, mtime: UInt32 = 100) -> RemoteFileInfo {
        RemoteFileInfo(path: "/x", length: len, modified: mtime, mode: mode, uid: uid, gid: uid)
    }

    @Test func zmianaPliku() {
        #expect(!RemoteFileInfo.changedSince(info(0o644, uid: 1), info(0o644, uid: 1)))
        #expect(RemoteFileInfo.changedSince(info(0o644, uid: 1), info(0o644, uid: 1, len: 11)))
        #expect(RemoteFileInfo.changedSince(info(0o644, uid: 1), info(0o644, uid: 1, mtime: 101)))
    }

    @Test func przewidywanieZapisu() {
        #expect(info(0o644, uid: 0).likelyWritable(by: 1000) == false)
        #expect(info(0o600, uid: 1000).likelyWritable(by: 1000) == true)
        #expect(info(0o444, uid: 1000).likelyWritable(by: 1000) == false)
        #expect(info(0o664, uid: 33).likelyWritable(by: 1000) == nil)
        #expect(info(0o666, uid: 0).likelyWritable(by: 1000) == true)
        #expect(info(0o600, uid: 33).likelyWritable(by: 0) == true)
    }
}

/// Macierz bezpiecznego zapisu na prawdziwym OpenSSH — ta sama co przy wersji Windows (#224).
/// Wymaga WAYPOINT_SFTP_TEST i uprawnień roota lokalnie (zakłada pliki roota i grupę „web").
@Suite(.enabled(if: ProcessInfo.processInfo.environment["WAYPOINT_SFTP_TEST"] != nil && getuid() == 0),
       .serialized)
struct SafeWriteIntegrationTests {
    // popen zamiast Process: na Linuksie wątek Foundation pilnujący procesów potomnych blokuje się
    // na wait4() działającego procesu ssh (transport SFTP), więc Process.waitUntilExit() krótkiej
    // komendy nigdy by nie wrócił. To pułapka samego testu — aplikacja nie uruchamia takich komend.
    @discardableResult
    static func shOut(_ cmd: String) -> String {
        #if os(macOS)
        let p = Process(), pipe = Pipe()
        p.executableURL = URL(fileURLWithPath: "/bin/sh")
        p.arguments = ["-c", cmd]
        p.standardOutput = pipe
        try? p.run()
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        return String(decoding: data, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
        #else
        guard let f = popen(cmd + " 2>/dev/null", "r") else { return "" }
        var out = Data()
        var buf = [UInt8](repeating: 0, count: 4096)
        while true {
            let n = fread(&buf, 1, buf.count, f)
            if n <= 0 { break }
            out.append(contentsOf: buf[0..<n])
        }
        pclose(f)
        return String(decoding: out, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
        #endif
    }

    static func sh(_ cmd: String) { shOut(cmd) }

    @Test func macierzPrzypadkow() throws {
        let user = SftpIntegrationTests.target.user
        let base = "/home/\(user)/wp-safe-\(UUID().uuidString.prefix(6))"
        let ro = "/srv/wp-ro-\(UUID().uuidString.prefix(6))"
        Self.sh("""
            set -e; getent group web >/dev/null || groupadd web; usermod -aG web \(user)
            mkdir -p \(base)/site \(base)/bin \(base)/keys \(base)/www \(ro)
            echo stare > \(base)/site/real.conf; ln -s real.conf \(base)/site/link.conf
            printf '#!/bin/sh\\necho A\\n' > \(base)/bin/run.sh; chmod 755 \(base)/bin/run.sh
            echo SEKRET > \(base)/keys/id; chmod 600 \(base)/keys/id
            echo '<h1>A</h1>' > \(base)/www/page.html; chmod 664 \(base)/www/page.html
            chown -R \(user):\(user) \(base); chgrp web \(base)/www/page.html
            echo roota > \(base)/rootowned.conf; chown root:root \(base)/rootowned.conf; chmod 644 \(base)/rootowned.conf
            echo otwarte > \(ro)/open.txt; chmod 666 \(ro)/open.txt; chmod 755 \(ro)
            """)
        defer { Self.sh("rm -rf \(base) \(ro)") }

        let c = try SftpIntegrationTests.connect()
        defer { c.close() }
        func save(_ p: String, _ text: String) throws -> SafeWriteMode {
            try SafeWrite.write(c, content: Data(text.utf8), original: try SafeWrite.stat(c, p))
        }

        // A) dowiązanie — podmiana pliku docelowego, dowiązanie zostaje
        #expect(try save("\(base)/site/link.conf", "NOWE-A\n") == .atomicReplace)
        #expect(Self.shOut("readlink \(base)/site/link.conf") == "real.conf")
        #expect(Self.shOut("cat \(base)/site/real.conf") == "NOWE-A")
        // B, C) uprawnienia zachowane
        #expect(try save("\(base)/bin/run.sh", "#!/bin/sh\necho B\n") == .atomicReplace)
        #expect(Self.shOut("stat -c %a \(base)/bin/run.sh") == "755")
        #expect(try save("\(base)/keys/id", "NOWY\n") == .atomicReplace)
        #expect(Self.shOut("stat -c %a \(base)/keys/id") == "600")
        // D) grupa zachowana (wpt należy do web — chgrp na pliku tymczasowym się udaje)
        #expect(try save("\(base)/www/page.html", "<h1>D</h1>\n") == .atomicReplace)
        #expect(Self.shOut("stat -c %G \(base)/www/page.html") == "web")
        // F) plik roota w katalogu użytkownika — uczciwa odmowa, właściciel i treść nietknięte
        do {
            _ = try save("\(base)/rootowned.conf", "PRZEJETY\n")
            Issue.record("zapis pliku roota nie powinien przejść")
        } catch let e as SafeWriteError {
            #expect(!e.originalMayBeDamaged)
            #expect(e.isPermissionDenied)
        }
        #expect(Self.shOut("stat -c %U \(base)/rootowned.conf") == "root")
        #expect(Self.shOut("cat \(base)/rootowned.conf") == "roota")
        // G) katalog roota + plik 0666 — w miejscu, właściciel root zostaje
        #expect(try save("\(ro)/open.txt", "NOWE-G\n") == .inPlace(reasonKey: "edit.fb.dir"))
        #expect(Self.shOut("stat -c %U \(ro)/open.txt") == "root")
        #expect(Self.shOut("cat \(ro)/open.txt") == "NOWE-G")
        // Po zapisach nie zostają pliki tymczasowe
        #expect(Self.shOut("ls -A \(base)/site \(base)/bin | grep -c waypoint || true") == "0")
    }
}
