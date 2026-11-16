import AppKit
import UserNotifications
import WaypointCore

/// Powiadomienia systemowe: koniec długiego przesyłania plików i nieoczekiwane rozłączenie — tylko gdy
/// użytkownik i tak tego nie widzi (inna aplikacja albo inna karta). Kliknięcie przenosi do tej karty.
@MainActor
final class Notifier: NSObject, UNUserNotificationCenterDelegate {
    static let shared = Notifier()
    private var authorized: Bool?
    private static let sessionKey = "session"

    /// Przesyłanie krótsze niż to nie zasługuje na powiadomienie.
    static let longTransfer: TimeInterval = 8

    /// Centrum powiadomień działa tylko w prawdziwej paczce .app (z identyfikatorem) — przy `swift run`
    /// i w teście dymnym wyłączone.
    private var available: Bool {
        Bundle.main.bundleIdentifier != nil && Bundle.main.bundleURL.pathExtension == "app"
            && ProcessInfo.processInfo.environment["WAYPOINT_SMOKE_DIR"] == nil
    }

    func install() {
        guard available else { return }
        UNUserNotificationCenter.current().delegate = self
    }

    /// Czy użytkownik widzi tę kartę (aplikacja na wierzchu i karta aktywna).
    static func isVisible(_ sessionID: UUID?) -> Bool {
        NSApp.isActive && (sessionID == nil || AppModel.shared.activeSessionID == sessionID)
    }

    func post(_ title: String, _ body: String, session: UUID?) {
        guard available, AppModel.shared.settings.notificationsEnabled, !Self.isVisible(session) else { return }
        let center = UNUserNotificationCenter.current()
        let send = {
            let content = UNMutableNotificationContent()
            content.title = title
            content.body = body
            content.sound = .default
            if let session { content.userInfo = [Self.sessionKey: session.uuidString] }
            center.add(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil))
        }
        if authorized == true { send(); return }
        if authorized == false { return }
        center.requestAuthorization(options: [.alert, .sound]) { ok, _ in
            DispatchQueue.main.async {
                self.authorized = ok
                if ok { send() }
            }
        }
    }

    // Kliknięcie powiadomienia: aplikacja na wierzch i karta, której dotyczy.
    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse,
                                            withCompletionHandler completionHandler: @escaping () -> Void) {
        let id = (response.notification.request.content.userInfo[Self.sessionKey] as? String).flatMap(UUID.init(uuidString:))
        DispatchQueue.main.async {
            NSApp.activate(ignoringOtherApps: true)
            if let id, AppModel.shared.sessions.contains(where: { $0.id == id }) { AppModel.shared.activeSessionID = id }
            completionHandler()
        }
    }

    // Aplikacja na wierzchu, ale inna karta — baner i tak ma się pokazać.
    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification,
                                            withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .sound])
    }
}
