import AppKit
import SwiftUI
import WaypointCore

/// Menedżer profili poświadczeń: wspólny login dla wielu serwerów (hasło w Pęku kluczy).
struct ProfileManagerView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @State private var list: [CredentialProfile] = []
    @State private var selection: CredentialProfile.ID?
    /// Hasła wpisane w tym arkuszu (id → hasło); zapisywane dopiero przy „Zapisz".
    @State private var passwords: [String: String] = [:]
    @State private var generatorFor: String?

    var body: some View {
        VStack(spacing: 0) {
            HSplitView {
                List(selection: $selection) {
                    ForEach(list) { p in
                        VStack(alignment: .leading, spacing: 1) {
                            Text(p.displayName.isEmpty ? L("cred.new") : p.displayName)
                            Text(String(format: L("cred.used"), model.serversUsing(p)))
                                .font(.caption).foregroundStyle(.secondary)
                        }
                        .tag(p.id)
                    }
                }
                .frame(minWidth: 190, idealWidth: 210)
                editor.frame(minWidth: 360)
            }
            Divider()
            HStack {
                Button { let p = CredentialProfile(); list.append(p); selection = p.id } label: { Image(systemName: "plus") }
                    .help(L("cred.add"))
                Button { list.removeAll { $0.id == selection }; selection = list.first?.id } label: { Image(systemName: "minus") }
                    .disabled(selection == nil)
                    .help(L("cred.remove"))
                Button(L("cred.import")) {
                    if let imported = model.importProfilesFile() {
                        list = CredentialProfileStore.merge(list, imported).list
                        selection = imported.first?.id
                    }
                }
                Spacer()
                Button(L("btn.cancel"), role: .cancel) { dismiss() }.keyboardShortcut(.cancelAction)
                Button(L("btn.save")) { model.saveProfiles(list, passwords: passwords); dismiss() }
                    .keyboardShortcut(.defaultAction)
            }
            .padding(12)
        }
        .frame(width: 720, height: 440)
        .onAppear { list = model.profiles; selection = list.first?.id }
        .sheet(item: Binding(get: { generatorFor.map(GenTarget.init) }, set: { generatorFor = $0?.id })) { t in
            PasswordGeneratorView { passwords[t.id] = $0 }
        }
    }

    private struct GenTarget: Identifiable { let id: String }

    @ViewBuilder private var editor: some View {
        if let i = list.firstIndex(where: { $0.id == selection }) {
            let p = list[i]
            Form {
                TextField(L("cred.name"), text: $list[i].name, prompt: Text(L("cred.name.ph")))
                TextField(L("f.domain"), text: $list[i].domain, prompt: Text(L("cred.domain.ph")))
                TextField(L("f.user"), text: $list[i].username)
                HStack {
                    SecureField(L("cred.password"), text: Binding(get: { passwords[p.id] ?? "" }, set: { passwords[p.id] = $0 }),
                                prompt: Text(Keychain.hasPassword(for: p.keychainAccount) ? L("cred.password.keep") : ""))
                    Button { generatorFor = p.id } label: { Image(systemName: "key.viewfinder") }
                        .help(L("gen.title"))
                }
                if Keychain.hasPassword(for: p.keychainAccount) && passwords[p.id] == nil {
                    Button(L("cred.password.forget")) { passwords[p.id] = "" }.buttonStyle(.link)
                }
                Text(String(format: L("cred.used.long"), model.serversUsing(p)))
                    .font(.callout).foregroundStyle(.secondary)
            }
            .formStyle(.grouped)
        } else {
            ContentUnavailableView(L("cred.empty"), systemImage: "person.badge.key", description: Text(L("cred.empty.desc")))
        }
    }
}

/// Generator haseł, tokenów hex i GUID — jak w Windows. `onUse` (opcjonalnie) wstawia wynik do pola.
struct PasswordGeneratorView: View {
    enum Mode: String, CaseIterable, Identifiable { case password, hex, guid; var id: String { rawValue } }

    @Environment(\.dismiss) private var dismiss
    var onUse: ((String) -> Void)? = nil
    @State private var mode = Mode.password
    @State private var opts = PasswordGen.Options()
    @State private var hexBytes = 32
    @State private var value = ""
    @State private var copied = false

    var body: some View {
        VStack(spacing: 0) {
            Form {
                Picker(L("gen.mode"), selection: $mode) {
                    Text(L("gen.mode.password")).tag(Mode.password)
                    Text(L("gen.mode.hex")).tag(Mode.hex)
                    Text(L("gen.mode.guid")).tag(Mode.guid)
                }
                .pickerStyle(.segmented)
                switch mode {
                case .password:
                    LabeledContent(String(format: L("gen.length"), opts.length)) {
                        Slider(value: Binding(get: { Double(opts.length) }, set: { opts.length = Int($0) }), in: 4...128, step: 1)
                    }
                    Toggle(L("gen.upper"), isOn: $opts.upper)
                    Toggle(L("gen.lower"), isOn: $opts.lower)
                    Toggle(L("gen.digits"), isOn: $opts.digits)
                    Toggle(L("gen.symbols"), isOn: $opts.symbols)
                    Toggle(L("gen.ambiguous"), isOn: $opts.excludeAmbiguous)
                case .hex:
                    Picker(L("gen.bytes"), selection: $hexBytes) {
                        ForEach([16, 24, 32, 48, 64], id: \.self) { Text(String(format: L("gen.bytes.n"), $0, $0 * 8)).tag($0) }
                    }
                case .guid:
                    EmptyView()
                }
                Section {
                    Text(value.isEmpty ? L("gen.noclass") : value)
                        .font(.system(.body, design: .monospaced))
                        .textSelection(.enabled)
                        .lineLimit(4)
                        .frame(maxWidth: .infinity, alignment: .leading)
                    if mode == .password, !value.isEmpty {
                        let bits = PasswordGen.entropyBits(length: opts.length, poolSize: PasswordGen.pool(opts).count)
                        Label(String(format: L("gen.entropy"), Int(bits.rounded()), strength(bits)), systemImage: "lock.shield")
                            .font(.callout).foregroundStyle(.secondary)
                    }
                }
            }
            .formStyle(.grouped)
            Divider()
            HStack {
                Button { generate() } label: { Label(L("gen.again"), systemImage: "arrow.clockwise") }
                Button { copy() } label: { Label(copied ? L("gen.copied") : L("gen.copy"), systemImage: "doc.on.doc") }
                    .disabled(value.isEmpty)
                Spacer()
                Button(onUse == nil ? L("btn.close") : L("btn.cancel"), role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                if let onUse {
                    Button(L("gen.use")) { onUse(value); dismiss() }
                        .keyboardShortcut(.defaultAction)
                        .disabled(value.isEmpty)
                }
            }
            .padding(12)
        }
        .frame(width: 480)
        .onAppear(perform: generate)
        .onChange(of: mode) { generate() }
        .onChange(of: opts) { generate() }
        .onChange(of: hexBytes) { generate() }
    }

    private func generate() {
        copied = false
        switch mode {
        case .password: value = PasswordGen.password(opts)
        case .hex: value = PasswordGen.hexToken(bytes: hexBytes)
        case .guid: value = PasswordGen.guid()
        }
    }

    private func copy() {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(value, forType: .string)
        copied = true
    }

    private func strength(_ bits: Double) -> String {
        bits < 50 ? L("gen.weak") : bits < 80 ? L("gen.ok") : L("gen.strong")
    }
}

/// „Połącz jako…" — inny login i hasło dla jednego połączenia (albo zapamiętane dla serwera).
struct ConnectAsView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let server: Server
    @State private var login: String
    @State private var password = ""
    @State private var remember = false

    init(server: Server, login: String) {
        self.server = server
        _login = State(initialValue: login)
    }

    var body: some View {
        VStack(spacing: 0) {
            Form {
                Section {
                    TextField(L("connas.login"), text: $login, prompt: Text(server.proto == .rdp ? "DOMENA\\user" : "user"))
                    if server.proto != .rdp {
                        SecureField(L("cred.password"), text: $password, prompt: Text(L("connas.password.ph")))
                    }
                    Toggle(L("connas.remember"), isOn: $remember)
                } header: {
                    Text(String(format: L("connas.title"), server.displayName))
                } footer: {
                    Text(server.proto == .rdp ? L("connas.hint.rdp") : L("connas.hint")).foregroundStyle(.secondary)
                }
            }
            .formStyle(.grouped)
            Divider()
            HStack {
                Spacer()
                Button(L("btn.cancel"), role: .cancel) { dismiss() }.keyboardShortcut(.cancelAction)
                Button(L("act.connect")) {
                    model.connectAs(server, login: login, password: password, remember: remember)
                    dismiss()
                }
                .keyboardShortcut(.defaultAction)
                .disabled(login.trimmingCharacters(in: .whitespaces).isEmpty)
            }
            .padding(12)
        }
        .frame(width: 440)
    }
}
