import AppKit
import Observation
import WaypointCore

/// Karta panelu plików (SFTP). Klient SFTP rozmawia przez systemowe `ssh -s sftp`, więc logowanie jest
/// takie samo jak w terminalu (config, agent, Pęk kluczy przez askpass). Wszystkie operacje sieciowe
/// idą przez jedną kolejkę szeregową — protokół jest i tak „jedno żądanie naraz" na poziomie operacji.
@MainActor
@Observable
final class FileSession: Identifiable {
    enum State: Equatable { case connecting, ready, failed(String) }

    struct Transfer: Equatable {
        var label: String
        var done: UInt64
        var total: UInt64
    }

    let id = UUID()
    private(set) var server: Server
    let auth: AuthBroker
    private(set) var state: State = .connecting
    /// FTPS: certyfikatu nie da się zweryfikować — karta proponuje świadomą zgodę dla tego serwera.
    private(set) var certificateProblem = false
    /// Zapis zgody na certyfikat w liście serwerów (robi to AppModel — sesja ma tylko kopię serwera).
    @ObservationIgnored var onTrustCertificate: ((Server) -> Void)?
    private(set) var path = ""
    private(set) var entries: [SftpEntry] = []
    var selection = Set<SftpEntry.ID>()
    private(set) var busy = false
    private(set) var transfer: Transfer?
    var message: String?
    var messageIsError = false
    /// Pytanie o nadpisanie przy wysyłaniu (nazwy, które już są w katalogu).
    var overwriteQuestion: (names: [String], urls: [URL])?

    @ObservationIgnored private var client: RemoteFS?
    /// Prace czekające na zakończenie łączenia (true = połączono).
    @ObservationIgnored private var whenConnected: [(Bool) -> Void] = []
    @ObservationIgnored private let queue = DispatchQueue(label: "waypoint.sftp")
    /// Flaga „Anuluj" czytana z kolejki SFTP — dlatego osobny obiekt z blokadą, nie pole aktora.
    @ObservationIgnored private let cancelFlag = CancelFlag()

    var title: String { server.displayName }
    var isRunning: Bool { state == .ready }

    init(server: Server) {
        self.server = server
        self.auth = AuthBroker(server: server)
        connect()
    }

    // MARK: Połączenie

    func connect() {
        state = .connecting
        certificateProblem = false
        if server.proto == .ftp { connectFtp(); return }
        let srv = server
        let extra = auth.start()
        let launch = SshCommand.buildSftp(srv, homeDirectory: NSHomeDirectory(),
                                          fileExists: { FileManager.default.fileExists(atPath: $0) })
        let env = SshCommand.environment(base: ProcessInfo.processInfo.environment, extra: extra)
            .reduce(into: [String: String]()) { d, kv in
                if let i = kv.firstIndex(of: "=") { d[String(kv[..<i])] = String(kv[kv.index(after: i)...]) }
            }
        queue.async { [weak self] in
            do {
                let transport = try SshProcessTransport(executable: SshCommand.executable, arguments: launch.arguments, environment: env)
                let c = try SftpClient(transport: transport)
                let home = try c.realPath(".")
                let list = try c.list(home)
                DispatchQueue.main.async {
                    guard let self else { c.close(); return }
                    self.client = c
                    self.path = home
                    self.entries = Self.sorted(list)
                    self.state = .ready
                    self.auth.stop()   // zalogowano — kanał askpass nie jest już potrzebny
                    self.flushWaiting(true)
                }
            } catch {
                DispatchQueue.main.async {
                    self?.auth.stop()
                    self?.state = .failed(Self.describe(error))
                    self?.flushWaiting(false)
                }
            }
        }
    }

    // MARK: FTP

    /// FTP nie ma askpass — hasło bierzemy z Pęku kluczy, a gdy go nie ma (albo zostało odrzucone),
    /// pytamy w karcie tym samym oknem co przy SSH.
    private func connectFtp(retry: Bool = false) {
        if server.ftpAnonymous { startFtp(user: "anonymous", password: "waypoint@", save: false); return }
        if !retry, let saved = Keychain.password(for: server.id) {
            startFtp(user: server.username, password: saved, save: false)
            return
        }
        let text = "\(server.username)@\(server.host)"
        auth.prompt = PendingPrompt(kind: .password, text: text, isRetry: retry) { [weak self] value, save in
            guard let self else { return }
            self.auth.prompt = nil
            guard let value else { self.state = .failed(L("files.err.cancelledLogin")); self.flushWaiting(false); return }
            self.startFtp(user: self.server.username, password: value, save: save)
        }
    }

    private func startFtp(user: String, password: String, save: Bool) {
        let srv = server
        queue.async { [weak self] in
            do {
                let c = try FtpClient(host: srv.host, port: srv.port, user: user, password: password,
                                      encryption: FtpEncryption(rawValue: srv.ftpEncryption) ?? .explicitTLS,
                                      acceptInvalidCertificate: srv.ftpAcceptInvalidCertificate)
                let home = try c.realPath(".")
                let list = try c.list(home)
                DispatchQueue.main.async {
                    guard let self else { c.close(); return }
                    if save {
                        let ok = Keychain.save(password, for: srv.id, label: "Waypoint — \(srv.displayName)")
                        self.auth.notice = ok ? L("prompt.saved") : L("prompt.savefail")
                    }
                    self.client = c
                    self.path = home
                    self.entries = Self.sorted(list)
                    self.state = .ready
                    self.flushWaiting(true)
                }
            } catch SftpError.authenticationFailed {
                DispatchQueue.main.async { self?.connectFtp(retry: true) }
            } catch {
                DispatchQueue.main.async {
                    guard let self else { return }
                    if case SftpError.certificateUntrusted = error { self.certificateProblem = true }
                    self.state = .failed(Self.describe(error))
                    self.flushWaiting(false)
                }
            }
        }
    }

    /// Świadoma zgoda na certyfikat, którego nie da się zweryfikować — zapamiętana dla tego serwera.
    func trustCertificate() {
        server.ftpAcceptInvalidCertificate = true
        onTrustCertificate?(server)
        connect()
    }

    private func flushWaiting(_ connected: Bool) {
        let waiting = whenConnected
        whenConnected = []
        waiting.forEach { $0(connected) }
    }

    func close() {
        cancelFlag.set(true)
        auth.stop()
        let c = client
        client = nil
        queue.async { c?.close() }
    }

    /// Zerwane połączenie (np. uśpienie Maca) — karta przechodzi w stan błędu z „Połącz ponownie".
    private func handleFailure(_ error: Error) {
        if case SftpError.disconnected = error {
            client?.close()
            client = nil
            state = .failed(Self.describe(error))
        } else if case SftpError.cancelled = error {
            show(L("files.cancelled"))
        } else {
            show(Self.describe(error), error: true)
        }
    }

    static func describe(_ error: Error) -> String {
        if let e = error as? SftpError {
            switch e {
            case .status(.permissionDenied, _): return L("files.err.denied")
            case .status(.noSuchFile, _): return L("files.err.missing")
            case .disconnected(let d): return d.isEmpty ? L("files.err.disconnected") : d
            case .certificateUntrusted(let d): return String(format: L("files.err.cert"), d)
            case .authenticationFailed: return L("files.err.login")
            default: return e.description
            }
        }
        return error.localizedDescription
    }

    private func show(_ text: String, error: Bool = false) {
        message = text
        messageIsError = error
    }

    /// Wykonuje pracę na kolejce SFTP; wynik (albo błąd) wraca na wątek główny.
    private func run<T>(_ label: String?, work: @escaping (RemoteFS) throws -> T, done: @escaping (T) -> Void) {
        guard let c = client, !busy else { return }
        busy = true
        if let label { show(label) }
        queue.async { [weak self] in
            let result = Result { try work(c) }
            DispatchQueue.main.async {
                guard let self else { return }
                self.busy = false
                self.transfer = nil
                switch result {
                case .success(let v): done(v)
                case .failure(let e): self.handleFailure(e)
                }
            }
        }
    }

    static func sorted(_ list: [SftpEntry]) -> [SftpEntry] {
        list.sorted {
            if $0.isDirectory != $1.isDirectory { return $0.isDirectory }
            return $0.name.localizedStandardCompare($1.name) == .orderedAscending
        }
    }

    // MARK: Nawigacja

    func navigate(_ newPath: String) {
        run(nil, work: { c in (try c.realPath(newPath), try c.list(newPath)) }) { [weak self] r in
            self?.path = r.0
            self?.entries = Self.sorted(r.1)
            self?.selection = []
            self?.message = nil
        }
    }

    func refresh() { navigate(path) }
    func up() { navigate(RemotePath.parent(path)) }

    func open(_ e: SftpEntry) {
        if e.isDirectory { navigate(e.path); return }
        // Plik: pobranie do katalogu tymczasowego i otwarcie domyślną aplikacją Maca.
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("Waypoint-\(UUID().uuidString.prefix(8))")
        startTransfer(String(format: L("files.downloading"), e.name), total: e.size, work: { c, progress in
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            let local = dir.appendingPathComponent(e.name)
            try c.download(e.path, to: local, progress: progress)
            return local
        }) { local in NSWorkspace.shared.open(local) }
    }

    // MARK: Operacje

    func makeDirectory(_ name: String) {
        guard RemotePath.isSafeName(name) else { show(L("files.err.name"), error: true); return }
        let target = RemotePath.join(path, name)
        run(nil, work: { c in try c.mkdir(target) }) { [weak self] in self?.refresh() }
    }

    func rename(_ e: SftpEntry, to name: String) {
        guard RemotePath.isSafeName(name) else { show(L("files.err.name"), error: true); return }
        guard name != e.name else { return }
        if entries.contains(where: { $0.name == name }) { show(String(format: L("files.err.exists"), name), error: true); return }
        let target = RemotePath.join(RemotePath.parent(e.path), name)
        run(nil, work: { c in try c.rename(e.path, to: target) }) { [weak self] in self?.refresh() }
    }

    func delete(_ list: [SftpEntry]) {
        run(L("files.deleting"), work: { c in for e in list { try c.removeTree(e.path) } }) { [weak self] in
            self?.show(String(format: L("files.deleted"), list.count))
            self?.refresh()
        }
    }

    /// Wysyłanie plików/katalogów z Findera (przycisk albo przeciągnięcie). Istniejące nazwy → pytanie.
    func upload(_ urls: [URL], confirmedOverwrite: Bool = false) {
        guard !urls.isEmpty else { return }
        let existing = urls.map(\.lastPathComponent).filter { n in entries.contains { $0.name == n } }
        if !existing.isEmpty && !confirmedOverwrite {
            overwriteQuestion = (existing, urls)
            return
        }
        let dir = path
        let total = urls.reduce(UInt64(0)) { $0 + SftpClient.localTreeSize($1) }
        let label = urls.count == 1 ? urls[0].lastPathComponent : String(format: L("files.items"), urls.count)
        startTransfer(String(format: L("files.uploading"), label), total: total, work: { c, progress in
            var base: UInt64 = 0
            for u in urls {
                try c.uploadTree(u, into: dir, overwrite: confirmedOverwrite) { progress(base + $0) }
                base += SftpClient.localTreeSize(u)
            }
        }) { [weak self] in
            self?.show(String(format: L("files.uploaded"), label))
            self?.refresh()
        }
    }

    func download(_ list: [SftpEntry], to folder: URL) {
        guard !list.isEmpty else { return }
        let label = list.count == 1 ? list[0].name : String(format: L("files.items"), list.count)
        startTransfer(String(format: L("files.downloading"), label), total: 0, work: { [weak self] c, progress in
            // Rozmiar całości liczony na serwerze (katalogi), żeby pasek postępu miał sens.
            let total = try list.reduce(UInt64(0)) { $0 + (try c.treeSize($1.path).bytes) }
            DispatchQueue.main.async { self?.transfer?.total = total }
            var base: UInt64 = 0
            for e in list {
                try c.downloadTree(e.path, into: folder) { progress(base + $0) }
                base += try c.treeSize(e.path).bytes
            }
        }) { [weak self] in
            self?.show(String(format: L("files.downloaded"), label, folder.path))
            NSWorkspace.shared.activateFileViewerSelecting(list.map { folder.appendingPathComponent($0.name) })
        }
    }

    func cancelTransfer() { cancelFlag.set(true) }

    // MARK: Edytor

    /// Dane do otwarcia pliku w edytorze: treść, metadane (po rozwiązaniu dowiązań) i uid zalogowanego
    /// (z właściciela katalogu domowego — SFTP nie mówi „kim jestem").
    struct EditPayload {
        var data: Data
        var info: RemoteFileInfo
        var myUid: UInt32?
    }

    static let editMaxBytes: UInt64 = 10 * 1024 * 1024

    func readForEdit(_ e: SftpEntry, completion: @escaping (EditPayload) -> Void) {
        if e.size > Self.editMaxBytes {
            show(String(format: L("edit.toobig"), ByteCountFormatter.string(fromByteCount: Int64(Self.editMaxBytes), countStyle: .file)), error: true)
            return
        }
        run(String(format: L("files.downloading"), e.name), work: { c -> EditPayload? in
            let info = try SafeWrite.stat(c, e.path)
            guard info.length <= Self.editMaxBytes else { return nil }
            let data = try c.readFile(info.path, limit: Int(Self.editMaxBytes))
            let uid = (try? c.stat(try c.realPath(".")))?.uid
            return EditPayload(data: data, info: info, myUid: uid)
        }) { [weak self] payload in
            guard let self else { return }
            guard let payload else {
                self.show(String(format: L("edit.toobig"), ByteCountFormatter.string(fromByteCount: Int64(Self.editMaxBytes), countStyle: .file)), error: true)
                return
            }
            if TextFileFormat.looksBinary(payload.data) { self.show(L("edit.binary"), error: true); return }
            self.message = nil
            completion(payload)
        }
    }

    /// Operacja dla edytora na tym połączeniu (stat przed zapisem, bezpieczny zapis, ponowne wczytanie).
    /// Błędy wracają do wołającego (edytor pokazuje je po swojemu), a nie na pasek panelu.
    func perform<T>(_ work: @escaping (RemoteFS) throws -> T, completion: @escaping (Result<T, Error>) -> Void) {
        guard let c = client else {
            // Połączenie jeszcze wstaje (edytor otwiera własne) albo padło — praca czeka na wynik łączenia.
            whenConnected.append { [weak self] connected in
                guard let self, connected else {
                    completion(.failure(SftpError.disconnected(L("files.err.disconnected"))))
                    return
                }
                self.perform(work, completion: completion)
            }
            if case .failed = state { connect() }
            return
        }
        queue.async { [weak self] in
            let result = Result { try work(c) }
            DispatchQueue.main.async {
                if case .failure(let e) = result, case SftpError.disconnected = e {
                    self?.client?.close(); self?.client = nil; self?.state = .failed(Self.describe(e))
                }
                completion(result)
            }
        }
    }

    private func startTransfer<T>(_ label: String, total: UInt64,
                                  work: @escaping (RemoteFS, @escaping (UInt64) -> Bool) throws -> T,
                                  done: @escaping (T) -> Void) {
        cancelFlag.set(false)
        let flag = cancelFlag
        transfer = Transfer(label: label, done: 0, total: total)
        var lastUpdate = Date.distantPast
        run(label, work: { [weak self] c in
            try work(c) { bytes in
                // Postęp do UI najwyżej 10× na sekundę — bez zalewania wątku głównego.
                let now = Date()
                if now.timeIntervalSince(lastUpdate) > 0.1 {
                    lastUpdate = now
                    DispatchQueue.main.async { self?.transfer?.done = bytes }
                }
                return !flag.value
            }
        }, done: done)
    }
}

final class CancelFlag: @unchecked Sendable {
    private let lock = NSLock()
    private var flag = false
    var value: Bool { lock.lock(); defer { lock.unlock() }; return flag }
    func set(_ v: Bool) { lock.lock(); flag = v; lock.unlock() }
}
