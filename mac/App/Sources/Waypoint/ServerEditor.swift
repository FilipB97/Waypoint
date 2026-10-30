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
    /// Nowe hasło do Pęku kluczy (puste = bez zmian). Importy z Windows nie niosą haseł — tu je uzupełnisz.
    @State private var password = ""
    @State private var hasPassword = false
    @State private var profilesOpen = false
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
                    switch draft.proto {
                    case .serial?:
                        HStack {
                            TextField(L("f.device"), text: $draft.host, prompt: Text("/dev/cu.usbserial-0001"))
                            Menu {
                                let devs = SerialPort.devices()
                                if devs.isEmpty { Text(L("f.device.none")) }
                                ForEach(devs, id: \.self) { d in Button(d) { draft.host = d } }
                            } label: { Image(systemName: "cable.connector") }
                                .menuStyle(.borderlessButton)
                                .fixedSize()
                                .help(L("f.device.pick"))
                        }
                        Picker(L("f.baud"), selection: $draft.port) {
                            ForEach(SerialPort.commonBauds, id: \.self) { Text(String($0)).tag($0) }
                            if !SerialPort.commonBauds.contains(draft.port) { Text(String(draft.port)).tag(draft.port) }
                        }
                    case .http?:
                        TextField(L("f.url"), text: $draft.host, prompt: Text("https://grafana.example.com"))
                    case .rest?:
                        TextField(L("rest.baseurl"), text: $draft.host, prompt: Text("https://api.example.com"))
                    default:
                        TextField(L("f.host"), text: $draft.host, prompt: Text("example.com"))
                        TextField(L("f.port"), value: $draft.port, format: .number.grouping(.never))
                        TextField(L("f.mac"), text: $draft.macAddress, prompt: Text(L("f.mac.ph")))
                    }
                }
                if draft.usesCredentials {
                Section {
                    HStack {
                        Picker(L("f.profile"), selection: $draft.credentialProfileId) {
                            Text(L("f.profile.none")).tag("")
                            ForEach(model.profiles) { p in Text("\(p.displayName) — \(p.login)").tag(p.id) }
                            if !draft.credentialProfileId.isEmpty && model.profile(for: draft) == nil {
                                Text(L("f.profile.missing")).tag(draft.credentialProfileId)
                            }
                        }
                        // Nowy profil bez zamykania edytora (arkusz nad arkuszem).
                        Button { profilesOpen = true } label: { Image(systemName: "person.badge.key") }
                            .help(L("cred.menu"))
                    }
                    if let p = model.profile(for: draft) {
                        LabeledContent(L("f.user"), value: p.login)
                    } else {
                        TextField(L("f.user"), text: $draft.username)
                        if draft.proto == .rdp {
                            TextField(L("f.domain"), text: $draft.domain)
                        }
                        if draft.proto != .serial {
                            SecureField(L("f.password"), text: $password,
                                        prompt: Text(L(hasPassword ? "f.password.saved" : "f.password.none")))
                        }
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
                            Text(L("f.ftpenc.auto")).tag(3)
                        }
                        Toggle(L("f.ftpanon"), isOn: $draft.ftpAnonymous)
                    }
                } header: {
                    Text(L("edit.sec.login"))
                } footer: {
                    Text(L(model.profile(for: draft) == nil ? "f.password.hint" : "f.profile.hint")).foregroundStyle(.secondary)
                }
                }
                if draft.proto == .rdp {
                    Section {
                        Picker(L("f.rdp.client"), selection: $draft.rdpUsesWindowsApp) {
                            Text(L("f.rdp.client.builtin")).tag(false)
                            Text(L("f.rdp.client.windowsapp")).tag(true)
                        }
                        Toggle(L("f.rdp.clipboard"), isOn: $draft.rdpRedirectClipboard)
                        Toggle(L("f.rdp.drives"), isOn: $draft.rdpRedirectDrives)
                        Toggle(L("f.rdp.admin"), isOn: $draft.rdpAdminSession)
                        Picker(L("f.rdp.auth"), selection: $draft.rdpAuthenticationLevel) {
                            Text(L("f.rdp.auth.warn")).tag(2)
                            Text(L("f.rdp.auth.require")).tag(1)
                            Text(L("f.rdp.auth.none")).tag(0)
                        }
                        TextField(L("f.rdp.gateway"), text: $draft.rdpGatewayHostname, prompt: Text("rdg.example.com"))
                        TextField(L("f.rdp.app"), text: $draft.rdpRemoteAppProgram, prompt: Text(L("f.rdp.app.ph")))
                    } header: {
                        Text(L("edit.sec.rdp"))
                    } footer: {
                        Text(L("f.rdp.hint")).foregroundStyle(.secondary)
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
                Button(isNew ? L("btn.add") : L("btn.save")) { save() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(!problems.isEmpty)
            }
            .padding(16)
        }
        .frame(width: 520, height: 600)
        .sheet(isPresented: $profilesOpen) { ProfileManagerView() }
        .task(id: draft.credentialProfileId) { hasPassword = Keychain.hasPassword(for: draft.keychainAccount) }
    }

    private func save() {
        let s = result
        // Hasło serwera (nie profilu — to ustawia się w profilu) prosto do Pęku kluczy.
        if !password.isEmpty, s.credentialProfileId.isEmpty, s.usesCredentials {
            _ = Keychain.save(password, for: s.keychainAccount, label: SessionPasswords.label(s))
            SessionPasswords.clear(s)
        }
        model.commit(s)
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
