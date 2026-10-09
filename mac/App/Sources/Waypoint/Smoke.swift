import AppKit
import SwiftTerm
import WaypointCore

/// Test dymny dla CI (runner macOS nie ma człowieka, który by kliknął): przy WAYPOINT_SMOKE_DIR
/// aplikacja sama przechodzi przez główne ekrany, łączy się z lokalnym sshd i zapisuje zrzuty okna,
/// zawartość terminala oraz wynik. Bez tej zmiennej nic się nie dzieje.
///
/// Scenariusz (serwery z WAYPOINT_DATA_DIR, przygotowane przez workflow):
///  1. lista + szczegóły, arkusz edycji;
///  2. serwer z hasłem w Pęku kluczy → logowanie bez pytania (askpass + Keychain), komenda w powłoce;
///  3. serwer bez zapisanego hasła → karta z pytaniem o hasło (zrzut), odpowiedź, logowanie.
@MainActor
enum Smoke {
    static func startIfRequested() {
        guard let dir = ProcessInfo.processInfo.environment["WAYPOINT_SMOKE_DIR"] else { return }
        let out = URL(fileURLWithPath: dir, isDirectory: true)
        try? FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)
        Task { await run(out) }
    }

    private static var log: [String] = []

    private static func note(_ s: String, _ out: URL) {
        log.append(s)
        try? log.joined(separator: "\n").write(to: out.appendingPathComponent("smoke.log"), atomically: true, encoding: .utf8)
    }

    private static func run(_ out: URL) async {
        let model = AppModel.shared
        let env = ProcessInfo.processInfo.environment
        var ok = true

        guard let window = await waitFor(10, { NSApp.windows.first { $0.isVisible && !($0 is NSPanel) } }) else {
            note("FAIL: brak okna", out); finish(out, ok: false); return
        }
        window.setContentSize(NSSize(width: 980, height: 640))   // ekran runnera ma 1024×768
        window.center()
        NSApp.activate(ignoringOtherApps: true)
        note("okno: \(window.frame.size), serwery: \(model.servers.count)", out)

        // 1. Lista + szczegóły + edytor
        model.selection = model.servers.first?.id
        try? await Task.sleep(for: .seconds(1.5))
        snapshot(window, out.appendingPathComponent("01-lista.png"))
        if let s = model.servers.first {
            model.beginEdit(s)
            try? await Task.sleep(for: .seconds(1.5))
            if let sheet = window.attachedSheet { snapshot(sheet, out.appendingPathComponent("02-edytor.png")) }
            else { note("uwaga: brak arkusza edycji", out) }
            model.editing = nil
            try? await Task.sleep(for: .seconds(1))
        }

        // 2. Logowanie hasłem z Pęku kluczy. Hasło zapisuje sama aplikacja (jak po „Zapisz w Pęku kluczy")
        //    — wpis dodany innym programem (np. `security`) wywołałby systemowe pytanie o dostęp.
        if let id = env["WAYPOINT_SMOKE_KEYCHAIN_SERVER"], let s = model.servers.first(where: { $0.id == id }),
           let password = env["WAYPOINT_SMOKE_PASSWORD"] {
            note(Keychain.save(password, for: id, label: "Waypoint — smoke") ? "hasło zapisane w Pęku kluczy" : "FAIL: zapis do Pęku kluczy", out)
            defer { Keychain.delete(for: id) }
            model.connect(s)
            let session = model.activeSession!
            let prompted = await waitFor(15, { session.prompt != nil ? true : (bufferText(session).contains("$ ") || bufferText(session).contains("% ") ? true : nil) })
            if session.prompt != nil {
                note("FAIL: pytanie o hasło mimo hasła w Pęku kluczy: \(session.prompt!.text)", out)
                snapshot(window, out.appendingPathComponent("03-nieoczekiwane-pytanie.png"))
                session.prompt?.answer(nil, false)
                ok = false
            } else if prompted == nil {
                note("FAIL: brak znaku zachęty powłoki po 15 s", out); ok = false
            }
            session.view.send(txt: "echo WAYPOINT_SMOKE_$((40+2)); uname -sm\r")
            let done = await waitFor(8, { bufferText(session).contains("WAYPOINT_SMOKE_42") ? true : nil })
            try? await Task.sleep(for: .seconds(0.7))   // ostatnie wiersze muszą zdążyć się narysować
            let t = session.view.getTerminal()
            note("terminal: \(t.cols)×\(t.rows), widok \(Int(session.view.frame.width))×\(Int(session.view.frame.height))", out)
            if t.rows < 10 || t.cols < 40 { note("FAIL: terminal za mały", out); ok = false }
            snapshot(window, out.appendingPathComponent("03-terminal.png"))
            try? bufferText(session).write(to: out.appendingPathComponent("terminal-keychain.txt"), atomically: true, encoding: .utf8)
            note(done == true ? "OK: logowanie z Pęku kluczy i komenda" : "FAIL: komenda nie wykonała się", out)
            ok = ok && done == true
        }

        // 3. Pytanie o hasło w karcie
        if let id = env["WAYPOINT_SMOKE_PROMPT_SERVER"], let s = model.servers.first(where: { $0.id == id }),
           let password = env["WAYPOINT_SMOKE_PASSWORD"] {
            model.connect(s)
            let session = model.activeSession!
            let p = await waitFor(15, { session.prompt })
            if let p {
                try? await Task.sleep(for: .seconds(0.8))
                snapshot(window, out.appendingPathComponent("04-pytanie-o-haslo.png"))
                note("pytanie: \(p.kind) — \(p.text.trimmingCharacters(in: .whitespacesAndNewlines))", out)
                p.answer(password, false)
                session.view.send(txt: "echo WAYPOINT_PROMPT_$((40+2))\r")
                let done = await waitFor(10, { bufferText(session).contains("WAYPOINT_PROMPT_42") ? true : nil })
                try? await Task.sleep(for: .seconds(0.7))
                snapshot(window, out.appendingPathComponent("05-po-zalogowaniu.png"))
                try? bufferText(session).write(to: out.appendingPathComponent("terminal-prompt.txt"), atomically: true, encoding: .utf8)
                note(done == true ? "OK: logowanie hasłem wpisanym w karcie" : "FAIL: brak logowania po podaniu hasła", out)
                ok = ok && done == true
            } else {
                note("FAIL: nie pojawiło się pytanie o hasło", out)
                snapshot(window, out.appendingPathComponent("04-brak-pytania.png"))
                try? bufferText(session).write(to: out.appendingPathComponent("terminal-prompt.txt"), atomically: true, encoding: .utf8)
                ok = false
            }
        }

        // 4. RDP: plik .rdp powstaje, a bez Windows App (runner jej nie ma) — komunikat z App Store.
        if let rdp = model.servers.first(where: { $0.proto == .rdp }) {
            model.activeSessionID = nil
            model.selection = rdp.id
            try? await Task.sleep(for: .seconds(0.8))
            snapshot(window, out.appendingPathComponent("07-rdp-szczegoly.png"))
            model.connect(rdp)
            let shown = await waitFor(5, { model.alert != nil || model.toast != nil ? true : nil })
            try? await Task.sleep(for: .seconds(0.8))
            snapshot(window, out.appendingPathComponent("08-rdp-polacz.png"))
            let file = RdpLauncher.directory.appendingPathComponent(RdpFile.fileName(for: rdp))
            let content = (try? String(contentsOf: file, encoding: .utf8)) ?? ""
            try? content.write(to: out.appendingPathComponent("rdp-plik.rdp"), atomically: true, encoding: .utf8)
            let fileOK = content.contains("full address:s:\(rdp.host)")
            note(fileOK && shown == true ? "OK: RDP — plik .rdp i komunikat (\(RdpLauncher.isWindowsAppInstalled ? "Windows App" : "brak Windows App"))"
                                         : "FAIL: RDP plik=\(fileOK) komunikat=\(shown == true)", out)
            ok = ok && fileOK && shown == true
            model.alert = nil
            if let s = model.servers.first(where: { $0.proto == .rdp }) {
                model.beginEdit(s)
                try? await Task.sleep(for: .seconds(1.2))
                if let sheet = window.attachedSheet { snapshot(sheet, out.appendingPathComponent("09-rdp-edytor.png")) }
                model.editing = nil
                try? await Task.sleep(for: .seconds(0.8))
            }
        }

        // Kilka kart: zrzut paska kart z aktywną pierwszą.
        model.activateTab(0)
        try? await Task.sleep(for: .seconds(1))
        snapshot(window, out.appendingPathComponent("06-karty.png"))
        finish(out, ok: ok)
    }

    private static func finish(_ out: URL, ok: Bool) {
        try? (ok ? "OK" : "FAIL").write(to: out.appendingPathComponent("result.txt"), atomically: true, encoding: .utf8)
        for s in AppModel.shared.sessions { s.close() }
        exit(ok ? 0 : 1)
    }

    static func bufferText(_ s: TerminalSession) -> String {
        String(decoding: s.view.getTerminal().getBufferAsData(), as: UTF8.self)
    }

    private static func waitFor<T>(_ seconds: Double, _ check: () -> T?) async -> T? {
        let end = Date().addingTimeInterval(seconds)
        while Date() < end {
            if let v = check() { return v }
            try? await Task.sleep(for: .milliseconds(200))
        }
        return check()
    }

    /// Zrzut samego okna przez systemowe `screencapture -l` — obejmuje też zawartość terminala, której
    /// zrzut z wnętrza aplikacji (cacheDisplay) nie łapie, bo SwiftTerm rysuje na warstwach.
    static func snapshot(_ window: NSWindow, _ url: URL) {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
        p.arguments = ["-x", "-o", "-l", String(window.windowNumber), url.path]
        try? p.run()
        p.waitUntilExit()
    }
}
