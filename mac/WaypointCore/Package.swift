// swift-tools-version:6.0
// Logika Waypointa dla macOS — tylko Foundation, bez SwiftUI/AppKit. Dzięki temu kompiluje się
// i testuje także na Linuksie (swift test), a interfejs (../App) korzysta z niej jako zależności.
import PackageDescription

let package = Package(
    name: "WaypointCore",
    platforms: [.macOS(.v14)],
    products: [
        .library(name: "WaypointCore", targets: ["WaypointCore"])
    ],
    targets: [
        .target(name: "WaypointCore"),
        .testTarget(name: "WaypointCoreTests", dependencies: ["WaypointCore"])
    ]
)
