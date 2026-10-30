// swift-tools-version:6.0
// Logika Waypointa dla macOS — tylko Foundation, bez SwiftUI/AppKit. Dzięki temu kompiluje się
// i testuje także na Linuksie (swift test), a interfejs (../App) korzysta z niej jako zależności.
import PackageDescription

// Wbudowany klient RDP (FreeRDP 3). WAYPOINT_FREERDP = prefiks instalacji FreeRDP (include/freerdp3,
// include/winpr3, lib). Na macOS: statyczne biblioteki uniwersalne budowane przez
// scripts/build-freerdp.sh; na Linuksie (testy z serwerem xrdp): /usr z pakietu freerdp3-dev.
// Bez zmiennej moduł RDP kompiluje się jako zaślepka (RdpConnection.isAvailable == false).
let freerdp = Context.environment["WAYPOINT_FREERDP"].flatMap { $0.isEmpty ? nil : $0 }
var rdpC: [CSetting] = []
var rdpLink: [LinkerSetting] = []
if let p = freerdp {
    rdpC = [.define("WP_HAVE_FREERDP"),
            .unsafeFlags(["-I\(p)/include/freerdp3", "-I\(p)/include/winpr3", "-Wno-deprecated-declarations"])]
    rdpLink = [
        .unsafeFlags(["-L\(p)/lib"]),
        .linkedLibrary("freerdp-client3"), .linkedLibrary("freerdp3"), .linkedLibrary("winpr3"),
        // Statyczna budowa (build-freerdp.sh, macOS): OpenSSL z tej samej instalacji i frameworki
        // systemowe, których używa WinPR. Na Linuksie biblioteki dzielone same ciągną zależności.
        .linkedLibrary("ssl", .when(platforms: [.macOS])), .linkedLibrary("crypto", .when(platforms: [.macOS])),
        .linkedLibrary("z", .when(platforms: [.macOS])),
        .linkedLibrary("rdpsnd-common", .when(platforms: [.macOS])),
        .linkedLibrary("remdesk-common", .when(platforms: [.macOS])),
        .linkedFramework("AudioToolbox", .when(platforms: [.macOS])),
        .linkedFramework("CoreAudio", .when(platforms: [.macOS])),
        .linkedFramework("AVFoundation", .when(platforms: [.macOS])),
        .linkedFramework("Carbon", .when(platforms: [.macOS])),   // TIS — układ klawiatury
        .linkedFramework("CoreFoundation", .when(platforms: [.macOS])),
        .linkedFramework("Security", .when(platforms: [.macOS])),
        .linkedFramework("SystemConfiguration", .when(platforms: [.macOS])),
        .linkedFramework("IOKit", .when(platforms: [.macOS])),
        .linkedFramework("CoreServices", .when(platforms: [.macOS])),
    ]
}

let package = Package(
    name: "WaypointCore",
    platforms: [.macOS(.v14)],
    products: [
        .library(name: "WaypointCore", targets: ["WaypointCore"]),
        .library(name: "WaypointRDP", targets: ["WaypointRDP"]),
    ],
    targets: [
        // libcurl (FTP/FTPS) — systemowa na macOS; na Linuksie: libcurl4-openssl-dev.
        .target(name: "CurlShim", linkerSettings: [.linkedLibrary("curl")]),
        .target(name: "WaypointCore", dependencies: ["CurlShim"]),
        .target(name: "RdpShim", cSettings: rdpC, linkerSettings: rdpLink),
        .target(name: "WaypointRDP", dependencies: ["RdpShim"]),
        .testTarget(name: "WaypointCoreTests", dependencies: ["WaypointCore", "CurlShim"]),
        .testTarget(name: "WaypointRDPTests", dependencies: ["WaypointRDP"]),
    ]
)
