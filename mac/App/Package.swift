// swift-tools-version:6.0
// Aplikacja Waypoint dla macOS (SwiftUI). Buduje się tylko na macOS; logika żyje w ../WaypointCore.
// Paczkę .app składa scripts/bundle-app.sh (Info.plist, ikona, podpis ad-hoc).
import PackageDescription

let package = Package(
    name: "Waypoint",
    platforms: [.macOS(.v14)],
    dependencies: [
        .package(path: "../WaypointCore")
    ],
    targets: [
        .executableTarget(
            name: "Waypoint",
            dependencies: [.product(name: "WaypointCore", package: "WaypointCore")]
        )
    ]
)
