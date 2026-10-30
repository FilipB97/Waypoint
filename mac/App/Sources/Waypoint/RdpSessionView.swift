import AppKit
import SwiftUI
import WaypointCore
import WaypointRDP

/// Karta pulpitu zdalnego: obraz sesji z nakładkami — certyfikat serwera, hasło, koniec połączenia.
struct RdpSessionView: View {
    @Environment(AppModel.self) private var model
    @Bindable var session: RdpSession

    var body: some View {
        ZStack {
            RdpHost(session: session)
            if let q = session.certificate {
                Color.black.opacity(0.35)
                RdpCertificateCard(question: q).id(q.id)
            } else if let p = session.prompt {
                Color.black.opacity(0.35)
                PromptCard(prompt: p, serverName: session.server.displayName).id(p.id)
            } else if session.state == .connecting {
                VStack(spacing: 10) {
                    ProgressView()
                    Text(String(format: L("rdp.connecting"), session.server.host))
                        .foregroundStyle(.secondary)
                }
                .padding(18)
                .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12))
            } else if case .ended(let message) = session.state {
                VStack(spacing: 12) {
                    Text(message ?? L("rdp.ended")).font(.headline)
                        .multilineTextAlignment(.center)
                        .fixedSize(horizontal: false, vertical: true)
                    HStack {
                        Button(L("tab.close")) { model.close(.rdp(session)) }
                        Button(L("tab.reconnect")) { session.reconnect() }
                            .keyboardShortcut(.defaultAction)
                    }
                }
                .padding(18)
                .frame(maxWidth: 460)
                .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12))
                .frame(maxHeight: .infinity, alignment: .bottom)
                .padding(.bottom, 24)
            }
        }
        .overlay(alignment: .top) {
            if let n = session.notice {
                Text(n)
                    .font(.callout)
                    .padding(.horizontal, 12).padding(.vertical, 6)
                    .background(.regularMaterial, in: Capsule())
                    .padding(.top, 10)
                    .task(id: n) {
                        try? await Task.sleep(for: .seconds(3))
                        session.notice = nil
                    }
            }
        }
        .background(Color.black)
        .navigationTitle(session.title)
        .navigationSubtitle(session.server.username.isEmpty ? session.server.host
                            : "\(session.server.username)@\(session.server.host)")
    }
}

/// Osadza widok pulpitu sesji (żyje w sesji, jak terminal — przełączenie karty go nie niszczy).
private struct RdpHost: NSViewRepresentable {
    let session: RdpSession

    func makeNSView(context: Context) -> NSView {
        let container = NSView()
        attach(to: container)
        return container
    }

    func updateNSView(_ container: NSView, context: Context) { attach(to: container) }

    private func attach(to container: NSView) {
        let v = session.view
        guard v.superview !== container else { return }
        v.removeFromSuperview()
        v.translatesAutoresizingMaskIntoConstraints = false
        container.addSubview(v)
        NSLayoutConstraint.activate([
            v.leadingAnchor.constraint(equalTo: container.leadingAnchor),
            v.trailingAnchor.constraint(equalTo: container.trailingAnchor),
            v.topAnchor.constraint(equalTo: container.topAnchor),
            v.bottomAnchor.constraint(equalTo: container.bottomAnchor),
        ])
        if session.state == .connected {
            DispatchQueue.main.async { v.window?.makeFirstResponder(v) }
        }
    }
}

/// Nieznany (albo zmieniony) certyfikat serwera — zaufanie przy pierwszym połączeniu, jak mstsc.
private struct RdpCertificateCard: View {
    let question: RdpSession.CertificateQuestion

    var body: some View {
        let c = question.info
        VStack(alignment: .leading, spacing: 12) {
            Label(c.changed ? L("rdp.cert.changed") : L("rdp.cert.unknown"),
                  systemImage: c.changed ? "exclamationmark.shield.fill" : "checkmark.shield")
                .font(.headline)
                .foregroundStyle(c.changed ? Color.red : Color.primary)
            Text(String(format: L(c.changed ? "rdp.cert.changed.msg" : "rdp.cert.unknown.msg"), "\(c.host):\(c.port)"))
                .font(.callout)
                .fixedSize(horizontal: false, vertical: true)
            Grid(alignment: .leading, horizontalSpacing: 10, verticalSpacing: 4) {
                row(L("rdp.cert.subject"), c.subject.isEmpty ? c.commonName : c.subject)
                row(L("rdp.cert.issuer"), c.issuer)
                row(L("rdp.cert.fingerprint"), c.fingerprint)
                if c.changed && !c.oldFingerprint.isEmpty { row(L("rdp.cert.old"), c.oldFingerprint) }
            }
            .font(.system(.caption, design: .monospaced))
            HStack {
                Spacer()
                Button(L("rdp.cert.reject")) { question.answer(.reject) }
                    .keyboardShortcut(.cancelAction)
                Button(L("rdp.cert.once")) { question.answer(.acceptOnce) }
                Button(L("rdp.cert.always")) { question.answer(.acceptAlways) }
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
        .frame(width: 520)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12))
        .shadow(radius: 20)
    }

    @ViewBuilder private func row(_ k: String, _ v: String) -> some View {
        GridRow {
            Text(k).foregroundStyle(.secondary)
            Text(v).textSelection(.enabled).lineLimit(3)
        }
    }
}
