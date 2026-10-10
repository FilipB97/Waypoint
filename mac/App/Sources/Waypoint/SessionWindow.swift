import AppKit
import SwiftUI
import WaypointCore

/// Karta przeniesiona do osobnego okna (jak „Otwórz w nowym oknie" w Windows): ta sama sesja — proces
/// ssh, bufor terminala, połączenie plików — tylko w innym oknie. Można ją przywrócić do okna głównego.
@MainActor
final class SessionWindowController: NSWindowController, NSWindowDelegate {
    static var open: [SessionWindowController] = []
    let tab: SessionTab
    private var reattaching = false

    static func show(_ tab: SessionTab) {
        let wc = SessionWindowController(tab: tab)
        open.append(wc)
        wc.showWindow(nil)
        wc.window?.makeKeyAndOrderFront(nil)
    }

    init(tab: SessionTab) {
        self.tab = tab
        let host = NSHostingController(rootView: DetachedSessionView(tab: tab).environment(AppModel.shared))
        host.sizingOptions = []
        let w = NSWindow(contentViewController: host)
        w.setContentSize(NSSize(width: 900, height: 560))
        w.minSize = NSSize(width: 480, height: 300)
        w.title = tab.title
        w.subtitle = tab.server.host
        w.tabbingMode = .disallowed
        w.center()
        super.init(window: w)
        w.delegate = self
    }

    required init?(coder: NSCoder) { fatalError() }

    /// Z powrotem do paska kart okna głównego.
    func reattach() {
        reattaching = true
        AppModel.shared.add(tab)
        NSApp.windows.first { $0.isVisible && AppModel.isMainWindow($0) && $0 !== window }?.makeKeyAndOrderFront(nil)
        close()
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        if reattaching { return true }
        let model = AppModel.shared
        if model.settings.confirmCloseConnected && tab.isRunning {
            let a = NSAlert()
            a.messageText = String(format: L("close.title"), tab.title)
            a.informativeText = L("close.msg")
            a.addButton(withTitle: L("close.confirm"))
            a.addButton(withTitle: L("btn.cancel"))
            guard a.runModal() == .alertFirstButtonReturn else { return false }
        }
        return true
    }

    func windowWillClose(_ notification: Notification) {
        if !reattaching { tab.close() }
        Self.open.removeAll { $0 === self }
    }
}

private struct DetachedSessionView: View {
    let tab: SessionTab

    var body: some View {
        SessionContent(tab: tab)
            .toolbar {
                ToolbarItem {
                    Button {
                        SessionWindowController.open.first { $0.tab.id == tab.id }?.reattach()
                    } label: { Label(L("tab.reattach"), systemImage: "rectangle.stack.badge.plus") }
                        .help(L("tab.reattach"))
                }
            }
    }
}
