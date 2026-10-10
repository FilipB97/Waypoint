import AppKit
import SwiftUI
import WaypointCore

/// Ustawienia (⌘,). Zapis od razu przy każdej zmianie, jak reszta aplikacji.
struct SettingsView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        @Bindable var model = model
        Form {
            Section(L("set.look")) {
                Picker(L("set.theme"), selection: $model.settings.theme) {
                    Text(L("set.theme.system")).tag(AppTheme.system.rawValue)
                    Text(L("set.theme.light")).tag(AppTheme.light.rawValue)
                    Text(L("set.theme.dark")).tag(AppTheme.dark.rawValue)
                }
                Picker(L("set.term.dark"), selection: $model.settings.themeVariantDark) {
                    ForEach(ThemePreset.list(light: false)) { p in Text(p.name).tag(p.id) }
                }
                Picker(L("set.term.light"), selection: $model.settings.themeVariantLight) {
                    ForEach(ThemePreset.list(light: true)) { p in Text(p.name).tag(p.id) }
                }
                HStack {
                    Toggle(L("set.accent"), isOn: Binding(
                        get: { !model.settings.accentColor.isEmpty },
                        set: { model.settings.accentColor = $0 ? "#6C6DFF" : "" }))
                    Spacer()
                    if !model.settings.accentColor.isEmpty {
                        ColorPicker("", selection: Binding(
                            get: { Color(hex: model.settings.accentColor) ?? .accentColor },
                            set: { model.settings.accentColor = $0.hexString ?? model.settings.accentColor }),
                                    supportsOpacity: false)
                            .labelsHidden()
                    }
                }
                Stepper(value: $model.settings.terminalFontSize, in: 8...24) {
                    LabeledContent(L("set.fontsize"), value: "\(model.settings.terminalFontSize) pt")
                }
            }
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
            if old.theme != new.theme { model.applyAppearance() }
            if old.terminalFontSize != new.terminalFontSize { model.applyTerminalFont() }
            if old.reachabilityEnabled != new.reachabilityEnabled
                || old.reachabilityIntervalSec != new.reachabilityIntervalSec
                || old.probeTimeoutSeconds != new.probeTimeoutSeconds {
                model.restartReachability()
            }
        }
    }
}

extension Color {
    /// „#RRGGBB" w sRGB — zapis jak AccentColor w Windows.
    var hexString: String? {
        guard let c = NSColor(self).usingColorSpace(.sRGB) else { return nil }
        return String(format: "#%02X%02X%02X", Int((c.redComponent * 255).rounded()),
                      Int((c.greenComponent * 255).rounded()), Int((c.blueComponent * 255).rounded()))
    }
}
