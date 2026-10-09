import SwiftUI
import WaypointCore

/// Ustawienia (⌘,). Zapis od razu przy każdej zmianie, jak reszta aplikacji.
struct SettingsView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        @Bindable var model = model
        Form {
            Section(L("set.list")) {
                Toggle(L("set.reach"), isOn: $model.settings.reachabilityEnabled)
                Stepper(value: $model.settings.reachabilityIntervalSec, in: 5...3600, step: 5) {
                    LabeledContent(L("set.reach.interval"), value: String(format: L("set.seconds"), model.settings.reachabilityIntervalSec))
                }
                .disabled(!model.settings.reachabilityEnabled)
                Stepper(value: $model.settings.probeTimeoutSeconds, in: 1...60) {
                    LabeledContent(L("set.reach.timeout"), value: String(format: L("set.seconds"), model.settings.probeTimeoutSeconds))
                }
                .disabled(!model.settings.reachabilityEnabled)
                Toggle(L("set.latency"), isOn: $model.settings.showLatency)
                    .disabled(!model.settings.reachabilityEnabled)
            }
            Section(L("set.app")) {
                Toggle(L("set.updates"), isOn: $model.settings.checkUpdates)
                LabeledContent(L("set.version"), value: AppModel.currentVersion)
                Button(L("upd.menu")) { model.checkForUpdates(manual: true) }
            }
            Section(L("set.connections")) {
                Toggle(L("set.confirmclose"), isOn: $model.settings.confirmCloseConnected)
                Toggle(L("set.log"), isOn: $model.settings.connectionLogEnabled)
                Button(L("log.reveal")) { model.revealConnectionLog() }
            }
        }
        .formStyle(.grouped)
        .frame(width: 460)
        .fixedSize(horizontal: false, vertical: true)
        .onChange(of: model.settings) { old, new in
            model.saveSettings()
            if old.reachabilityEnabled != new.reachabilityEnabled
                || old.reachabilityIntervalSec != new.reachabilityIntervalSec
                || old.probeTimeoutSeconds != new.probeTimeoutSeconds {
                model.restartReachability()
            }
        }
    }
}
