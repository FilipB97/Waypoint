import AppKit
import Observation
import WaypointCore
import WaypointRDP

/// Wbudowana sesja RDP (FreeRDP) w karcie — odpowiednik kontrolki mstscax z wersji Windows.
/// Hasło przychodzi z Pęku kluczy (albo profilu poświadczeń), więc nie ma pytania przy każdym
/// połączeniu; pytamy tylko, gdy go brak albo serwer je odrzuci.
@MainActor
@Observable
final class RdpSession: Identifiable {
    enum State: Equatable { case connecting, connected, ended(String?) }

    /// Pytanie o certyfikat serwera (wątek połączenia czeka na odpowiedź).
    struct CertificateQuestion: Identifiable {
        let id = UUID()
        let info: RdpConnection.Certificate
        let answer: (RdpConnection.CertificateAnswer) -> Void
    }

    let id = UUID()
    private(set) var server: Server
    private(set) var state: State = .connecting
    var title: String { server.displayName }
    var isRunning: Bool { state == .connected || state == .connecting }
    /// Pytanie o hasło (ten sam wygląd karty co przy SSH).
    var prompt: PendingPrompt?
    var certificate: CertificateQuestion?
    var notice: String?
    /// Trwające pobieranie plików ze schowka sesji (tekst postępu nad pulpitem).
    var transfer: String?
    private(set) var desktopSize: CGSize = .zero

    @ObservationIgnored let view = RdpDisplayView()
    @ObservationIgnored private var connection: RdpConnection?
    @ObservationIgnored private var password: String?
    @ObservationIgnored private var passwordRejected = false
    @ObservationIgnored var onEnded: ((Bool) -> Void)?   // true = błąd (do dziennika: FAILED)
    /// Schowek: licznik zmian NSPasteboard — nowy tekst z Maca idzie do sesji, nasz zapis nie wraca.
    @ObservationIgnored private var pasteboardCount = -1
    @ObservationIgnored private var clipboardTimer: Timer?
    @ObservationIgnored private var readCount = -1   // licznik schowka, którego treść już wysłano
    @ObservationIgnored private var download: CancelFlag?

    init(server: Server) {
        self.server = server
        view.session = self   // połączenie startuje widok, gdy dostanie okno i rozmiar
    }

    // MARK: Połączenie

    func connect() {
        connection?.stop()
        state = .connecting
        let s = server
        if password == nil {
            password = SessionPasswords.get(s) ?? Keychain.password(for: s.keychainAccount)
        }
        guard let pw = password, !pw.isEmpty else { askPassword(retry: passwordRejected); return }
        start(password: pw)
    }

    private func start(password pw: String) {
        let s = server
        let c = RdpConnection()
        connection = c
        view.connection = c
        let mode = view.displayMode()
        view.lastMode = mode
        var cfg = RdpConnection.Config(host: s.host)
        cfg.port = s.port
        cfg.username = s.username
        cfg.domain = s.domain
        cfg.password = pw
        cfg.width = mode.width
        cfg.height = mode.height
        cfg.scalePercent = mode.scalePercent
        cfg.adminSession = s.rdpAdminSession
        cfg.redirectClipboard = s.rdpRedirectClipboard
        cfg.ignoreCertificate = s.rdpAuthenticationLevel == 0   // jak „połącz bez ostrzeżenia” w mstsc
        cfg.configDirectory = AppModel.shared.dataDirectory.appendingPathComponent("freerdp").path
        cfg.clientHostname = Host.current().localizedName ?? "Mac"
        if AppModel.shared.settings.rdpDiagnostics {
            let dir = RdpSession.logDirectory
            try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            cfg.logDirectory = dir.path
        }
        if !s.rdpGatewayHostname.isEmpty {
            let (h, p) = ExternalImport.splitHostPort(s.rdpGatewayHostname, defaultPort: 443)
            cfg.gatewayHost = h
            cfg.gatewayPort = p
            cfg.gatewaySameCredentials = true
        }

        c.onState = { [weak self] st in
            DispatchQueue.main.async { self?.handle(st, from: c) }
        }
        c.onResize = { [weak self] w, h in
            DispatchQueue.main.async { self?.desktopSize = CGSize(width: w, height: h); self?.view.desktopResized(w, h) }
        }
        let ticker = view.ticker
        c.onFrame = { _ in ticker.tick() }
        c.onCursor = { [weak self] cursor in
            DispatchQueue.main.async {
                guard let self, c === self.connection else { return }
                self.view.setRemoteCursor(cursor)
            }
        }
        if s.rdpRedirectClipboard {
            c.onClipboardText = { [weak self] text in
                DispatchQueue.main.async {
                    guard let self, c === self.connection else { return }
                    let pb = NSPasteboard.general
                    pb.clearContents()
                    pb.setString(text, forType: .string)
                    self.pasteboardCount = pb.changeCount
                    self.readCount = pb.changeCount   // zawartość z sesji — nie odsyłać
                }
            }
        }
        if s.rdpRedirectClipboard {
            c.onClipboardFiles = { [weak self] files in
                DispatchQueue.main.async {
                    guard let self, c === self.connection else { return }
                    self.receiveFiles(files, from: c)
                }
            }
        }
        if s.rdpRedirectDrives {
            // Dysk „Mac" w sesji (Ten komputer) — katalog domowy; duże pliki bez schowka.
            cfg.drivePath = FileManager.default.homeDirectoryForCurrentUser.path
            cfg.driveName = "Mac"
        }
        if s.rdpRedirectClipboard {
            // Windows wkleja ogłoszoną zawartość — odczyt schowka Maca teraz (na wątku głównym; wątek
            // kanału czeka, także na ewentualne systemowe pytanie o zgodę na wklejenie).
            c.onClipboardProvide = { [weak self] in
                let done = DispatchSemaphore(value: 0)
                DispatchQueue.main.async {
                    if let self, c === self.connection { self.sendPasteboard(to: c) }
                    done.signal()
                }
                _ = done.wait(timeout: .now() + 30)
            }
        }
        c.onVerifyCertificate = { [weak self] info in
            // Wątek połączenia czeka na odpowiedź użytkownika.
            let done = DispatchSemaphore(value: 0)
            nonisolated(unsafe) var result = RdpConnection.CertificateAnswer.reject
            DispatchQueue.main.async {
                guard let self else { done.signal(); return }
                self.certificate = CertificateQuestion(info: info) { a in
                    result = a
                    self.certificate = nil
                    done.signal()
                }
            }
            done.wait()
            return result
        }
        c.onAuthenticate = { [weak self] gateway, user, domain in
            let done = DispatchSemaphore(value: 0)
            nonisolated(unsafe) var result: RdpConnection.Credentials?
            DispatchQueue.main.async {
                guard let self else { done.signal(); return }
                let label = (domain.isEmpty ? user : domain + "\\" + user) + "@" + (gateway ? self.server.rdpGatewayHostname : self.server.host)
                self.prompt = PendingPrompt(kind: .password, text: label, isRetry: true) { value, save in
                    self.prompt = nil
                    if let value {
                        result = RdpConnection.Credentials(username: user, password: value, domain: domain)
                        self.remember(value, save: save)
                    }
                    done.signal()
                }
            }
            done.wait()
            return result
        }
        if !c.start(cfg) {
            state = .ended(L("rdp.err.start"))
        }
    }

    private func handle(_ st: RdpConnection.State, from c: RdpConnection) {
        guard c === connection else { return }   // zdarzenie starego połączenia
        switch st {
        case .connecting:
            state = .connecting
        case .connected:
            state = .connected
            passwordRejected = false
            startClipboardSync()
            // Serwer bez kanału schowka (zasady serwera albo usługa schowka nie wystartowała) — powiedz o tym,
            // zamiast milcząco nie wklejać.
            if server.rdpRedirectClipboard {
                DispatchQueue.main.asyncAfter(deadline: .now() + 30) { [weak self] in
                    guard let self, c === self.connection, self.state == .connected, !c.isClipboardReady else { return }
                    RdpConnection.log("[\(self.server.host)] schowek: serwer nie otworzył kanału schowka w 30 s")
                    self.notice = L("rdp.clip.notready")
                }
            }
            view.window?.makeFirstResponder(view)
        case .disconnected(let error, let message):
            stopClipboardSync()
            cancelTransfer()
            connection?.stop()
            connection = nil
            view.connection = nil
            if RdpConnection.isAuthenticationError(error) {
                // Hasło odrzucone: zapomnij je w tej sesji aplikacji i zapytaj (z opcją zapisu).
                SessionPasswords.clear(server)
                password = nil
                passwordRejected = true
                askPassword(retry: true)
                return
            }
            state = .ended(error == 0 ? nil : (message.isEmpty ? String(format: L("rdp.err.code"), error) : message))
            onEnded?(error != 0)
        }
    }

    private func askPassword(retry: Bool) {
        let s = server
        let label = (s.domain.isEmpty ? s.username : s.domain + "\\" + s.username) + "@" + s.host
        prompt = PendingPrompt(kind: .password, text: label, isRetry: retry) { [weak self] value, save in
            guard let self else { return }
            self.prompt = nil
            guard let value, !value.isEmpty else { self.state = .ended(L("rdp.err.nopassword")); return }
            self.remember(value, save: save)
            self.password = value
            self.start(password: value)
        }
    }

    private func remember(_ value: String, save: Bool) {
        SessionPasswords.set(value, for: server)
        if save {
            let ok = Keychain.save(value, for: server.keychainAccount, label: SessionPasswords.label(server))
            notice = ok ? L("prompt.saved") : L("prompt.savefail")
        }
    }

    func reconnect() {
        guard !isRunning else { return }
        connect()
    }

    // MARK: Schowek

    private func startClipboardSync() {
        guard server.rdpRedirectClipboard, clipboardTimer == nil else { return }
        pasteboardCount = -1
        syncPasteboard()
        // NSPasteboard nie ma powiadomień o zmianie — sprawdzanie licznika jest tanie.
        clipboardTimer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { _ = self?.syncPasteboard() }
        }
    }

    private func stopClipboardSync() {
        clipboardTimer?.invalidate()
        clipboardTimer = nil
    }

    /// Schowek Maca → sesja, jeśli się zmienił od ostatniego razu. true = coś wysłano.
    ///
    /// W tle (`read: false`) tylko ogłaszamy serwerowi, że jest tekst / są pliki — po typach, bez odczytu
    /// treści (nowsze macOS chronią schowek przed czytaniem w tle). Treść czytamy, gdy Windows poprosi
    /// o nią przy wklejeniu (`provideClipboard`), albo od razu przy ⌘V w pulpicie (`read: true`).
    @discardableResult
    func syncPasteboard(read: Bool = false) -> Bool {
        let pb = NSPasteboard.general
        guard server.rdpRedirectClipboard, pb.changeCount != pasteboardCount, let c = connection else { return false }
        pasteboardCount = pb.changeCount
        if read { sendPasteboard(to: c); return true }
        if readCount == pb.changeCount { return true }
        let types = pb.types ?? []
        let files = types.contains(.fileURL)
        let text = !files && types.contains(.string)
        RdpConnection.log("[\(server.host)] schowek Maca: zmiana \(pb.changeCount), typy: \(types.prefix(6).map(\.rawValue).joined(separator: ", "))")
        c.announceClipboard(text: text, files: files)
        return true
    }

    /// ⌘V w pulpicie (gest użytkownika — odczyt schowka dozwolony): treść do sesji, jeśli jeszcze jej
    /// nie wysłano. true = wysłano teraz (Ctrl+V trzeba chwilę opóźnić).
    func preparePaste() -> Bool {
        let pb = NSPasteboard.general
        guard server.rdpRedirectClipboard, let c = connection, pb.changeCount != readCount else { return false }
        pasteboardCount = pb.changeCount
        sendPasteboard(to: c)
        return true
    }

    /// Odczyt schowka Maca i przekazanie treści do sesji (pliki z Findera albo tekst).
    private func sendPasteboard(to c: RdpConnection) {
        let pb = NSPasteboard.general
        readCount = pb.changeCount
        if let urls = pb.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL],
           !urls.isEmpty {
            RdpConnection.log("[\(server.host)] schowek Maca: pliki \(urls.count)")
            c.setClipboardFiles(RdpConnection.clipboardEntries(for: urls))
        } else {
            let text = pb.string(forType: .string)
            // Pusty wynik przy tekście w schowku = macOS odmówił odczytu (ochrona schowka).
            RdpConnection.log("[\(server.host)] schowek Maca: tekst " + (text.map { "\($0.utf8.count) bajtów" } ?? "BRAK (odmowa dostępu albo nie tekst)"))
            c.setClipboardText(text)
        }
    }

    /// Pliki skopiowane w sesji: pobranie od razu do pamięci podręcznej, potem do schowka Maca
    /// (schowek Finderu chce gotowych plików). Nowa kopia w sesji przerywa poprzednie pobieranie.
    private func receiveFiles(_ files: [RdpConnection.RemoteFile], from c: RdpConnection) {
        download?.set(true)
        guard !files.isEmpty else { return }
        let flag = CancelFlag()
        download = flag
        let root = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Waypoint/rdp-clipboard")
        try? FileManager.default.removeItem(at: root)   // poprzednie kopie
        let dir = root.appendingPathComponent(UUID().uuidString)
        let count = files.filter { !$0.path.contains("/") }.count
        transfer = String(format: L("rdp.files.receiving"), count)
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            var lastShown = Date.distantPast
            do {
                let top = try c.downloadClipboardFiles(files, to: dir, progress: { done, total in
                    guard Date().timeIntervalSince(lastShown) > 0.25, total > 0 else { return }
                    lastShown = Date()
                    let text = String(format: L("rdp.files.progress"), count,
                                      ByteCountFormatter.string(fromByteCount: done, countStyle: .file),
                                      ByteCountFormatter.string(fromByteCount: total, countStyle: .file))
                    DispatchQueue.main.async { if self?.download === flag { self?.transfer = text } }
                }, isCancelled: { flag.value })
                DispatchQueue.main.async {
                    guard let self, self.download === flag else { return }
                    self.download = nil
                    self.transfer = nil
                    let pb = NSPasteboard.general
                    pb.clearContents()
                    pb.writeObjects(top as [NSURL])
                    self.pasteboardCount = pb.changeCount
                    self.readCount = pb.changeCount   // zawartość z sesji — nie odsyłać
                    self.notice = String(format: L("rdp.files.ready"), top.count)
                }
            } catch {
                DispatchQueue.main.async {
                    guard let self, self.download === flag else { return }
                    self.download = nil
                    self.transfer = nil
                    if !flag.value { self.notice = String(format: L("rdp.files.failed"), "\(error)") }
                }
            }
        }
    }

    /// Pliki upuszczone na pulpit: do schowka sesji i wklejenie w miejscu upuszczenia (klik + Ctrl+V).
    func dropFiles(_ urls: [URL], at point: (Int, Int)) {
        guard state == .connected, let c = connection else { return }
        guard server.rdpRedirectClipboard else { notice = L("rdp.drop.noclipboard"); return }
        let entries = RdpConnection.clipboardEntries(for: urls)
        guard !entries.isEmpty else { return }
        c.setClipboardFiles(entries)
        notice = String(format: L("rdp.drop.pasting"), urls.count)
        // Chwila na dotarcie listy plików do serwera, zanim Eksplorator poprosi o schowek.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { [weak self] in
            guard let self, self.connection === c else { return }
            self.view.pasteAt(point)
        }
    }

    func cancelTransfer() {
        download?.set(true)
        download = nil
        transfer = nil
    }

    func close() {
        stopClipboardSync()
        cancelTransfer()
        certificate?.answer(.reject)
        prompt?.answer(nil, false)
        let c = connection
        connection = nil
        view.connection = nil
        state = .ended(nil)
        // Rozłączenie czeka na koniec wątku FreeRDP — poza wątkiem głównym, żeby okno nie zamarzło.
        DispatchQueue.global(qos: .userInitiated).async { c?.stop() }
    }

    /// Okno zmieniło rozmiar (po ustabilizowaniu) — nowa rozdzielczość sesji.
    func requestResize(width: Int, height: Int, scale: Int) {
        guard state == .connected else { return }
        connection?.resize(width: width, height: height, scalePercent: scale)
    }
}

extension RdpSession {
    /// ~/Library/Logs/Waypoint — dziennik diagnostyczny RDP (waypoint-rdp.log).
    static var logDirectory: URL {
        FileManager.default.urls(for: .libraryDirectory, in: .userDomainMask)[0].appendingPathComponent("Logs/Waypoint")
    }
}

extension Server {
    /// Klient RDP na Macu: wbudowany (FreeRDP) albo Microsoft Windows App (pole tylko dla Maca).
    var rdpUsesWindowsApp: Bool {
        get { if case .string(let v)? = extra["MacRdpClient"] { return v == "windowsApp" } else { return false } }
        set { if newValue { extra["MacRdpClient"] = .string("windowsApp") } else { extra.removeValue(forKey: "MacRdpClient") } }
    }
}

