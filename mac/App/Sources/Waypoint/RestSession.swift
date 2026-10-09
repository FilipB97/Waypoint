import AppKit
import Observation
import WaypointCore

/// Konsola REST jednego wpisu (kolekcji): drzewo żądań, edycja, wysyłka, odpowiedź, historia.
/// Zmiany zapisują się do `rest.json` (⌘S, przełączenie żądania, zamknięcie karty); sekrety
/// uwierzytelniania — do Pęku kluczy.
@MainActor
@Observable
final class RestSession: Identifiable {
    let id = UUID()
    let server: Server
    private let store: RestStore
    private let envStore: EnvironmentStore

    var collection: RestCollection
    var environments: [RestEnvironment]
    var activeEnvironmentId: String { didSet { envStore.activeId = activeEnvironmentId } }
    var selectedId: String?
    /// Sekrety (token / hasło Basic) w pamięci — konto Pęku kluczy → wartość; zapis przy „Zapisz".
    /// Bez obserwacji: odczyt (leniwie z Pęku kluczy) dzieje się w trakcie rysowania widoku.
    @ObservationIgnored var secrets: [String: String] = [:]
    @ObservationIgnored private var dirtySecrets = Set<String>()
    private(set) var sending = false
    var response: RestResponse?
    var preOutcome = ScriptOutcome()
    var testOutcome = ScriptOutcome()
    var dirty = false

    init(server: Server, dataDirectory: URL) {
        self.server = server
        store = RestStore(directory: dataDirectory)
        envStore = EnvironmentStore(directory: dataDirectory)
        collection = store.collection(for: server.id)
        environments = envStore.load()
        activeEnvironmentId = envStore.activeId
        if collection.baseUrl.isEmpty && collection.requests.isEmpty { collection.baseUrl = server.host }
        selectedId = collection.requests.first?.id
    }

    var collectionAccount: String { "restcoll:" + server.id }
    var activeEnvironment: RestEnvironment? { environments.first { $0.id == activeEnvironmentId } }
    var variables: [String: String] {
        var v = activeEnvironment?.dictionary ?? [:]
        if v["baseUrl"] == nil && !collection.baseUrl.isEmpty { v["baseUrl"] = collection.baseUrl }
        return v
    }

    var selectedIndex: Int? { collection.requests.firstIndex { $0.id == selectedId } }

    func secret(_ account: String) -> String {
        if let s = secrets[account] { return s }
        let s = Keychain.password(for: account) ?? ""
        secrets[account] = s
        return s
    }

    func setSecret(_ value: String, for account: String) {
        secrets[account] = value
        dirtySecrets.insert(account)
        dirty = true
    }

    // MARK: Drzewo

    func addRequest(in folderId: String = "") {
        var r = RestRequest(name: L("rest.newrequest"), url: collection.baseUrl.isEmpty ? "" : "{{baseUrl}}/")
        r.folderId = folderId
        collection.requests.append(r)
        selectedId = r.id
        dirty = true
    }

    func addFolder(in parentId: String = "") {
        collection.folders.append(RestFolder(name: L("rest.newfolder"), parentId: parentId))
        dirty = true
    }

    func duplicate(_ r: RestRequest) {
        var c = r
        c.id = Server.newId()
        c.name = String(format: L("list.copyname"), r.name)
        let s = secret(r.keychainAccount)
        if !s.isEmpty { setSecret(s, for: c.keychainAccount) }
        if let i = collection.requests.firstIndex(where: { $0.id == r.id }) { collection.requests.insert(c, at: i + 1) }
        selectedId = c.id
        dirty = true
    }

    func delete(_ r: RestRequest) {
        collection.requests.removeAll { $0.id == r.id }
        Keychain.delete(for: r.keychainAccount)
        if selectedId == r.id { selectedId = collection.requests.first?.id }
        dirty = true
    }

    /// Folder z całą zawartością (podfoldery i żądania).
    func delete(_ f: RestFolder) {
        var ids: Set<String> = [f.id]
        var grew = true
        while grew {
            let more = collection.folders.filter { ids.contains($0.parentId) && !ids.contains($0.id) }.map(\.id)
            grew = !more.isEmpty
            ids.formUnion(more)
        }
        for r in collection.requests where ids.contains(r.folderId) { Keychain.delete(for: r.keychainAccount) }
        for id in ids { Keychain.delete(for: "restfolder:" + id) }
        collection.requests.removeAll { ids.contains($0.folderId) }
        collection.folders.removeAll { ids.contains($0.id) }
        if let s = selectedId, !collection.requests.contains(where: { $0.id == s }) { selectedId = collection.requests.first?.id }
        dirty = true
    }

    func save() {
        guard dirty else { return }
        for a in dirtySecrets {
            let v = secrets[a] ?? ""
            if v.isEmpty { Keychain.delete(for: a) } else { Keychain.save(v, for: a, label: "Waypoint — REST \(server.displayName)") }
        }
        dirtySecrets = []
        do { try store.put(collection, for: server.id); dirty = false }
        catch { AppModel.shared.alert = AppAlert(title: L("rest.title"), message: error.localizedDescription) }
    }

    func saveEnvironments(_ list: [RestEnvironment]) {
        environments = list
        if !list.contains(where: { $0.id == activeEnvironmentId }) { activeEnvironmentId = "" }
        do { try envStore.save(list) }
        catch { AppModel.shared.alert = AppAlert(title: L("rest.title"), message: error.localizedDescription) }
    }

    // MARK: Wysyłka

    /// Skrypt pre-request → zmienne → uwierzytelnianie (z dziedziczeniem) → HTTP → skrypt testów → historia.
    func send() {
        guard let i = selectedIndex, !sending else { return }
        var req = collection.requests[i]
        var vars = variables
        let pre = RestScript.run(req.preScript, request: req, response: nil, vars: vars)
        preOutcome = pre.outcome
        req = pre.request
        if pre.vars != vars { vars = pre.vars; storeVariables(vars) }

        let auth: (type: Int, username: String, account: String) = req.authType == RestAuthType.inherit.rawValue
            ? collection.resolveAuth(from: req.folderId, collectionAccount: collectionAccount)
            : (req.authType, req.authUsername, req.keychainAccount)
        let prepared = RestBuild.prepare(req, vars: vars, authType: auth.type, username: auth.username,
                                         secret: secret(auth.account), userAgent: "Waypoint/" + AppModel.currentVersion)
        sending = true
        response = nil
        testOutcome = ScriptOutcome()
        Task {
            let r = await RestHTTP.send(prepared)
            self.response = r
            self.sending = false
            if r.ok {
                let t = RestScript.run(req.testScript, request: req, response: r, vars: vars)
                self.testOutcome = t.outcome
                if t.vars != vars { self.storeVariables(t.vars) }
            }
            let when = ISO8601DateFormatter().string(from: Date())
            self.collection.record(RestHistoryEntry(method: prepared.method, url: prepared.url, status: r.status,
                                                    elapsedMs: r.elapsedMs, whenIso: when))
            self.dirty = true
            self.save()
        }
    }

    /// Zmienne ustawione przez skrypt trafiają do aktywnego środowiska (jak pm.environment.set w Postmanie).
    private func storeVariables(_ vars: [String: String]) {
        guard let e = environments.firstIndex(where: { $0.id == activeEnvironmentId }) else { return }
        var env = environments[e]
        for (k, v) in vars where k != "baseUrl" || env.variables.contains(where: { $0.key == "baseUrl" }) {
            if let j = env.variables.firstIndex(where: { $0.key == k }) { env.variables[j].value = v }
            else { env.variables.append(RestVariable(key: k, value: v)) }
        }
        env.variables.removeAll { vars[$0.key] == nil }
        var list = environments
        list[e] = env
        saveEnvironments(list)
    }

    var missingVariables: (missing: [String], empty: [String]) {
        guard let i = selectedIndex else { return ([], []) }
        let r = collection.requests[i]
        let auth = r.authType == RestAuthType.inherit.rawValue
            ? collection.resolveAuth(from: r.folderId, collectionAccount: collectionAccount)
            : (r.authType, r.authUsername, r.keychainAccount)
        return RestBuild.audit(r, secret: auth.type == 0 ? "" : secret(auth.account), username: auth.username, vars: variables)
    }
}
