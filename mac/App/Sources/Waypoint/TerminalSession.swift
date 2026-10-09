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

/// Jedna karta SSH: proces `/usr/bin/ssh` w pseudo-terminalu (SwiftTerm) + kanał askpass.
@MainActor
@Observable
final class TerminalSession: Identifiable {
    enum State: Equatable { case running, ended(Int32?) }

    let id = UUID()
    private(set) var server: Server
    private(set) var state: State = .running
    var title: String
    var prompt: PendingPrompt?
    private(set) var warnings: [String] = []
    /// Ostatnia informacja o zapisie hasła („Zapisano w Pęku kluczy") — pokazywana chwilę nad terminalem.
    var notice: String?

    @ObservationIgnored let view: SessionTerminalView
    @ObservationIgnored private var askpass: AskpassServer?
    @ObservationIgnored private var storedPasswordTried = false
    @ObservationIgnored private var passwordAttempts = 0

    init(server: Server) {
        self.server = server
        self.title = server.displayName
        self.view = SessionTerminalView(frame: NSRect(x: 0, y: 0, width: 800, height: 500))
        view.session = self
        TerminalAppearance.apply(to: view)
        start()
    }

    var isRunning: Bool { state == .running }

    func start() {
        storedPasswordTried = false
        passwordAttempts = 0
        let launch = SshCommand.build(server, homeDirectory: NSHomeDirectory(),
                                      fileExists: { FileManager.default.fileExists(atPath: $0) })
        warnings = launch.warnings

        var extra: [String: String] = [:]
        do {
            let srv = try AskpassServer { [weak self] req, reply in
                Task { @MainActor in
                    guard let self else { reply(Askpass.Reply(value: nil)); return }
                    self.handle(req) { reply(Askpass.Reply(value: $0)) }
                }
            }
            askpass = srv
            extra = srv.environment(askpassExecutable: Bundle.main.executablePath ?? CommandLine.arguments[0])
        } catch {
            // Bez kanału askpass ssh zapyta o hasło w samym terminalu — dalej da się zalogować.
            askpass = nil
        }

        state = .running
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
        prompt?.answer(nil, false)
        prompt = nil
        if isRunning { view.terminate() }
        askpass?.stop()
        askpass = nil
    }

    func processEnded(_ code: Int32?) {
        state = .ended(code)
        prompt?.answer(nil, false)
        prompt = nil
        askpass?.stop()
        askpass = nil
    }

    // MARK: askpass

    private func handle(_ req: Askpass.Request, reply: @escaping (String?) -> Void) {
        let kind = Askpass.classify(req.prompt, promptHint: req.hint)

        if kind == .password {
            passwordAttempts += 1
            // Pierwsza prośba o hasło: zapisane w Pęku kluczy idzie od razu, bez okna.
            if !storedPasswordTried {
                storedPasswordTried = true
                if let saved = Keychain.password(for: server.id) {
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
            if save, kind == .password, let value, !value.isEmpty {
                let ok = Keychain.save(value, for: self.server.id, label: "Waypoint — \(self.server.displayName)")
                self.notice = ok ? L("prompt.saved") : L("prompt.savefail")
            }
            self.view.window?.makeFirstResponder(self.view)
        }
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

    static func apply(to v: TerminalView) {
        v.font = NSFont.monospacedSystemFont(ofSize: 13, weight: .regular)
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
