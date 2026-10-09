import Foundation
import WaypointCore

/// Karta na pasku: terminal SSH albo panel plików SFTP.
enum SessionTab: Identifiable {
    case terminal(TerminalSession)
    case files(FileSession)

    var id: UUID {
        switch self {
        case .terminal(let t): return t.id
        case .files(let f): return f.id
        }
    }

    @MainActor var title: String {
        switch self {
        case .terminal(let t): return t.title
        case .files(let f): return f.title
        }
    }

    @MainActor var server: Server {
        switch self {
        case .terminal(let t): return t.server
        case .files(let f): return f.server
        }
    }

    /// Czy połączenie żyje (kropka na karcie, pytanie przy zamykaniu).
    @MainActor var isRunning: Bool {
        switch self {
        case .terminal(let t): return t.isRunning
        case .files(let f): return f.isRunning
        }
    }

    var systemImage: String {
        switch self {
        case .terminal: return "terminal"
        case .files: return "folder"
        }
    }

    @MainActor func close() {
        switch self {
        case .terminal(let t): t.close()
        case .files(let f): f.close()
        }
    }

    @MainActor func reconnect() {
        switch self {
        case .terminal(let t): t.reconnect()
        case .files(let f): if !f.isRunning { f.connect() }
        }
    }

    var terminal: TerminalSession? { if case .terminal(let t) = self { return t }; return nil }
    var files: FileSession? { if case .files(let f) = self { return f }; return nil }
}
