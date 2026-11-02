import AppKit
import SwiftUI
import WaypointCore

/// Ustawienia (⌘,). Zapis od razu przy każdej zmianie, jak reszta aplikacji.
struct SettingsView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        TabView {
            GeneralSettings()
                .tabItem { Label(L("set.tab.general"), systemImage: "gearshape") }
            RdpSettings()
                .tabItem { Label(L("set.tab.rdp"), systemImage: "display") }
            StartupSettings()
                .tabItem { Label(L("set.tab.startup"), systemImage: "play.circle") }
            PasswordSettings()
                .tabItem { Label(L("set.tab.passwords"), systemImage: "key") }
        }
        .frame(width: 500)
        .onChange(of: model.settings) { old, new in
            model.saveSettings()
            if old.theme != new.theme { model.applyAppearance() }
            if old.terminalFontSize != new.terminalFontSize { model.applyTerminalFont() }
            if old.reachabilityEnabled != new.reachabilityEnabled
                || old.reachabilityIntervalSec != new.reachabilityIntervalSec
                || old.probeTimeoutSeconds != new.probeTimeoutSeconds {
                model.restartReachability()
            }
            if old.rdpResolution != new.rdpResolution || old.rdpScale != new.rdpScale || old.rdpRetina != new.rdpRetina {
                for case .rdp(let d) in model.sessions { d.view.applyDisplayMode() }   // w locie, bez rozłączania
            }
        }
    }
}

private struct GeneralSettings: View {
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
        .fixedSize(horizontal: false, vertical: true)
    }
}

private struct RdpSettings: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        @Bindable var model = model
        Form {
            Section {
                Picker(L("set.rdp.res"), selection: $model.settings.rdpResolution) {
                    Text(L("set.rdp.res.fit")).tag(RdpDisplay.fit)
                    ForEach(RdpDisplay.resolutions, id: \.self) { Text($0.replacingOccurrences(of: "x", with: " × ")).tag($0) }
                }
                Picker(L("set.rdp.scale"), selection: $model.settings.rdpScale) {
                    ForEach(RdpDisplay.scales, id: \.self) { v in Text(v == 0 ? L("set.rdp.scale.auto") : "\(v)%").tag(v) }
                }
                Toggle(L("set.rdp.retina"), isOn: $model.settings.rdpRetina)
                    .disabled(model.settings.rdpResolution != RdpDisplay.fit)
            } header: {
                Text(L("set.rdp"))
            } footer: {
                Text(L("set.rdp.hint")).foregroundStyle(.secondary)
            }
            Section {
                Toggle(L("set.rdp.diag"), isOn: $model.settings.rdpDiagnostics)
                Button(L("set.rdp.diag.show")) {
                    let f = RdpSession.logDirectory.appendingPathComponent("waypoint-rdp.log")
                    if FileManager.default.fileExists(atPath: f.path) { NSWorkspace.shared.activateFileViewerSelecting([f]) }
                    else { NSWorkspace.shared.open(RdpSession.logDirectory.deletingLastPathComponent()) }
                }
            } footer: {
                Text(L("set.rdp.diag.hint")).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .fixedSize(horizontal: false, vertical: true)
    }
}

/// „Połącz na starcie" — jak w Windows: zaznaczone serwery łączą się po uruchomieniu, w tej kolejności.
private struct StartupSettings: View {
    @Environment(AppModel.self) private var model

    private var candidates: [Server] {
        model.servers.filter { $0.proto?.supportedOnMac == true }
            .sorted { $0.displayName.localizedStandardCompare($1.displayName) == .orderedAscending }
    }

    var body: some View {
        Form {
            Section {
                let chosen = model.settings.autoConnectServerIds.compactMap { id in model.servers.first { $0.id == id } }
                if chosen.isEmpty {
                    Text(L("set.autoconnect.none")).foregroundStyle(.secondary)
                }
                ForEach(Array(chosen.enumerated()), id: \.element.id) { i, s in
                    HStack {
                        Text("\(i + 1).").foregroundStyle(.secondary).monospacedDigit()
                        Avatar(server: s, size: 18)
                        Text(s.displayName)
                        Spacer()
                        Button { move(s, -1) } label: { Image(systemName: "chevron.up") }
                            .buttonStyle(.borderless).disabled(i == 0)
                        Button { move(s, 1) } label: { Image(systemName: "chevron.down") }
                            .buttonStyle(.borderless).disabled(i == chosen.count - 1)
                        Button { model.setAutoConnect(s, false) } label: { Image(systemName: "minus.circle") }
                            .buttonStyle(.borderless).help(L("set.autoconnect.remove"))
                    }
                }
                Menu(L("set.autoconnect.add")) {
                    ForEach(candidates.filter { !model.isAutoConnect($0) }) { s in
                        Button(s.displayName) { model.setAutoConnect(s, true) }
                    }
                }
                .disabled(candidates.allSatisfy { model.isAutoConnect($0) })
            } header: {
                Text(L("set.autoconnect"))
            } footer: {
                Text(L("set.autoconnect.hint")).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .frame(minHeight: 260)
        .fixedSize(horizontal: false, vertical: true)
    }

    private func move(_ s: Server, _ by: Int) {
        var ids = model.settings.autoConnectServerIds
        guard let i = ids.firstIndex(of: s.id), ids.indices.contains(i + by) else { return }
        ids.swapAt(i, i + by)
        model.settings.autoConnectServerIds = ids
    }
}

/// Dostęp do haseł w Pęku kluczy — skąd pytanie o hasło systemu i jak go uniknąć.
private struct PasswordSettings: View {
    var body: some View {
        Form {
            Section {
                LabeledContent(L("set.sign.current"), value: L(Keychain.isStableSignature ? "set.sign.stable" : "set.sign.adhoc"))
                Text(L(Keychain.isStableSignature ? "set.sign.stable.hint" : "set.sign.adhoc.hint"))
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            } header: {
                Text(L("set.tab.passwords"))
            }
        }
        .formStyle(.grouped)
        .fixedSize(horizontal: false, vertical: true)
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
