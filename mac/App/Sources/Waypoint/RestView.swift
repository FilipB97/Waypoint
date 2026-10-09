import AppKit
import SwiftUI
import UniformTypeIdentifiers
import WaypointCore

/// Konsola REST w karcie: drzewo kolekcji po lewej, żądanie i odpowiedź po prawej (jak RestConsole w Windows).
struct RestView: View {
    @Environment(AppModel.self) private var model
    @Bindable var session: RestSession
    @State private var envEditorOpen = false
    @State private var collectionAuthOpen = false
    @State private var folderAuth: RestFolder?
    @State private var renamingFolder: RestFolder?
    @State private var renameText = ""

    var body: some View {
        HSplitView {
            sidebar.frame(minWidth: 180, idealWidth: 240, maxWidth: 420)
            Group {
                if let i = session.selectedIndex {
                    RestRequestPane(session: session, index: i)
                } else {
                    ContentUnavailableView {
                        Label(L("rest.empty"), systemImage: "curlybraces")
                    } description: {
                        Text(L("rest.empty.desc"))
                    } actions: {
                        Button(L("rest.newrequest")) { session.addRequest() }.buttonStyle(.borderedProminent)
                    }
                }
            }
            .frame(minWidth: 360, maxWidth: .infinity, maxHeight: .infinity)
        }
        .navigationTitle(session.server.displayName)
        .navigationSubtitle(session.collection.baseUrl)
        .onChange(of: session.selectedId) { session.save() }
        .sheet(isPresented: $envEditorOpen) { RestEnvironmentEditor(session: session) }
        .sheet(isPresented: $collectionAuthOpen) {
            RestAuthSheet(session: session, title: session.server.displayName, allowInherit: false,
                          type: $session.collection.authType, username: $session.collection.authUsername,
                          account: session.collectionAccount, baseUrl: $session.collection.baseUrl)
        }
        .sheet(item: $folderAuth) { f in
            if let i = session.collection.folders.firstIndex(where: { $0.id == f.id }) {
                RestAuthSheet(session: session, title: f.name, allowInherit: true,
                              type: $session.collection.folders[i].authType, username: $session.collection.folders[i].authUsername,
                              account: f.keychainAccount, baseUrl: nil)
            }
        }
        .alert(L("rest.folder.rename"), isPresented: Binding(get: { renamingFolder != nil }, set: { if !$0 { renamingFolder = nil } })) {
            TextField("", text: $renameText)
            Button(L("btn.cancel"), role: .cancel) {}
            Button(L("btn.save")) {
                if let f = renamingFolder, let i = session.collection.folders.firstIndex(where: { $0.id == f.id }) {
                    session.collection.folders[i].name = renameText
                    session.dirty = true
                }
            }
        }
    }

    // MARK: Drzewo i środowisko

    private var sidebar: some View {
        VStack(spacing: 0) {
            HStack(spacing: 4) {
                Picker(L("rest.env"), selection: $session.activeEnvironmentId) {
                    Text(L("rest.env.none")).tag("")
                    ForEach(session.environments) { e in Text(e.name.isEmpty ? L("rest.env.unnamed") : e.name).tag(e.id) }
                }
                .labelsHidden()
                .help(L("rest.env"))
                Button { envEditorOpen = true } label: { Image(systemName: "slider.horizontal.3") }.help(L("rest.env.edit"))
            }
            .buttonStyle(.borderless)
            .padding(.horizontal, 8).padding(.vertical, 6)
            Divider()
            List(selection: $session.selectedId) {
                RestTreeLevel(session: session, folderId: "", onFolderAuth: { folderAuth = $0 },
                              onRename: { renameText = $0.name; renamingFolder = $0 })
                if !session.collection.history.isEmpty {
                    Section(L("rest.history")) {
                        ForEach(Array(session.collection.history.prefix(15).enumerated()), id: \.offset) { _, h in
                            VStack(alignment: .leading, spacing: 1) {
                                Text("\(h.method) \(h.url)").lineLimit(1).font(.caption)
                                Text("\(h.status > 0 ? String(h.status) : "—") · \(h.elapsedMs) ms")
                                    .font(.caption2).foregroundStyle(statusColor(h.status))
                            }
                            .selectionDisabled()
                        }
                    }
                }
            }
            .listStyle(.sidebar)
            Divider()
            HStack(spacing: 8) {
                Button { session.addRequest() } label: { Image(systemName: "plus") }.help(L("rest.newrequest"))
                Button { session.addFolder() } label: { Image(systemName: "folder.badge.plus") }.help(L("rest.newfolder"))
                Spacer()
                Button { collectionAuthOpen = true } label: { Image(systemName: "key") }.help(L("rest.coll.settings"))
                Button { session.save() } label: { Image(systemName: "square.and.arrow.down") }
                    .help(L("rest.save")).disabled(!session.dirty)
                    .keyboardShortcut("s")
            }
            .buttonStyle(.borderless)
            .padding(8)
        }
    }
}

func statusColor(_ code: Int) -> Color {
    switch code {
    case 200..<300: return .green
    case 300..<400: return .blue
    case 400..<500: return .orange
    case 500...: return .red
    default: return .secondary
    }
}

/// Jeden poziom drzewa: podfoldery (rozwijane, rekurencyjnie) i żądania.
private struct RestTreeLevel: View {
    @Bindable var session: RestSession
    let folderId: String
    let onFolderAuth: (RestFolder) -> Void
    let onRename: (RestFolder) -> Void

    var body: some View {
        ForEach(session.collection.folders.filter { $0.parentId == folderId }) { f in
            DisclosureGroup {
                RestTreeLevel(session: session, folderId: f.id, onFolderAuth: onFolderAuth, onRename: onRename)
            } label: {
                Label(f.name.isEmpty ? L("rest.newfolder") : f.name, systemImage: "folder")
                    .contextMenu {
                        Button(L("rest.newrequest")) { session.addRequest(in: f.id) }
                        Button(L("rest.newfolder")) { session.addFolder(in: f.id) }
                        Divider()
                        Button(L("rest.folder.rename")) { onRename(f) }
                        Button(L("rest.folder.auth")) { onFolderAuth(f) }
                        Divider()
                        Button(L("act.delete"), role: .destructive) { session.delete(f) }
                    }
            }
            .selectionDisabled()
        }
        ForEach(session.collection.requests.filter { $0.folderId == folderId }) { r in
            HStack(spacing: 6) {
                Text(r.method.uppercased())
                    .font(.system(size: 9, weight: .bold, design: .monospaced))
                    .foregroundStyle(methodColor(r.method))
                    .frame(width: 42, alignment: .leading)
                Text(r.name.isEmpty ? r.url : r.name).lineLimit(1)
            }
            .tag(r.id)
            .contextMenu {
                Button(L("act.duplicate")) { session.duplicate(r) }
                Button(L("act.delete"), role: .destructive) { session.delete(r) }
            }
        }
    }

    private func methodColor(_ m: String) -> Color {
        switch m.uppercased() {
        case "GET": return .green
        case "POST": return .orange
        case "PUT", "PATCH": return .blue
        case "DELETE": return .red
        default: return .secondary
        }
    }
}

/// Żądanie (górna część) i odpowiedź (dolna).
private struct RestRequestPane: View {
    @Bindable var session: RestSession
    let index: Int
    @State private var tab = "params"
    @State private var responseTab = "body"

    private var req: Binding<RestRequest> { $session.collection.requests[index] }
    static let methods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"]

    var body: some View {
        VSplitView {
            requestEditor.frame(minHeight: 200)
            response.frame(minHeight: 160)
        }
        .onChange(of: session.collection.requests[index]) { session.dirty = true }
    }

    private var requestEditor: some View {
        VStack(alignment: .leading, spacing: 8) {
            TextField(L("rest.name"), text: req.name).textFieldStyle(.plain).font(.headline)
            HStack(spacing: 6) {
                Picker("", selection: req.method) {
                    ForEach(Self.methods, id: \.self) { Text($0).tag($0) }
                    if !Self.methods.contains(req.wrappedValue.method.uppercased()) { Text(req.wrappedValue.method).tag(req.wrappedValue.method) }
                }
                .labelsHidden().frame(width: 100)
                TextField("https://api.example.com/{{path}}", text: req.url)
                    .textFieldStyle(.roundedBorder)
                    .font(.system(.body, design: .monospaced))
                    .onSubmit { session.send() }
                if session.sending {
                    ProgressView().controlSize(.small).frame(width: 60)
                } else {
                    Button(L("rest.send")) { session.send() }
                        .buttonStyle(.borderedProminent)
                        .keyboardShortcut(.return, modifiers: .command)
                        .disabled(req.wrappedValue.url.trimmingCharacters(in: .whitespaces).isEmpty)
                }
            }
            let audit = session.missingVariables
            if !audit.missing.isEmpty || !audit.empty.isEmpty {
                Label(warning(audit), systemImage: "exclamationmark.triangle.fill")
                    .font(.caption).foregroundStyle(.orange)
            }
            Picker("", selection: $tab) {
                Text(String(format: L("rest.tab.params"), req.wrappedValue.queryParams.filter(\.enabled).count)).tag("params")
                Text(String(format: L("rest.tab.headers"), req.wrappedValue.headers.filter(\.enabled).count)).tag("headers")
                Text(L("rest.tab.body")).tag("body")
                Text(L("rest.tab.auth")).tag("auth")
                Text(L("rest.tab.scripts")).tag("scripts")
            }
            .pickerStyle(.segmented).labelsHidden()
            Group {
                switch tab {
                case "params": KeyValueEditor(rows: req.queryParams)
                case "headers": KeyValueEditor(rows: req.headers)
                case "body": bodyEditor
                case "auth": authEditor
                default: scriptsEditor
                }
            }
            .frame(maxHeight: .infinity, alignment: .top)
        }
        .padding(12)
    }

    private func warning(_ a: (missing: [String], empty: [String])) -> String {
        var parts: [String] = []
        if !a.missing.isEmpty { parts.append(String(format: L("rest.vars.missing"), a.missing.joined(separator: ", "))) }
        if !a.empty.isEmpty { parts.append(String(format: L("rest.vars.empty"), a.empty.joined(separator: ", "))) }
        return parts.joined(separator: " · ")
    }

    private static let contentTypes = ["application/json", "application/x-www-form-urlencoded", "text/plain", "application/xml", "text/html"]

    @ViewBuilder private var bodyEditor: some View {
        VStack(alignment: .leading, spacing: 6) {
            Picker(L("rest.body.type"), selection: req.bodyContentType) {
                ForEach(Self.contentTypes, id: \.self) { Text($0).tag($0) }
                if !Self.contentTypes.contains(req.wrappedValue.bodyContentType) {
                    Text(req.wrappedValue.bodyContentType).tag(req.wrappedValue.bodyContentType)
                }
            }
            .frame(maxWidth: 380)
            if req.wrappedValue.bodyContentType.contains("x-www-form-urlencoded") {
                KeyValueEditor(rows: req.formFields)
            } else {
                TextEditor(text: req.body)
                    .font(.system(.body, design: .monospaced))
                    .autocorrectionDisabled()
                    .border(Color.secondary.opacity(0.3))
                HStack {
                    Button(L("rest.body.format")) { req.wrappedValue.body = RestHTTP.pretty(req.wrappedValue.body) }
                        .buttonStyle(.link)
                    if ["GET", "HEAD"].contains(req.wrappedValue.method.uppercased()) && !req.wrappedValue.body.isEmpty {
                        Text(L("rest.body.get")).font(.caption).foregroundStyle(.secondary)
                    }
                }
            }
        }
    }

    @ViewBuilder private var authEditor: some View {
        let r = req.wrappedValue
        Form {
            Picker(L("rest.auth.type"), selection: req.authType) {
                Text(L("rest.auth.inherit")).tag(RestAuthType.inherit.rawValue)
                Text(L("rest.auth.none")).tag(RestAuthType.none.rawValue)
                Text("Bearer").tag(RestAuthType.bearer.rawValue)
                Text("Basic").tag(RestAuthType.basic.rawValue)
            }
            switch r.authType {
            case RestAuthType.bearer.rawValue:
                SecureField(L("rest.auth.token"), text: secretBinding(r.keychainAccount), prompt: Text("{{token}}"))
            case RestAuthType.basic.rawValue:
                TextField(L("f.user"), text: req.authUsername)
                SecureField(L("cred.password"), text: secretBinding(r.keychainAccount))
            case RestAuthType.inherit.rawValue:
                let a = session.collection.resolveAuth(from: r.folderId, collectionAccount: session.collectionAccount)
                Text(String(format: L("rest.auth.inherited"), authName(a.type))).foregroundStyle(.secondary)
            default:
                EmptyView()
            }
        }
        .formStyle(.grouped)
    }

    private func authName(_ t: Int) -> String {
        t == RestAuthType.bearer.rawValue ? "Bearer" : t == RestAuthType.basic.rawValue ? "Basic" : L("rest.auth.none")
    }

    private func secretBinding(_ account: String) -> Binding<String> {
        Binding(get: { session.secret(account) }, set: { session.setSecret($0, for: account) })
    }

    @ViewBuilder private var scriptsEditor: some View {
        HStack(spacing: 8) {
            VStack(alignment: .leading, spacing: 4) {
                Text(L("rest.script.pre")).font(.caption).foregroundStyle(.secondary)
                TextEditor(text: req.preScript).font(.system(.callout, design: .monospaced)).autocorrectionDisabled()
                    .border(Color.secondary.opacity(0.3))
            }
            VStack(alignment: .leading, spacing: 4) {
                Text(L("rest.script.test")).font(.caption).foregroundStyle(.secondary)
                TextEditor(text: req.testScript).font(.system(.callout, design: .monospaced)).autocorrectionDisabled()
                    .border(Color.secondary.opacity(0.3))
            }
        }
    }

    // MARK: Odpowiedź

    private var response: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 12) {
                if let r = session.response {
                    if r.ok {
                        Text("\(r.status) \(r.reason)").font(.headline).foregroundStyle(statusColor(r.status))
                        Text("\(r.elapsedMs) ms").foregroundStyle(.secondary)
                        Text(ByteCountFormatter.string(fromByteCount: Int64(r.size), countStyle: .file)).foregroundStyle(.secondary)
                    } else {
                        Label(r.error, systemImage: "xmark.octagon.fill").foregroundStyle(.red).lineLimit(2)
                    }
                    let tests = session.testOutcome.tests
                    if !tests.isEmpty {
                        let passed = tests.filter(\.passed).count
                        Text(String(format: L("rest.tests.summary"), passed, tests.count))
                            .foregroundStyle(passed == tests.count ? .green : .orange)
                    }
                } else if session.sending {
                    Text(L("rest.sending")).foregroundStyle(.secondary)
                } else {
                    Text(L("rest.noresponse")).foregroundStyle(.secondary)
                }
                Spacer()
                Picker("", selection: $responseTab) {
                    Text(L("rest.resp.body")).tag("body")
                    Text(L("rest.resp.headers")).tag("headers")
                    Text(L("rest.resp.sent")).tag("sent")
                    Text(L("rest.resp.tests")).tag("tests")
                }
                .pickerStyle(.segmented).labelsHidden().frame(maxWidth: 320).layoutPriority(-1)
            }
            ScrollView(.vertical) {
                Text(responseText)
                    .font(.system(.callout, design: .monospaced))
                    .textSelection(.enabled)
                    .multilineTextAlignment(.leading)
                    .frame(maxWidth: .infinity, alignment: .topLeading)
                    .padding(8)
            }
            .background(Color(nsColor: .textBackgroundColor).opacity(0.6), in: RoundedRectangle(cornerRadius: 6))
            if session.response?.ok == true && responseTab == "body" {
                Button(L("rest.resp.copy")) {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(session.response?.body ?? "", forType: .string)
                }
                .buttonStyle(.link).font(.caption)
            }
        }
        .padding(12)
    }

    private var responseText: String {
        guard let r = session.response else { return "" }
        switch responseTab {
        case "headers":
            return r.headers.map { "\($0.0): \($0.1)" }.joined(separator: "\n")
        case "sent":
            guard let p = r.sent else { return "" }
            return (["\(p.method) \(p.url)"] + p.headers.map { "\($0.0): \(maskAuth($0))" } + (p.body.map { ["", $0] } ?? []))
                .joined(separator: "\n")
        case "tests":
            var lines: [String] = []
            for (label, o) in [(L("rest.script.pre"), session.preOutcome), (L("rest.script.test"), session.testOutcome)] where !o.isEmpty {
                lines.append("— \(label) —")
                if !o.ok { lines.append("✗ " + o.error) }
                lines += o.tests.map { ($0.passed ? "✓ " : "✗ ") + $0.name + ($0.error.isEmpty ? "" : " — " + $0.error) }
                lines += o.logs.map { "› " + $0 }
            }
            return lines.isEmpty ? L("rest.tests.none") : lines.joined(separator: "\n")
        default:
            let b = r.prettyBody
            return b.count > 1_000_000 ? String(b.prefix(1_000_000)) + "\n…" : b
        }
    }

    /// Sekret w nagłówku Authorization zakryty — zakładka „Wysłane" bywa pokazywana na zrzutach.
    private func maskAuth(_ h: (String, String)) -> String {
        guard h.0.caseInsensitiveCompare("Authorization") == .orderedSame, let sp = h.1.firstIndex(of: " ") else { return h.1 }
        return String(h.1[..<sp]) + " ••••••"
    }
}

/// Tabela klucz–wartość z przełącznikiem (parametry, nagłówki, pola formularza, zmienne).
struct KeyValueEditor: View {
    @Binding var rows: [RestKeyValue]

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            ScrollView {
                VStack(spacing: 4) {
                    ForEach($rows) { $row in
                        HStack(spacing: 6) {
                            Toggle("", isOn: $row.enabled).labelsHidden().toggleStyle(.checkbox)
                            TextField(L("rest.kv.key"), text: $row.key).textFieldStyle(.roundedBorder)
                            TextField(L("rest.kv.value"), text: $row.value).textFieldStyle(.roundedBorder)
                                .font(.system(.body, design: .monospaced))
                            Button { rows.removeAll { $0.id == row.id } } label: { Image(systemName: "minus.circle") }
                                .buttonStyle(.borderless)
                        }
                    }
                }
            }
            Button { rows.append(RestKeyValue()) } label: { Label(L("rest.kv.add"), systemImage: "plus") }
                .buttonStyle(.borderless)
        }
    }
}

/// Uwierzytelnianie kolekcji (z adresem bazowym) albo folderu (z „dziedzicz").
private struct RestAuthSheet: View {
    @Environment(\.dismiss) private var dismiss
    @Bindable var session: RestSession
    let title: String
    let allowInherit: Bool
    @Binding var type: Int
    @Binding var username: String
    let account: String
    var baseUrl: Binding<String>?

    var body: some View {
        VStack(spacing: 0) {
            Form {
                Section(title) {
                    if let baseUrl {
                        TextField(L("rest.baseurl"), text: baseUrl, prompt: Text("https://api.example.com"))
                        Text(L("rest.baseurl.hint")).font(.caption).foregroundStyle(.secondary)
                    }
                    Picker(L("rest.auth.type"), selection: $type) {
                        if allowInherit { Text(L("rest.auth.inherit")).tag(RestAuthType.inherit.rawValue) }
                        Text(L("rest.auth.none")).tag(RestAuthType.none.rawValue)
                        Text("Bearer").tag(RestAuthType.bearer.rawValue)
                        Text("Basic").tag(RestAuthType.basic.rawValue)
                    }
                    if type == RestAuthType.bearer.rawValue {
                        SecureField(L("rest.auth.token"), text: secret)
                    } else if type == RestAuthType.basic.rawValue {
                        TextField(L("f.user"), text: $username)
                        SecureField(L("cred.password"), text: secret)
                    }
                }
            }
            .formStyle(.grouped)
            Divider()
            HStack {
                Spacer()
                Button(L("btn.ok")) { session.dirty = true; session.save(); dismiss() }.keyboardShortcut(.defaultAction)
            }
            .padding(12)
        }
        .frame(width: 440)
    }

    private var secret: Binding<String> {
        Binding(get: { session.secret(account) }, set: { session.setSecret($0, for: account) })
    }
}

/// Środowiska (wspólne dla wszystkich kolekcji, jak w Windows) i ich zmienne.
private struct RestEnvironmentEditor: View {
    @Environment(\.dismiss) private var dismiss
    @Bindable var session: RestSession
    @State private var list: [RestEnvironment] = []
    @State private var selection: RestEnvironment.ID?

    var body: some View {
        VStack(spacing: 0) {
            HSplitView {
                List(selection: $selection) {
                    ForEach(list) { e in Text(e.name.isEmpty ? L("rest.env.unnamed") : e.name).tag(e.id) }
                }
                .frame(minWidth: 170, idealWidth: 190)
                if let i = list.firstIndex(where: { $0.id == selection }) {
                    VStack(alignment: .leading, spacing: 8) {
                        TextField(L("cred.name"), text: $list[i].name).textFieldStyle(.roundedBorder)
                        VariablesEditor(rows: $list[i].variables)
                        Text(L("rest.env.hint")).font(.caption).foregroundStyle(.secondary)
                    }
                    .padding(12)
                    .frame(minWidth: 380)
                } else {
                    ContentUnavailableView(L("rest.env.none.title"), systemImage: "slider.horizontal.3")
                        .frame(minWidth: 380)
                }
            }
            Divider()
            HStack {
                Button { let e = RestEnvironment(name: L("rest.env.new")); list.append(e); selection = e.id } label: { Image(systemName: "plus") }
                Button { list.removeAll { $0.id == selection }; selection = list.first?.id } label: { Image(systemName: "minus") }
                    .disabled(selection == nil)
                Button(L("rest.env.import")) { importPostmanEnvironment() }
                Spacer()
                Button(L("btn.cancel"), role: .cancel) { dismiss() }.keyboardShortcut(.cancelAction)
                Button(L("btn.save")) { session.saveEnvironments(list); dismiss() }.keyboardShortcut(.defaultAction)
            }
            .padding(12)
        }
        .frame(width: 680, height: 420)
        .onAppear { list = session.environments; selection = session.activeEnvironmentId.isEmpty ? list.first?.id : session.activeEnvironmentId }
    }

    private func importPostmanEnvironment() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.json]
        guard panel.runModal() == .OK, let url = panel.url, let data = try? Data(contentsOf: url),
              let (env, blanked) = try? PostmanImport.parseEnvironment(data) else { return }
        list.append(env)
        selection = env.id
        if !blanked.isEmpty {
            AppModel.shared.alert = AppAlert(title: L("rest.env.import"), message: String(format: L("rest.env.secrets"), blanked.joined(separator: ", ")))
        }
    }
}

/// Zmienne środowiska: klucz i wartość (bez przełącznika — jak w Windows).
private struct VariablesEditor: View {
    @Binding var rows: [RestVariable]

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            ScrollView {
                VStack(spacing: 4) {
                    ForEach($rows) { $row in
                        HStack(spacing: 6) {
                            TextField(L("rest.kv.key"), text: $row.key).textFieldStyle(.roundedBorder)
                            TextField(L("rest.kv.value"), text: $row.value).textFieldStyle(.roundedBorder)
                                .font(.system(.body, design: .monospaced))
                            Button { rows.removeAll { $0.id == row.id } } label: { Image(systemName: "minus.circle") }
                                .buttonStyle(.borderless)
                        }
                    }
                }
            }
            Button { rows.append(RestVariable()) } label: { Label(L("rest.kv.add"), systemImage: "plus") }
                .buttonStyle(.borderless)
        }
    }
}
