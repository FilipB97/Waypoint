import AppKit
import SwiftUI
import WaypointCore

/// Pasek kart sesji nad terminalem.
struct SessionTabBar: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 4) {
                ForEach(Array(model.sessions.enumerated()), id: \.element.id) { index, s in
                    SessionTabView(session: s, index: index, active: model.activeSessionID == s.id)
                }
            }
            .padding(.horizontal, 8)
            .padding(.vertical, 5)
        }
        .background(.bar)
        .overlay(alignment: .bottom) { Divider() }
    }
}

private struct SessionTabView: View {
    @Environment(AppModel.self) private var model
    let session: SessionTab
    let index: Int
    let active: Bool
    @State private var hover = false

    var body: some View {
        HStack(spacing: 6) {
            Circle()
                .fill(session.isRunning ? Color.green : Color.secondary)
                .frame(width: 7, height: 7)
            Image(systemName: session.systemImage)
                .font(.caption)
                .foregroundStyle(.secondary)
            Text(session.title)
                .lineLimit(1)
                .frame(maxWidth: 180, alignment: .leading)
            Button { model.close(session) } label: {
                Image(systemName: "xmark").font(.system(size: 9, weight: .bold))
            }
            .buttonStyle(.plain)
            .foregroundStyle(.secondary)
            .opacity(hover || active ? 1 : 0)
            .help(L("tab.close"))
        }
        .font(.callout)
        .padding(.horizontal, 10)
        .padding(.vertical, 5)
        .background(RoundedRectangle(cornerRadius: 6)
            .fill(active ? Color.accentColor.opacity(0.18) : (hover ? Color.primary.opacity(0.06) : .clear)))
        .contentShape(Rectangle())
        .onTapGesture { model.activeSessionID = session.id }
        .onHover { hover = $0 }
        .help(index < 9 ? "\(session.server.host)  ⌘\(index + 1)" : session.server.host)
        .contextMenu {
            Button(L("tab.reconnect")) { session.reconnect() }.disabled(session.isRunning)
            Button(L("tab.close")) { model.close(session) }
        }
    }
}

/// Zawartość aktywnej karty.
struct SessionContent: View {
    let tab: SessionTab

    var body: some View {
        switch tab {
        case .terminal(let t): SessionContainer(session: t)
        case .files(let f): FilesView(session: f)
        }
    }
}

/// Terminal sesji z nakładkami: ostrzeżenia, pytanie ssh (hasło itd.) i ekran końca połączenia.
struct SessionContainer: View {
    let session: TerminalSession

    var body: some View {
        VStack(spacing: 0) {
            ForEach(session.warnings, id: \.self) { w in
                Label(L(w), systemImage: "exclamationmark.triangle.fill")
                    .font(.callout)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.horizontal, 12).padding(.vertical, 6)
                    .background(Color.orange.opacity(0.15))
            }
            ZStack {
                TerminalHost(session: session)
                if let p = session.auth.prompt {
                    Color.black.opacity(0.35)
                    PromptCard(prompt: p, serverName: session.server.displayName)
                        .id(p.id)
                } else if case .ended(let code) = session.state {
                    EndedCard(session: session, code: code)
                }
            }
            .overlay(alignment: .top) { NoticeBadge(auth: session.auth) }
        }
        .background(Color(nsColor: TerminalAppearance.background))
        .navigationTitle(session.title)
        .navigationSubtitle(session.server.username.isEmpty ? session.server.host
                            : "\(session.server.username)@\(session.server.host)")
    }
}

/// Osadza widok terminala sesji. Widok żyje w sesji (proces działa także na niewidocznej karcie),
/// a ten kontener tylko go podpina.
struct TerminalHost: NSViewRepresentable {
    let session: TerminalSession

    func makeNSView(context: Context) -> NSView {
        let container = NSView()
        container.wantsLayer = true
        container.layer?.backgroundColor = TerminalAppearance.background.cgColor
        attach(to: container)
        return container
    }

    func updateNSView(_ container: NSView, context: Context) {
        attach(to: container)
    }

    /// Margines wokół tekstu — bez niego pierwsza kolumna dotyka krawędzi okna.
    static let inset = NSSize(width: 10, height: 6)

    private func attach(to container: NSView) {
        let tv = session.view
        if tv.superview !== container {
            tv.removeFromSuperview()
            // Ograniczenia, nie ramka z autoresizing: kontener przy podpięciu ma jeszcze rozmiar 0×0,
            // a ramka „0×0 minus margines" jest zdegenerowana — terminal liczył z niej 1–2 wiersze.
            tv.translatesAutoresizingMaskIntoConstraints = false
            container.addSubview(tv)
            NSLayoutConstraint.activate([
                tv.leadingAnchor.constraint(equalTo: container.leadingAnchor, constant: Self.inset.width),
                tv.trailingAnchor.constraint(equalTo: container.trailingAnchor, constant: -Self.inset.width),
                tv.topAnchor.constraint(equalTo: container.topAnchor, constant: Self.inset.height),
                tv.bottomAnchor.constraint(equalTo: container.bottomAnchor, constant: -Self.inset.height),
            ])
        }
        if session.auth.prompt == nil {
            DispatchQueue.main.async { tv.window?.makeFirstResponder(tv) }
        }
    }
}

struct PromptCard: View {
    let prompt: PendingPrompt
    let serverName: String
    @State private var value = ""
    @State private var save = true
    @FocusState private var focused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Label(heading, systemImage: icon).font(.headline)
            if prompt.isRetry {
                Label(L("prompt.retry"), systemImage: "xmark.octagon.fill")
                    .foregroundStyle(.red)
                    .font(.callout)
            }
            Text(prompt.text.trimmingCharacters(in: .whitespacesAndNewlines))
                .font(.system(.callout, design: .monospaced))
                .foregroundStyle(.secondary)
                .textSelection(.enabled)
                .fixedSize(horizontal: false, vertical: true)

            if prompt.kind == .confirm {
                HStack {
                    Spacer()
                    Button(L("prompt.no")) { prompt.answer("no", false) }
                        .keyboardShortcut(.cancelAction)
                    Button(L("prompt.yes")) { prompt.answer("yes", false) }
                        .keyboardShortcut(.defaultAction)
                }
            } else {
                Group {
                    if prompt.kind == .other {
                        TextField("", text: $value)
                    } else {
                        SecureField("", text: $value)
                    }
                }
                .textFieldStyle(.roundedBorder)
                .focused($focused)
                .onSubmit(submit)

                HStack {
                    if prompt.kind == .password {
                        Toggle(L("prompt.save"), isOn: $save)
                    }
                    Spacer()
                    Button(L("btn.cancel")) { prompt.answer(nil, false) }
                        .keyboardShortcut(.cancelAction)
                    Button(L("prompt.login"), action: submit)
                        .keyboardShortcut(.defaultAction)
                }
            }
        }
        .padding(20)
        .frame(width: 420)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12))
        .shadow(radius: 20)
        .onAppear { focused = true }
    }

    private func submit() { prompt.answer(value, save && prompt.kind == .password) }

    private var heading: String {
        switch prompt.kind {
        case .password: return String(format: L("prompt.password"), serverName)
        case .passphrase: return L("prompt.passphrase")
        case .confirm: return L("prompt.hostkey")
        case .other: return String(format: L("prompt.other"), serverName)
        }
    }

    private var icon: String {
        switch prompt.kind {
        case .password, .passphrase: return "key.fill"
        case .confirm: return "checkmark.shield"
        case .other: return "number"
        }
    }
}

private struct EndedCard: View {
    @Environment(AppModel.self) private var model
    let session: TerminalSession
    let code: Int32?

    var body: some View {
        VStack(spacing: 12) {
            Text(code == 0 ? L("ended.clean") : String(format: L("ended.code"), code.map(String.init) ?? "?"))
                .font(.headline)
            HStack {
                Button(L("tab.close")) { model.close(.terminal(session)) }
                Button(L("tab.reconnect")) { session.reconnect() }
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(18)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12))
        .frame(maxHeight: .infinity, alignment: .bottom)
        .padding(.bottom, 24)
    }
}

/// Krótka informacja o zapisie hasła nad kartą (znika po 3 s).
struct NoticeBadge: View {
    let auth: AuthBroker

    var body: some View {
        if let n = auth.notice {
            Text(n)
                .font(.callout)
                .padding(.horizontal, 12).padding(.vertical, 6)
                .background(.regularMaterial, in: Capsule())
                .padding(.top, 10)
                .task(id: n) {
                    try? await Task.sleep(for: .seconds(3))
                    auth.notice = nil
                }
        }
    }
}
