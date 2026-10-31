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
    private(set) var desktopSize: CGSize = .zero

    @ObservationIgnored let view = RdpDisplayView()
    @ObservationIgnored private var connection: RdpConnection?
    @ObservationIgnored private var password: String?
    @ObservationIgnored private var passwordRejected = false
    @ObservationIgnored var onEnded: ((Bool) -> Void)?   // true = błąd (do dziennika: FAILED)
    /// Schowek: licznik zmian NSPasteboard — nowy tekst z Maca idzie do sesji, nasz zapis nie wraca.
    @ObservationIgnored private var pasteboardCount = -1
    @ObservationIgnored private var clipboardTimer: Timer?

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
        if s.rdpRedirectClipboard {
            c.onClipboardText = { [weak self] text in
                DispatchQueue.main.async {
                    guard let self, c === self.connection else { return }
                    let pb = NSPasteboard.general
                    pb.clearContents()
                    pb.setString(text, forType: .string)
                    self.pasteboardCount = pb.changeCount
                }
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
            view.window?.makeFirstResponder(view)
        case .disconnected(let error, let message):
            stopClipboardSync()
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
            MainActor.assumeIsolated { self?.syncPasteboard() }
        }
    }

    private func stopClipboardSync() {
        clipboardTimer?.invalidate()
        clipboardTimer = nil
    }

    private func syncPasteboard() {
        let pb = NSPasteboard.general
        guard pb.changeCount != pasteboardCount, let c = connection else { return }
        pasteboardCount = pb.changeCount
        c.setClipboardText(pb.string(forType: .string))
    }

    func close() {
        stopClipboardSync()
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

extension Server {
    /// Klient RDP na Macu: wbudowany (FreeRDP) albo Microsoft Windows App (pole tylko dla Maca).
    var rdpUsesWindowsApp: Bool {
        get { if case .string(let v)? = extra["MacRdpClient"] { return v == "windowsApp" } else { return false } }
        set { if newValue { extra["MacRdpClient"] = .string("windowsApp") } else { extra.removeValue(forKey: "MacRdpClient") } }
    }
}
