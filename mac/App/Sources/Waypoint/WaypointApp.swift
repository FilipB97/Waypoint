import SwiftUI
import WaypointCore

struct WaypointApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @State private var model = AppModel.shared

    var body: some Scene {
        WindowGroup("Waypoint") {
            ContentView()
                .environment(model)
                .frame(minWidth: 760, minHeight: 460)
                .task { model.load() }
        }
        Settings {
            SettingsView().environment(model)
        }
        .commands {
            CommandGroup(replacing: .newItem) {
                Button(L("menu.newserver")) { model.beginNew() }
                    .keyboardShortcut("n")
            }
            CommandGroup(after: .newItem) {
                Button(L("menu.import")) { model.importProfile() }
                    .keyboardShortcut("i", modifiers: [.command, .shift])
                Button(L("rdp.import")) { model.importRdp() }
                Divider()
                Button(L("pal.quickmenu")) { model.openPalette() }
                    .keyboardShortcut("n", modifiers: [.command, .shift])
            }
            CommandMenu(L("menu.go")) {
                Button(L("dash.title")) { model.showDashboard() }
                    .keyboardShortcut("d", modifiers: [.command, .shift])
                Button(L("pal.open")) { model.openPalette() }
                    .keyboardShortcut("k")
            }
            CommandMenu(L("menu.terminal")) {
                Button(L("snip.menu.pick")) { model.snippetPickerOpen = true }
                    .keyboardShortcut("k", modifiers: [.command, .shift])
                Button(L("snip.menu.manage")) { model.snippetManagerOpen = true }
                Divider()
                Button(L("term.find")) { model.findInTerminal() }
                    .keyboardShortcut("f")
                    .disabled(model.activeTerminal == nil)
                Divider()
                Button(L("term.zoomin")) { model.zoomTerminal(1) }.keyboardShortcut("+")
                Button(L("term.zoomout")) { model.zoomTerminal(-1) }.keyboardShortcut("-")
                Button(L("term.zoomreset")) { model.zoomTerminal(0) }.keyboardShortcut("0")
                Divider()
                Button(L("tab.duplicate")) { if let t = model.activeSession { model.duplicate(t) } }
                    .disabled(model.activeSession == nil)
            }
        }
    }
}
