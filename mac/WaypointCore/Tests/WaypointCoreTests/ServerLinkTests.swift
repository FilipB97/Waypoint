import Foundation
import Testing
@testable import WaypointCore

@Suite struct ServerLinkTests {
    let a = Server(name: "MSDEV-B", host: "10.10.15.250", proto: .rdp)
    let b = Server(name: "Biuro — pulpit", host: "rdp.example.com", proto: .rdp)
    let c1 = Server(name: "Node", host: "10.0.0.1", proto: .ssh)
    let c2 = Server(name: "node", host: "10.0.0.2", proto: .ssh)

    @Test func linkPoNazwieIZPolskimiZnakami() throws {
        let all = [a, b, c1, c2]
        let u = try #require(ServerLink.url(for: b, among: all))
        #expect(u.absoluteString.hasPrefix("waypoint://connect/"))
        let key = try #require(ServerLink.query(from: u))
        #expect(ServerLink.find(key, in: all)?.id == b.id)
        // Powtarzająca się nazwa (bez wielkości liter) — link po id.
        let u2 = try #require(ServerLink.url(for: c2, among: all))
        #expect(ServerLink.find(try #require(ServerLink.query(from: u2)), in: all)?.id == c2.id)
    }

    @Test func rozneFormyLinku() throws {
        let all = [a, b]
        #expect(ServerLink.find(try #require(ServerLink.query(from: URL(string: "waypoint://connect/msdev-b")!)), in: all)?.id == a.id)
        #expect(ServerLink.find(try #require(ServerLink.query(from: URL(string: "WAYPOINT://connect?name=MSDEV-B")!)), in: all)?.id == a.id)
        #expect(ServerLink.find(try #require(ServerLink.query(from: URL(string: "waypoint://connect/10.10.15.250")!)), in: all)?.id == a.id)
        #expect(ServerLink.query(from: URL(string: "waypoint://other/x")!) == nil)
        #expect(ServerLink.query(from: URL(string: "https://connect/x")!) == nil)
        #expect(ServerLink.query(from: URL(string: "waypoint://connect/")!) == nil)
        #expect(ServerLink.find("nie-ma", in: all) == nil)
    }
}
