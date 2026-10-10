import Foundation
import Testing
@testable import WaypointCore

@Suite struct LocalListingTests {
    @Test func listaIKolejnosc() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("wp-local-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: dir) }
        let fm = FileManager.default
        try fm.createDirectory(at: dir.appendingPathComponent("zeta"), withIntermediateDirectories: true)
        try fm.createDirectory(at: dir.appendingPathComponent("Alfa"), withIntermediateDirectories: true)
        try Data("abc".utf8).write(to: dir.appendingPathComponent("plik10.txt"))
        try Data("a".utf8).write(to: dir.appendingPathComponent("plik2.txt"))
        try Data().write(to: dir.appendingPathComponent(".ukryty"))
        let l = try LocalListing.list(dir)
        #expect(l.map(\.name) == ["Alfa", "zeta", "plik2.txt", "plik10.txt"])
        #expect(l.first { $0.name == "plik10.txt" }?.size == 3)
        #expect(try LocalListing.list(dir, showHidden: true).contains { $0.name == ".ukryty" && $0.isHidden })
    }

    @Test func okruszki() {
        let c = LocalListing.breadcrumbs(URL(fileURLWithPath: "/Users/jan/Projekty/web"), home: "/Users/jan")
        #expect(c.map(\.name) == ["~", "Projekty", "web"] && c.last?.url.path == "/Users/jan/Projekty/web")
        #expect(LocalListing.breadcrumbs(URL(fileURLWithPath: "/etc/ssh"), home: "/Users/jan").map(\.name) == ["/", "etc", "ssh"])
        #expect(LocalListing.breadcrumbs(URL(fileURLWithPath: "/Users/jan"), home: "/Users/jan").map(\.name) == ["~"])
    }
}
