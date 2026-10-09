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
            CommandGroup(after: .appInfo) {
                Button(L("upd.menu")) { model.checkForUpdates(manual: true) }
            }
            CommandGroup(replacing: .newItem) {
                Button(L("menu.newserver")) { model.beginNew() }
                    .keyboardShortcut("n")
            }
            CommandGroup(after: .newItem) {
                Button(L("menu.import")) { model.importProfile() }
                    .keyboardShortcut("i", modifiers: [.command, .shift])
                Button(L("rdp.import")) { model.importRdp() }
                Menu(L("migr.menu")) {
                    ForEach(ExternalImport.Source.allCases, id: \.self) { src in
                        Button(L("migr.item." + src.rawValue)) { model.importExternal(src) }
                    }
                }
                Button(L("rest.import.menu")) { model.importPostman() }
                Button(L("export.menu")) { model.exportProfile() }
                    .keyboardShortcut("e", modifiers: [.command, .shift])
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
            CommandMenu(L("menu.tools")) {
                Button(L("cred.menu")) { model.profileManagerOpen = true }
                Button(L("gen.menu")) { model.generatorOpen = true }
                    .keyboardShortcut("g", modifiers: [.command, .option])
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
                Button(L("tab.detach")) { if let t = model.activeSession { model.detach(t) } }
                    .disabled(model.activeSession == nil)
            }
        }
    }
}
