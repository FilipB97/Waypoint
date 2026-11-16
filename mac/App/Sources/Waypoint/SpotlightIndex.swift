import AppKit
import CoreSpotlight
import UniformTypeIdentifiers
import WaypointCore

/// Serwery w Spotlight: wpisz nazwę, host albo grupę — wybranie wyniku łączy (onContinueUserActivity).
@MainActor
enum SpotlightIndex {
    static let domain = "io.github.filipb97.waypoint.servers"
    private static let prefix = "server:"
    private static var pending: DispatchWorkItem?

    private static var available: Bool {
        Bundle.main.bundleIdentifier != nil && Bundle.main.bundleURL.pathExtension == "app"
            && ProcessInfo.processInfo.environment["WAYPOINT_SMOKE_DIR"] == nil
    }

    /// Po zmianie listy (z opóźnieniem — seria edycji to jedno przeindeksowanie).
    static func schedule(_ servers: [Server], enabled: Bool) {
        guard available else { return }
        pending?.cancel()
        let work = DispatchWorkItem { update(servers, enabled: enabled) }
        pending = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 2, execute: work)
    }

    static func update(_ servers: [Server], enabled: Bool) {
        guard available else { return }
        let index = CSSearchableIndex.default()
        let items: [CSSearchableItem] = enabled ? servers.filter { $0.proto?.supportedOnMac == true }.map { s in
            let a = CSSearchableItemAttributeSet(contentType: .item)
            a.title = s.displayName
            let user = s.username.isEmpty ? "" : s.username + "@"
            a.contentDescription = "Waypoint · \(s.proto?.badge ?? s.protocolName) · \(user)\(s.host)"
            a.keywords = [s.host, s.group, s.proto?.badge ?? ""] + s.tags
            return CSSearchableItem(uniqueIdentifier: prefix + s.id, domainIdentifier: domain, attributeSet: a)
        } : []
        index.deleteSearchableItems(withDomainIdentifiers: [domain]) { _ in
            if !items.isEmpty { index.indexSearchableItems(items) }
        }
    }

    /// Identyfikator serwera z wybranego wyniku Spotlight.
    static func serverID(from activity: NSUserActivity) -> String? {
        guard let id = activity.userInfo?[CSSearchableItemActivityIdentifier] as? String, id.hasPrefix(prefix) else { return nil }
        return String(id.dropFirst(prefix.count))
    }
}
