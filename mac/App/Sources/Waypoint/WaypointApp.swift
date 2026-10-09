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
        .commands {
            CommandGroup(replacing: .newItem) {
                Button(L("menu.newserver")) { model.beginNew() }
                    .keyboardShortcut("n")
            }
            CommandGroup(after: .newItem) {
                Button(L("menu.import")) { model.importProfile() }
                    .keyboardShortcut("i", modifiers: [.command, .shift])
            }
        }
    }
}
