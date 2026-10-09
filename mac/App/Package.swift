// swift-tools-version:6.0
// Aplikacja Waypoint dla macOS (SwiftUI). Buduje się tylko na macOS; logika żyje w ../WaypointCore.
// Paczkę .app składa scripts/bundle-app.sh (Info.plist, ikona, podpis ad-hoc).
import PackageDescription

let package = Package(
    name: "Waypoint",
    platforms: [.macOS(.v14)],
    dependencies: [
        .package(path: "../WaypointCore"),
        // Emulator terminala (AppKit) — ten sam autor co terminal w Visual Studio for Mac.
        .package(url: "https://github.com/migueldeicaza/SwiftTerm", exact: "1.20.0"),
    ],
    targets: [
        .executableTarget(
            name: "Waypoint",
            dependencies: [
                .product(name: "WaypointCore", package: "WaypointCore"),
                .product(name: "SwiftTerm", package: "SwiftTerm"),
            ]
        )
    ],
    // Interfejs w trybie Swift 5: AppKit i SwiftTerm nie są jeszcze w pełni oznaczone pod ścisłą
    // współbieżność Swift 6. Logika (WaypointCore) jest kompilowana w trybie Swift 6.
    swiftLanguageModes: [.v5]
)
