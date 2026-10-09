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
            keychainServerID = id
            note(Keychain.save(password, for: id, label: "Waypoint — smoke") ? "hasło zapisane w Pęku kluczy" : "FAIL: zapis do Pęku kluczy", out)
            model.connect(s)
            let session = model.activeSession!.terminal!
            let prompted = await waitFor(15, { session.auth.prompt != nil ? true : (bufferText(session).contains("$ ") || bufferText(session).contains("% ") ? true : nil) })
            if session.auth.prompt != nil {
                note("FAIL: pytanie o hasło mimo hasła w Pęku kluczy: \(session.auth.prompt!.text)", out)
                snapshot(window, out.appendingPathComponent("03-nieoczekiwane-pytanie.png"))
                session.auth.prompt?.answer(nil, false)
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
            let session = model.activeSession!.terminal!
            let p = await waitFor(15, { session.auth.prompt })
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

        // 4b. Snippety, paleta, czcionka: snippet ze zmiennymi serwera idzie do terminala A.
        if model.sessions.count >= 1, let term = model.sessions.first?.terminal {
            model.activateTab(0)
            model.saveSnippets([CommandSnippet(name: "Kto i gdzie", command: "echo SNIP_{user}_{port}_$((40+2))")] + model.snippets)
            try? await Task.sleep(for: .seconds(0.5))
            model.sendSnippet(at: 0)
            let got = await waitFor(8, { bufferText(term).contains("SNIP_\(term.server.username)_\(term.server.port)_42") ? true : nil })
            note(got == true ? "OK: snippet ze zmiennymi wysłany do terminala" : "FAIL: snippet nie dotarł", out)
            ok = ok && got == true
            let before = term.view.getTerminal().cols
            model.zoomTerminal(2)
            try? await Task.sleep(for: .seconds(0.8))
            let after = term.view.getTerminal().cols
            note(after < before ? "OK: powiększenie czcionki (\(before) → \(after) kolumn)" : "FAIL: czcionka bez zmian (\(before) → \(after))", out)
            ok = ok && after < before
            model.zoomTerminal(0)
            model.openPalette(seed: "root@db.example")
            try? await Task.sleep(for: .seconds(1.2))
            if let sheet = window.attachedSheet { snapshot(sheet, out.appendingPathComponent("14-paleta.png")) }
            model.paletteOpen = false
            try? await Task.sleep(for: .seconds(0.8))
            model.snippetPickerOpen = true
            try? await Task.sleep(for: .seconds(1.2))
            if let sheet = window.attachedSheet { snapshot(sheet, out.appendingPathComponent("15-snippety.png")) }
            model.snippetPickerOpen = false
            try? await Task.sleep(for: .seconds(0.8))
            snapshot(window, out.appendingPathComponent("16-terminal-snippet.png"))
        }

        // 5. Panel plików SFTP (ten sam serwer, hasło z Pęku kluczy): wysłanie, lista, pobranie z porównaniem.
        if let id = keychainServerID, let s = model.servers.first(where: { $0.id == id }) {
            model.openFiles(s)
            let fs = model.activeSession!.files!
            let ready = await waitFor(20, { fs.state == .ready ? true : (fs.auth.prompt != nil ? false : nil) })
            if ready != true {
                note("FAIL: panel plików — \(fs.auth.prompt.map { "pytanie: \($0.text)" } ?? "\(fs.state)")", out)
                snapshot(window, out.appendingPathComponent("10-pliki-blad.png"))
                ok = false
            } else {
                let tmp = FileManager.default.temporaryDirectory.appendingPathComponent("wp-smoke-\(UUID().uuidString.prefix(6))")
                try? FileManager.default.createDirectory(at: tmp.appendingPathComponent("folder z plikami"), withIntermediateDirectories: true)
                let name = "waypoint-smoke.txt"
                let payload = "WAYPOINT_SFTP_OK zażółć gęślą jaźń\n" + String(repeating: "x", count: 300_000)
                try? payload.write(to: tmp.appendingPathComponent(name), atomically: true, encoding: .utf8)
                try? "w folderze".write(to: tmp.appendingPathComponent("folder z plikami/a.txt"), atomically: true, encoding: .utf8)
                fs.upload([tmp.appendingPathComponent(name), tmp.appendingPathComponent("folder z plikami")])
                let listed = await waitFor(20, { !fs.busy && fs.entries.contains { $0.name == name } && fs.entries.contains { $0.name == "folder z plikami" } ? true : nil })
                fs.selection = Set(fs.entries.filter { $0.name == name }.map(\.id))
                try? await Task.sleep(for: .seconds(0.8))
                snapshot(window, out.appendingPathComponent("10-pliki.png"))
                var downloaded = false
                if listed == true, let e = fs.entries.first(where: { $0.name == name }) {
                    let back = tmp.appendingPathComponent("pobrane")
                    try? FileManager.default.createDirectory(at: back, withIntermediateDirectories: true)
                    fs.download([e], to: back)
                    _ = await waitFor(20, { fs.busy ? nil : true })
                    downloaded = (try? String(contentsOf: back.appendingPathComponent(name), encoding: .utf8)) == payload
                }
                note(listed == true && downloaded ? "OK: SFTP — wysłanie pliku i folderu, lista, pobranie (treść zgodna)"
                                                  : "FAIL: SFTP lista=\(listed == true) pobranie=\(downloaded) komunikat=\(fs.message ?? "-")", out)
                ok = ok && listed == true && downloaded
                // 6. Edytor: plik z CRLF → Monaco w WKWebView → dopisana linia → bezpieczny zapis → CRLF zostaje.
                let conf = "konfig.conf"
                try? "a=1\r\nb=2\r\n".write(to: tmp.appendingPathComponent(conf), atomically: true, encoding: .utf8)
                fs.upload([tmp.appendingPathComponent(conf)])
                _ = await waitFor(10, { !fs.busy && fs.entries.contains { $0.name == conf } ? true : nil })
                if let e = fs.entries.first(where: { $0.name == conf }) {
                    var opened = false
                    fs.readForEdit(e) { payload in
                        EditorWindowController.show(server: s, entry: e, payload: payload)
                        opened = true
                    }
                    _ = await waitFor(10, { opened ? true : nil })
                    if let wc = EditorWindowController.open.last {
                        let doc = wc.doc
                        let monaco = await waitFor(20, { doc.ready ? true : nil })
                        note(monaco == true ? "Monaco wczytane (\(doc.language), \(doc.format.encodingName), \(doc.format.eolLabel))"
                                            : "FAIL: Monaco nie wstało w WKWebView", out)
                        _ = try? await doc.bridge.webView.evaluateJavaScript(
                            "var m = monaco.editor.getEditors()[0].getModel(); m.setValue(m.getValue() + 'c=3\\n'); 1")
                        let dirty = await waitFor(5, { doc.dirty ? true : nil })
                        try? await Task.sleep(for: .seconds(0.8))
                        if let w = wc.window { snapshot(w, out.appendingPathComponent("11-edytor.png")) }
                        doc.saveFromButton()
                        _ = await waitFor(15, { !doc.saving && !doc.dirty ? true : nil })
                        var content: Data?
                        fs.perform({ c in try c.readFile(e.path) }) { r in content = (try? r.get()) ?? Data() }
                        _ = await waitFor(10, { content })
                        let expected = Data("a=1\r\nb=2\r\nc=3\r\n".utf8)
                        let saved = content == expected
                        note(saved && dirty == true ? "OK: edytor — zapis na serwer, CRLF zachowane (\(doc.status ?? ""))"
                                                    : "FAIL: edytor dirty=\(dirty == true) treść=\(content.map { String(decoding: $0, as: UTF8.self).debugDescription } ?? "-") status=\(doc.status ?? "-")", out)
                        ok = ok && monaco == true && saved
                        wc.window?.close()
                    } else {
                        note("FAIL: okno edytora się nie otworzyło (\(fs.message ?? "-"))", out); ok = false
                    }
                }

                // 7. sudo: plik roota → edytor tylko do odczytu → „Edytuj i zapisuj przez sudo" → zapis
                //    (hasło sudo = hasło logowania z Pęku kluczy) → właściciel root zostaje.
                if let e = fs.entries.first(where: { $0.name == "root-owned.conf" }) {
                    var opened = false
                    fs.readForEdit(e) { payload in
                        EditorWindowController.show(server: s, entry: e, payload: payload)
                        opened = true
                    }
                    _ = await waitFor(10, { opened ? true : nil })
                    if let wc = EditorWindowController.open.last {
                        let doc = wc.doc
                        _ = await waitFor(20, { doc.ready ? true : nil })
                        let wasReadOnly = doc.readOnly
                        try? await Task.sleep(for: .seconds(0.6))
                        if let w = wc.window { snapshot(w, out.appendingPathComponent("12-edytor-root.png")) }
                        doc.editWithSudo()
                        _ = try? await doc.bridge.webView.evaluateJavaScript(
                            "var m = monaco.editor.getEditors()[0].getModel(); m.setValue(m.getValue() + 'server_name smoke;\\n'); 1")
                        _ = await waitFor(5, { doc.dirty ? true : nil })
                        doc.saveFromButton()
                        _ = await waitFor(30, { (!doc.saving && !doc.dirty) || doc.connection.auth.prompt != nil ? true : nil })
                        if let p = doc.connection.auth.prompt { note("pytanie sudo: \(p.text)", out); p.answer(nil, false) }
                        var info: SftpAttributes?
                        var content: Data?
                        fs.perform({ c in (try c.stat(e.path), try c.readFile(e.path)) }) { r in
                            if let v = try? r.get() { info = v.0; content = v.1 }
                        }
                        _ = await waitFor(10, { content })
                        let ok7 = content == Data("listen 80;\nserver_name smoke;\n".utf8) && info?.uid == 0
                        note(ok7 && wasReadOnly ? "OK: sudo — plik roota zapisany, właściciel root (\(doc.status ?? ""))"
                                                : "FAIL: sudo ro=\(wasReadOnly) uid=\(info?.uid.map(String.init) ?? "-") treść=\(content.map { String(decoding: $0, as: UTF8.self).debugDescription } ?? "-") status=\(doc.status ?? "-")", out)
                        ok = ok && ok7 && wasReadOnly
                        if let w = wc.window { snapshot(w, out.appendingPathComponent("13-edytor-sudo.png")) }
                        wc.window?.close()
                    }
                } else {
                    note("FAIL: brak root-owned.conf na liście", out); ok = false
                }

                // Sprzątanie na serwerze testowym.
                let toDelete = fs.entries.filter { $0.name == name || $0.name == "folder z plikami" || $0.name == conf }
                fs.delete(toDelete)
                _ = await waitFor(10, { fs.busy ? nil : true })
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

        // Kilka kart (terminale i pliki): zrzut paska kart z aktywną pierwszą.
        model.activateTab(0)
        try? await Task.sleep(for: .seconds(1))
        snapshot(window, out.appendingPathComponent("06-karty.png"))

        // 8. Lista serwerów: kropki osiągalności (sshd na 2222 osiągalny), dziennik połączeń, grupy, pulpit.
        model.settings.showLatency = true
        let online = await waitFor(20, { () -> Bool? in
            guard let id = keychainServerID, case .online? = model.reach[id] else { return nil }
            return true
        })
        note(online == true ? "OK: osiągalność — lokalny sshd online (\(model.onlineCount)/\(model.reach.count) osiągalnych)"
                            : "FAIL: brak kropki osiągalności dla lokalnego sshd (\(model.reach))", out)
        ok = ok && online == true
        let logLines = ConnectionLog.readLines(dir: model.dataDirectory)
        try? logLines.joined(separator: "\n").write(to: out.appendingPathComponent("connections.log"), atomically: true, encoding: .utf8)
        let connects = logLines.filter { $0.contains("  CONNECTED ") }.count
        note(connects >= 3 ? "OK: dziennik połączeń — \(connects)× CONNECTED, \(logLines.count) linii"
                           : "FAIL: dziennik połączeń — \(connects)× CONNECTED", out)
        ok = ok && connects >= 3
        model.renameGroup("Klienci", to: "Klienci 2026")
        model.setCollapsed("Produkcja", true)
        let renamed = model.servers.contains { $0.group == "Klienci 2026" } && !model.servers.contains { $0.group == "Klienci" }
        note(renamed ? "OK: zmiana nazwy grupy" : "FAIL: zmiana nazwy grupy", out)
        ok = ok && renamed
        model.showDashboard()
        try? await Task.sleep(for: .seconds(1.5))
        snapshot(window, out.appendingPathComponent("10-pulpit.png"))
        note(model.recentServers.count >= 2 ? "OK: pulpit — \(model.recentServers.count) ostatnio używanych"
                                            : "FAIL: pulpit bez ostatnio używanych", out)
        ok = ok && model.recentServers.count >= 2
        model.setCollapsed("Produkcja", false)
        // Okno Ustawień (⌘,) — tylko zrzut.
        let before = Set(NSApp.windows.filter(\.isVisible).map(ObjectIdentifier.init))
        // Pozycja „Ustawienia…" (⌘,) z menu aplikacji — tak, jak kliknąłby użytkownik.
        if let appMenu = NSApp.mainMenu?.items.first?.submenu,
           let i = appMenu.items.firstIndex(where: { $0.keyEquivalent == "," && $0.keyEquivalentModifierMask == .command }) {
            appMenu.performActionForItem(at: i)
        }
        if let w = await waitFor(5, { NSApp.windows.first { $0.isVisible && !before.contains(ObjectIdentifier($0)) } }) {
            try? await Task.sleep(for: .seconds(0.8))
            snapshot(w, out.appendingPathComponent("11-ustawienia.png"))
            w.close()
        } else {
            note("uwaga: nie otworzyło się okno Ustawień", out)
        }
        finish(out, ok: ok)
    }

    private static var keychainServerID: String?

    private static func finish(_ out: URL, ok: Bool) {
        if let id = keychainServerID { Keychain.delete(for: id) }
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
