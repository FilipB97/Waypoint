import Foundation

/// Presety palety — te same id, nazwy i kolory co `ThemePresets` w Windows (ThemeVariantDark/Light).
/// Na Macu interfejs rysuje system (jasny/ciemny), a preset koloruje terminal: tło = Canvas,
/// tekst = TextPrim, kursor i zaznaczenie = Accent (jak `TerminalTheme.From` w Windows).
public struct ThemePreset: Equatable, Sendable, Identifiable {
    public let id: String
    public let name: String
    public let light: Bool
    public let canvas: String, panel: String, border: String, textPrim: String, accent: String

    public static let defaultId = "Waypoint"

    public static let all: [ThemePreset] = [
        .init(id: "Waypoint", name: "Waypoint", light: false, canvas: "#14151B", panel: "#282A36", border: "#454751", textPrim: "#E7E8EE", accent: "#6C6DFF"),
        .init(id: "AtomOne", name: "Atom One Dark", light: false, canvas: "#282C34", panel: "#2F343D", border: "#3B414D", textPrim: "#ABB2BF", accent: "#61AFEF"),
        .init(id: "GitHubDark", name: "GitHub Dark", light: false, canvas: "#0D1117", panel: "#161B22", border: "#30363D", textPrim: "#C9D1D9", accent: "#58A6FF"),
        .init(id: "ClaudeDark", name: "Claude Dark", light: false, canvas: "#262624", panel: "#30302E", border: "#45443F", textPrim: "#ECEBE6", accent: "#D97757"),
        .init(id: "TokyoNight", name: "Tokyo Night", light: false, canvas: "#1A1B26", panel: "#24283B", border: "#3B4261", textPrim: "#C0CAF5", accent: "#7AA2F7"),
        .init(id: "Nord", name: "Nord", light: false, canvas: "#2E3440", panel: "#3B4252", border: "#4C566A", textPrim: "#ECEFF4", accent: "#88C0D0"),
        .init(id: "Waypoint", name: "Waypoint", light: true, canvas: "#E6E9EE", panel: "#FAFBFC", border: "#C8CACD", textPrim: "#1B1D22", accent: "#5B4BD6"),
        .init(id: "GitHubLight", name: "GitHub Light", light: true, canvas: "#FFFFFF", panel: "#F6F8FA", border: "#D0D7DE", textPrim: "#1F2328", accent: "#0969DA"),
        .init(id: "Solarized", name: "Solarized Light", light: true, canvas: "#FDF6E3", panel: "#EEE8D5", border: "#D9D2B8", textPrim: "#073642", accent: "#268BD2"),
        .init(id: "ClaudeLight", name: "Claude Light", light: true, canvas: "#F5F4EE", panel: "#FFFFFF", border: "#DDDBCF", textPrim: "#2A2925", accent: "#C15F3C"),
        .init(id: "Catppuccin", name: "Catppuccin Latte", light: true, canvas: "#EFF1F5", panel: "#FFFFFF", border: "#CCD0DA", textPrim: "#4C4F69", accent: "#1E66F5"),
        .init(id: "OneLight", name: "One Light", light: true, canvas: "#FAFAFA", panel: "#FFFFFF", border: "#D3D3D4", textPrim: "#383A42", accent: "#4078F2"),
    ]

    public static func list(light: Bool) -> [ThemePreset] { all.filter { $0.light == light } }

    /// Preset o danym id w danym trybie; nieznane id → „Waypoint" (jak w Windows).
    public static func find(_ id: String, light: Bool) -> ThemePreset {
        all.first { $0.id == id && $0.light == light } ?? all.first { $0.id == defaultId && $0.light == light }!
    }

    /// Kolory terminala z presetu; `accentOverride` (#RRGGBB, jak AccentColor w Windows) zastępuje akcent.
    public func terminal(accentOverride: String = "") -> TerminalColors {
        let acc = RGB(hex: accentOverride) ?? RGB(hex: accent)!
        return TerminalColors(background: RGB(hex: canvas)!, foreground: RGB(hex: textPrim)!, cursor: acc,
                              selection: acc, selectionAlpha: light ? 0.22 : 0.34)
    }
}

public struct RGB: Equatable, Sendable {
    public var r: Double, g: Double, b: Double

    /// „#RRGGBB" albo „#AARRGGBB" (format WPF); nil dla pustego/niepoprawnego.
    public init?(hex: String) {
        var s = hex.trimmingCharacters(in: .whitespaces)
        if s.hasPrefix("#") { s.removeFirst() }
        guard s.count == 6 || s.count == 8, let v = UInt64(s, radix: 16) else { return nil }
        let rgb = s.count == 8 ? v & 0xFFFFFF : v
        r = Double((rgb >> 16) & 0xFF) / 255; g = Double((rgb >> 8) & 0xFF) / 255; b = Double(rgb & 0xFF) / 255
    }
}

public struct TerminalColors: Equatable, Sendable {
    public var background: RGB, foreground: RGB, cursor: RGB, selection: RGB
    public var selectionAlpha: Double
}

/// Wygląd aplikacji — wartości jak `Theme` w Windows („Dark" | „Light" | „System").
public enum AppTheme: String, CaseIterable, Sendable {
    case system = "System", light = "Light", dark = "Dark"
}
