import AppKit
import SwiftUI
import WaypointCore

/// Arkusz dodawania/edycji serwera. Pracuje na kopii — zmiany trafiają do listy dopiero po „Zapisz".
struct ServerEditor: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @State private var draft: Server
    @State private var tagsText: String
    @State private var tunnelsText: String
    private let isNew: Bool

    init(server: Server, isNew: Bool) {
        _draft = State(initialValue: server)
        _tagsText = State(initialValue: server.tags.joined(separator: ", "))
        _tunnelsText = State(initialValue: server.tunnels.joined(separator: "\n"))
        self.isNew = isNew
    }

    private var result: Server {
        var s = draft
        s.tags = tagsText.split(separator: ",").map(String.init)
        s.tunnels = tunnelsText.split(whereSeparator: \.isNewline).map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty }
        return s
    }

    private var problems: [String] { ServerValidation.problems(ServerValidation.normalized(result)) }

    var body: some View {
        VStack(spacing: 0) {
            Form {
                Section {
                    TextField(L("f.name"), text: $draft.name, prompt: Text(L("f.name.ph")))
                    Picker(L("f.protocol"), selection: protoBinding) {
                        ForEach(RemoteProtocol.allCases, id: \.self) { p in
                            Text(p.supportedOnMac ? p.badge : p.badge + " — " + L("f.protocol.win")).tag(p)
                        }
                    }
                    TextField(L("f.host"), text: $draft.host, prompt: Text("example.com"))
                    TextField(L("f.port"), value: $draft.port, format: .number.grouping(.never))
                }
                Section(L("edit.sec.login")) {
                    TextField(L("f.user"), text: $draft.username)
                    if draft.proto == .rdp {
                        TextField(L("f.domain"), text: $draft.domain)
                    }
                    if draft.proto == .ssh || draft.proto == .sftp {
                        HStack {
                            TextField(L("f.key"), text: $draft.privateKeyPath, prompt: Text("~/.ssh/id_ed25519"))
                            Button(L("edit.choose")) { chooseKey() }
                        }
                    }
                    if draft.proto == .ftp {
                        Picker(L("f.ftpenc"), selection: $draft.ftpEncryption) {
                            Text(L("f.ftpenc.explicit")).tag(0)
                            Text(L("f.ftpenc.implicit")).tag(1)
                            Text(L("f.ftpenc.none")).tag(2)
                        }
                        Toggle(L("f.ftpanon"), isOn: $draft.ftpAnonymous)
                    }
                }
                Section(L("edit.sec.org")) {
                    HStack {
                        TextField(L("f.group"), text: $draft.group, prompt: Text(L("f.group.ph")))
                        // Istniejące grupy jednym kliknięciem — literówka w nazwie tworzyłaby nową grupę.
                        if !model.groupNames.isEmpty {
                            Menu {
                                ForEach(model.groupNames, id: \.self) { g in Button(g) { draft.group = g } }
                            } label: { Image(systemName: "chevron.up.chevron.down") }
                                .menuStyle(.borderlessButton)
                                .fixedSize()
                                .help(L("f.group.pick"))
                        }
                    }
                    TextField(L("f.tags"), text: $tagsText, prompt: Text(L("f.tags.ph")))
                    Toggle(L("f.pinned"), isOn: $draft.pinned)
                }
                if draft.proto == .ssh {
                    Section {
                        TextField(L("f.tunnels"), text: $tunnelsText, prompt: Text("8080:localhost:80"), axis: .vertical)
                            .lineLimit(2...5)
                    } footer: {
                        Text(L("f.tunnels.hint")).foregroundStyle(.secondary)
                    }
                }
                Section(L("f.notes")) {
                    TextField(L("f.notes"), text: $draft.notes, axis: .vertical)
                        .lineLimit(3...8)
                        .labelsHidden()
                }
            }
            .formStyle(.grouped)

            Divider()
            HStack {
                if let first = problems.first {
                    Label(L(first), systemImage: "exclamationmark.triangle.fill")
                        .foregroundStyle(.orange)
                        .font(.callout)
                }
                Spacer()
                Button(L("btn.cancel"), role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button(isNew ? L("btn.add") : L("btn.save")) { model.commit(result) }
                    .keyboardShortcut(.defaultAction)
                    .disabled(!problems.isEmpty)
            }
            .padding(16)
        }
        .frame(width: 520, height: 600)
    }

    /// Zmiana protokołu podmienia port tylko wtedy, gdy był domyślny dla poprzedniego —
    /// ręcznie wpisany port (np. SSH na 2222) zostaje.
    private var protoBinding: Binding<RemoteProtocol> {
        Binding(
            get: { draft.proto ?? .ssh },
            set: { newValue in
                if draft.port == (draft.proto?.defaultPort ?? -1) { draft.port = newValue.defaultPort }
                draft.proto = newValue
            })
    }

    private func chooseKey() {
        let panel = NSOpenPanel()
        panel.directoryURL = URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent(".ssh")
        panel.showsHiddenFiles = true
        panel.canChooseDirectories = false
        if panel.runModal() == .OK, let url = panel.url { draft.privateKeyPath = url.path }
    }
}
