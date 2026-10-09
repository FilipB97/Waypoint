import AppKit
import Observation
import SwiftTerm
import WaypointCore

/// Pytanie ssh czekające na użytkownika (hasło, passphrase, klucz hosta, kod 2FA).
struct PendingPrompt: Identifiable {
    let id = UUID()
    let kind: Askpass.Kind
    let text: String
    /// Poprzednia próba (zapisane albo wpisane hasło) została odrzucona.
    let isRetry: Bool
    let answer: (String?, Bool) -> Void   // (odpowiedź albo nil = anuluj, zapisać w Pęku kluczy?)
}

/// Hasła podane w tej sesji aplikacji (pytanie w karcie, „Połącz jako…") — tylko w pamięci, do
/// zamknięcia aplikacji, jak hasło sesji w Windows. Dzięki temu panel plików i edytor otwarte po
/// zalogowaniu w terminalu nie pytają drugi raz. Klucz: konto hasła (serwer albo profil) + login.
@MainActor
enum SessionPasswords {
    private static var store: [String: String] = [:]
    private static func key(_ s: Server) -> String { s.keychainAccount + "\n" + s.username }
    static func get(_ s: Server) -> String? { store[key(s)] }
    static func set(_ password: String, for s: Server) { store[key(s)] = password }
    static func clear(_ s: Server) { store.removeValue(forKey: key(s)) }

    /// Opis wpisu w Pęku kluczy: profil albo serwer.
    static func label(_ s: Server) -> String {
        s.credentialProfileId.isEmpty ? "Waypoint — \(s.displayName)"
            : "Waypoint — profil \(AppModel.shared.profiles.first { $0.id == s.credentialProfileId }?.displayName ?? s.username)"
    }
}

/// Logowanie dla procesu ssh (terminal albo panel plików): kanał askpass, hasło z Pęku kluczy przy
/// pierwszej prośbie, pytania dla użytkownika i zapis hasła. Jeden obiekt na jedno uruchomienie ssh.
@MainActor
@Observable
final class AuthBroker {
    let server: Server
    var prompt: PendingPrompt?
    /// Krótka informacja nad kartą („Hasło zapisane w Pęku kluczy").
    var notice: String?
    /// Wołane po odpowiedzi na pytanie — karta oddaje fokus terminalowi.
    @ObservationIgnored var onAnswered: (() -> Void)?

    @ObservationIgnored private var channel: AskpassServer?
    @ObservationIgnored private var storedPasswordTried = false
    @ObservationIgnored private var passwordAttempts = 0

    init(server: Server) { self.server = server }

    /// Uruchamia kanał askpass i zwraca zmienne środowiskowe dla ssh (puste, gdy kanał się nie udał —
    /// wtedy terminal zapyta w samym terminalu, a panel plików zgłosi błąd logowania).
    func start() -> [String: String] {
        stop()
        storedPasswordTried = false
        passwordAttempts = 0
        do {
            let srv = try AskpassServer { [weak self] req, reply in
                Task { @MainActor in
                    guard let self else { reply(Askpass.Reply(value: nil)); return }
                    self.handle(req) { reply(Askpass.Reply(value: $0)) }
                }
            }
            channel = srv
            return srv.environment(askpassExecutable: Bundle.main.executablePath ?? CommandLine.arguments[0])
        } catch {
            channel = nil
            return [:]
        }
    }

    func stop() {
        prompt?.answer(nil, false)
        prompt = nil
        channel?.stop()
        channel = nil
    }

    private func handle(_ req: Askpass.Request, reply: @escaping (String?) -> Void) {
        let kind = Askpass.classify(req.prompt, promptHint: req.hint)
        if kind == .password {
            passwordAttempts += 1
            // Pierwsza prośba o hasło: zapisane w Pęku kluczy idzie od razu, bez okna.
            if !storedPasswordTried {
                storedPasswordTried = true
                if let saved = SessionPasswords.get(server) ?? Keychain.password(for: server.keychainAccount) {
                    reply(saved)
                    return
                }
            }
        }
        let retry = kind == .password && passwordAttempts > 1
        prompt = PendingPrompt(kind: kind, text: req.prompt, isRetry: retry) { [weak self] value, save in
            reply(value)
            guard let self else { return }
            self.prompt = nil
            if kind == .password, let value, !value.isEmpty { SessionPasswords.set(value, for: self.server) }
            if save, kind == .password, let value, !value.isEmpty {
                let ok = Keychain.save(value, for: self.server.keychainAccount, label: SessionPasswords.label(self.server))
                self.notice = ok ? L("prompt.saved") : L("prompt.savefail")
            }
            self.onAnswered?()
        }
    }
}

/// Jedna karta SSH: proces `/usr/bin/ssh` w pseudo-terminalu (SwiftTerm) + kanał askpass.
@MainActor
@Observable
final class TerminalSession: Identifiable {
    enum State: Equatable { case running, ended(Int32?) }

    let id = UUID()
    private(set) var server: Server
    private(set) var state: State = .running
    var title: String
    let auth: AuthBroker
    private(set) var warnings: [String] = []

    @ObservationIgnored let view: SessionTerminalView
    /// Koniec procesu ssh (kod wyjścia) — do dziennika połączeń.
    @ObservationIgnored var onEnded: ((Int32?) -> Void)?

    init(server: Server) {
        self.server = server
        self.title = server.displayName
        self.auth = AuthBroker(server: server)
        self.view = SessionTerminalView(frame: NSRect(x: 0, y: 0, width: 800, height: 500))
        view.session = self
        TerminalAppearance.apply(to: view)
        auth.onAnswered = { [weak self] in
            guard let v = self?.view else { return }
            v.window?.makeFirstResponder(v)
        }
        start()
    }

    var isRunning: Bool { state == .running }

    func start() {
        state = .running
        // Telnet i port szeregowy: ten sam plik wykonywalny w trybie pomocniczym (StreamHelper).
        if server.proto == .telnet || server.proto == .serial {
            let args = server.proto == .telnet
                ? StreamHelper.telnetArguments(host: server.host, port: server.port)
                : StreamHelper.serialArguments(device: server.host, baud: server.port)
            let env = SshCommand.environment(base: ProcessInfo.processInfo.environment, extra: [:])
            view.startProcess(executable: Bundle.main.executablePath ?? CommandLine.arguments[0], args: args,
                              environment: env, execName: server.proto == .telnet ? "telnet" : "serial")
            return
        }
        let launch = SshCommand.build(server, homeDirectory: NSHomeDirectory(),
                                      fileExists: { FileManager.default.fileExists(atPath: $0) })
        warnings = launch.warnings
        let extra = auth.start()
        view.startProcess(executable: SshCommand.executable, args: launch.arguments,
                          environment: SshCommand.environment(base: ProcessInfo.processInfo.environment, extra: extra),
                          execName: "ssh")
    }

    func reconnect() {
        guard !isRunning else { return }
        view.feed(text: "\r\n")
        start()
    }

    /// Zamknięcie karty: odpowiada „anuluj" na wiszące pytanie i kończy ssh.
    func close() {
        auth.stop()
        if isRunning { view.terminate() }
    }

    func processEnded(_ code: Int32?) {
        state = .ended(code)
        auth.stop()
        onEnded?(code)
    }
}

/// Widok terminala jednej sesji. Zdarzenia procesu (koniec, tytuł) idą przez osobny obiekt-delegata:
/// metody delegata mają te same nazwy co publiczne (nie-open) metody klasy bazowej, więc sama podklasa
/// nie może być swoim delegatem.
final class SessionTerminalView: LocalProcessTerminalView {
    weak var session: TerminalSession?
    private let bridge = Bridge()

    override init(frame: CGRect) {
        super.init(frame: frame)
        bridge.owner = self
        processDelegate = bridge
    }

    required init?(coder: NSCoder) {
        super.init(coder: coder)
        bridge.owner = self
        processDelegate = bridge
    }

    /// Linki z wyjścia terminala: tylko http/https (treść pochodzi z serwera — jak w wersji Windows).
    override func requestOpenLink(source: TerminalView, link: String, params: [String: String]) {
        guard let url = URL(string: link), let scheme = url.scheme?.lowercased(),
              scheme == "http" || scheme == "https" else { return }
        NSWorkspace.shared.open(url)
    }

    private final class Bridge: LocalProcessTerminalViewDelegate {
        weak var owner: SessionTerminalView?

        func sizeChanged(source: LocalProcessTerminalView, newCols: Int, newRows: Int) {}
        func hostCurrentDirectoryUpdate(source: TerminalView, directory: String?) {}

        func setTerminalTitle(source: LocalProcessTerminalView, title: String) {
            let t = title.trimmingCharacters(in: .whitespaces)
            DispatchQueue.main.async { [weak self] in
                MainActor.assumeIsolated {
                    guard let s = self?.owner?.session else { return }
                    s.title = t.isEmpty ? s.server.displayName : t
                }
            }
        }

        func processTerminated(source: TerminalView, exitCode: Int32?) {
            DispatchQueue.main.async { [weak self] in
                MainActor.assumeIsolated { self?.owner?.session?.processEnded(exitCode) }
            }
        }
    }
}

/// Wygląd terminala: kolory marki (tło jak kafel ikony), czcionka SF Mono.
enum TerminalAppearance {
    static let background = NSColor(srgbRed: 0x0F / 255.0, green: 0x11 / 255.0, blue: 0x17 / 255.0, alpha: 1)
    static let foreground = NSColor(srgbRed: 0xE7 / 255.0, green: 0xE8 / 255.0, blue: 0xEE / 255.0, alpha: 1)
    static let accent = NSColor(srgbRed: 0x7A / 255.0, green: 0xA2 / 255.0, blue: 0xFF / 255.0, alpha: 1)

    static func setFont(_ v: TerminalView, size: Int) {
        v.font = NSFont.monospacedSystemFont(ofSize: CGFloat(size), weight: .regular)
    }

    static func apply(to v: TerminalView) {
        setFont(v, size: MainActor.assumeIsolated { AppModel.shared.settings.terminalFontSize })
        v.nativeBackgroundColor = background
        v.nativeForegroundColor = foreground
        v.caretColor = accent
        v.selectedTextBackgroundColor = accent.withAlphaComponent(0.35)
        v.optionAsMetaKey = false   // Option zostaje do polskich znaków (ą, ś, ł…)
        // Terminal jest zawsze ciemny, więc i jego pasek przewijania (przy myszy bez gładzika macOS
        // pokazuje go na stałe — w jasnym motywie byłby to jasny pas przy prawej krawędzi).
        v.appearance = NSAppearance(named: .darkAqua)
    }
}
