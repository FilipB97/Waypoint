import Foundation
import WaypointCore

/// Karta na pasku: terminal (SSH/Telnet/COM), panel plików albo konsola REST.
enum SessionTab: Identifiable {
    case terminal(TerminalSession)
    case files(FileSession)
    case rest(RestSession)

    var id: UUID {
        switch self {
        case .terminal(let t): return t.id
        case .files(let f): return f.id
        case .rest(let r): return r.id
        }
    }

    @MainActor var title: String {
        switch self {
        case .terminal(let t): return t.title
        case .files(let f): return f.title
        case .rest(let r): return r.server.displayName
        }
    }

    @MainActor var server: Server {
        switch self {
        case .terminal(let t): return t.server
        case .files(let f): return f.server
        case .rest(let r): return r.server
        }
    }

    /// Czy połączenie żyje (kropka na karcie, pytanie przy zamykaniu).
    @MainActor var isRunning: Bool {
        switch self {
        case .terminal(let t): return t.isRunning
        case .files(let f): return f.isRunning
        case .rest(let r): return r.sending   // konsola REST nie ma połączenia — „żyje" tylko w trakcie wysyłki
        }
    }

    var systemImage: String {
        switch self {
        case .terminal: return "terminal"
        case .files: return "folder"
        case .rest: return "curlybraces"
        }
    }

    @MainActor func close() {
        switch self {
        case .terminal(let t): t.close()
        case .files(let f): f.close()
        case .rest(let r): r.save()
        }
    }

    @MainActor func reconnect() {
        switch self {
        case .terminal(let t): t.reconnect()
        case .files(let f): if !f.isRunning { f.connect() }
        case .rest: break
        }
    }

    var terminal: TerminalSession? { if case .terminal(let t) = self { return t }; return nil }
    var files: FileSession? { if case .files(let f) = self { return f }; return nil }
    var rest: RestSession? { if case .rest(let r) = self { return r }; return nil }
}
