import Foundation
import Testing
@testable import WaypointCore

/// Wpis dokładnie w kształcie, w jakim zapisuje go wersja Windows (System.Text.Json, PascalCase,
/// enum Protocol po nazwie, Status jako liczba, Group może być null).
let windowsServer = """
{
  "SchemaVersion": 1,
  "Id": "3f2c1a9e8b7d4c6e9f0a1b2c3d4e5f60",
  "Name": "Prod — web",
  "Host": "web01.example.com",
  "Port": 2222,
  "Username": "deploy",
  "Domain": "",
  "Protocol": "Ssh",
  "PrivateKeyPath": "C:\\\\Users\\\\filip\\\\.ssh\\\\id_ed25519",
  "Tunnels": ["8080:localhost:80"],
  "FtpEncryption": 0,
  "FtpAnonymous": false,
  "UseWindowsAccount": false,
  "Group": "Produkcja",
  "Tags": ["prod", "web"],
  "Notes": "nginx + php-fpm",
  "Initials": null,
  "AvatarColor": "#3B82F6",
  "SavePassword": true,
  "CredentialProfileId": "",
  "Pinned": true,
  "RedirectClipboard": true,
  "AudioMode": 0,
  "AuthenticationLevel": 2,
  "RemoteAppProgram": "||Excel",
  "GatewayHostname": "rdg.example.com",
  "GatewayUsageMethod": 1,
  "Status": 2
}
"""

@Suite struct ServerCodingTests {
    @Test func czytaFormatWindows() throws {
        let s = try JSONDecoder().decode(Server.self, from: Data(windowsServer.utf8))
        #expect(s.id == "3f2c1a9e8b7d4c6e9f0a1b2c3d4e5f60")
        #expect(s.name == "Prod — web")
        #expect(s.proto == .ssh)
        #expect(s.port == 2222)
        #expect(s.username == "deploy")
        #expect(s.group == "Produkcja")
        #expect(s.tags == ["prod", "web"])
        #expect(s.tunnels == ["8080:localhost:80"])
        #expect(s.pinned)
        #expect(s.avatarColor == "#3B82F6")
        // Pola, których Mac nie używa, są przechowane, a nie zgubione.
        #expect(s.extra["RemoteAppProgram"] == .string("||Excel"))
        #expect(s.extra["GatewayUsageMethod"] == .number(1))
        #expect(s.extra["Initials"] == .null)
    }

    @Test func zapisIOdczytNieGubiaPolWindows() throws {
        let s = try JSONDecoder().decode(Server.self, from: Data(windowsServer.utf8))
        let data = try ServerStore.encode([s])
        let back = try JSONDecoder().decode([Server].self, from: data)
        #expect(back == [s])

        // Porównanie na poziomie JSON z oryginałem: każde pole Windows wraca z tą samą wartością.
        let original = try JSONSerialization.jsonObject(with: Data(windowsServer.utf8)) as! [String: Any]
        let written = (try JSONSerialization.jsonObject(with: data) as! [[String: Any]])[0]
        #expect(Set(original.keys) == Set(written.keys))
        for (k, v) in original {
            #expect("\(v)" == "\(written[k]!)", "pole \(k)")
        }
        // Liczby całkowite bez części ułamkowej — inaczej Windows nie wczyta int-a.
        let text = String(decoding: data, as: UTF8.self)
        #expect(text.contains("\"GatewayUsageMethod\" : 1,") || text.contains("\"GatewayUsageMethod\" : 1\n"))
        #expect(!text.contains("1.0"))
    }

    @Test func brakujacePolaJakWWindows() throws {
        // Wpis sprzed obsługi SSH: bez Protocol i bez Port → RDP na 3389; Group = null → bez grupy.
        let s = try JSONDecoder().decode(Server.self, from: Data(#"{"Name":"stary","Host":"10.0.0.5","Group":null}"#.utf8))
        #expect(s.proto == .rdp)
        #expect(s.port == 3389)
        #expect(s.group == "")
        #expect(s.id.count == 32)
    }

    @Test func nieznanyProtokolZostaje() throws {
        let s = try JSONDecoder().decode(Server.self, from: Data(#"{"Host":"h","Protocol":"Mosh","Port":60001}"#.utf8))
        #expect(s.proto == nil)
        #expect(s.protocolName == "Mosh")
        let back = try JSONDecoder().decode([Server].self, from: ServerStore.encode([s]))
        #expect(back[0].protocolName == "Mosh")
    }

    @Test func inicjaly() {
        #expect(Server(name: "Prod DB").initials == "PD")
        #expect(Server(name: "nginx").initials == "NG")
        #expect(Server(name: "", host: "10.1.2.3").initials == "10")
        #expect(Server(name: "", host: "").initials == "?")
    }

    @Test func nowyIdJakGuidN() {
        let id = Server.newId()
        #expect(id.count == 32)
        #expect(id.allSatisfy { $0.isHexDigit && !$0.isUppercase })
    }
}

@Suite struct ServerStoreTests {
    private func tempDir() -> URL {
        let d = FileManager.default.temporaryDirectory.appendingPathComponent("wp-\(UUID().uuidString)")
        try? FileManager.default.createDirectory(at: d, withIntermediateDirectories: true)
        return d
    }

    @Test func brakPliku() {
        #expect(ServerStore(directory: tempDir()).load() == .missing)
    }

    @Test func zapisOdczytIKopia() throws {
        let store = ServerStore(directory: tempDir())
        let a = Server(name: "a", host: "a.example")
        try store.save([a])
        try store.save([a, Server(name: "b", host: "b.example")])
        guard case .ok(let list) = store.load() else { Issue.record("brak listy"); return }
        #expect(list.map(\.name) == ["a", "b"])
        let bak = try Data(contentsOf: store.directory.appendingPathComponent("servers.json.bak"))
        #expect(try JSONDecoder().decode([Server].self, from: bak).map(\.name) == ["a"])
    }

    @Test func uszkodzonyPlikNieJestNadpisywanyPustka() throws {
        let store = ServerStore(directory: tempDir())
        try store.save([Server(name: "dobry", host: "h")])
        try store.save([Server(name: "dobry", host: "h"), Server(name: "drugi", host: "h2")])
        try Data("{ to nie jest json".utf8).write(to: store.fileURL)

        guard case .corrupt(let preserved, let fallback) = store.load() else { Issue.record("oczekiwano .corrupt"); return }
        #expect(FileManager.default.fileExists(atPath: preserved))
        #expect(try String(contentsOfFile: preserved, encoding: .utf8) == "{ to nie jest json")
        #expect(fallback.map(\.name) == ["dobry"])   // z .bak
    }
}

@Suite struct ProfileImportTests {
    @Test func profilZWindows() throws {
        let profile = """
        { "Version": 1, "Settings": { "Theme": "Dark", "Language": "pl" }, "Servers": [ \(windowsServer) ] }
        """
        let list = try ProfileImport.parse(Data(profile.utf8))
        #expect(list.count == 1)
        #expect(list[0].host == "web01.example.com")
    }

    @Test func samServersJson() throws {
        let list = try ProfileImport.parse(Data("[\(windowsServer)]".utf8))
        #expect(list.count == 1)
    }

    @Test func obcyKsztaltIPusty() {
        #expect(throws: ProfileImport.Failure.unrecognized) { try ProfileImport.parse(Data(#"{"foo":1}"#.utf8)) }
        #expect(throws: ProfileImport.Failure.unrecognized) { try ProfileImport.parse(Data("nie json".utf8)) }
        #expect(throws: ProfileImport.Failure.noServers) { try ProfileImport.parse(Data("[]".utf8)) }
        #expect(throws: ProfileImport.Failure.noServers) {
            try ProfileImport.parse(Data(#"{"Version":1,"Settings":{},"Servers":[]}"#.utf8))
        }
    }

    @Test func ponownyImportNieDublujeIPodmieniaWMiejscu() {
        let a = Server(id: "a", name: "A", host: "a")
        let b = Server(id: "b", name: "B", host: "b")
        var b2 = b; b2.host = "b-nowy"
        let c = Server(id: "c", name: "C", host: "c")
        let r = ProfileImport.merge(existing: [a, b], imported: [b2, c, a])
        #expect(r.servers.map(\.id) == ["a", "b", "c"])
        #expect(r.servers[1].host == "b-nowy")
        #expect(r.added == 1)
        #expect(r.updated == 1)
    }
}

@Suite struct ServerListTests {
    let servers = [
        Server(name: "Łódź web", host: "lodz.example", group: "Klienci", tags: ["prod"]),
        Server(name: "Baza", host: "db.internal", proto: .rdp, group: "Produkcja", pinned: true),
        Server(name: "Router", host: "192.168.1.1", proto: .telnet),
        Server(name: "Aplikacja", host: "app.internal", group: "produkcja2"),
    ]

    @Test func szukanieBezPolskichZnakowIWielkosciLiter() {
        #expect(ServerList.sections(servers, query: "LODZ").flatMap(\.servers).map(\.name) == ["Łódź web"])
        #expect(ServerList.sections(servers, query: "prod web").flatMap(\.servers).map(\.name) == ["Łódź web"])
        #expect(ServerList.sections(servers, query: "rdp").flatMap(\.servers).map(\.name) == ["Baza", "Baza"])
        #expect(ServerList.sections(servers, query: "nic-takiego").isEmpty)
    }

    @Test func ukladSekcji() {
        let s = ServerList.sections(servers)
        #expect(s.map(\.kind) == [.pinned, .group("Klienci"), .group("Produkcja"), .group("produkcja2"), .ungrouped])
        #expect(s[0].servers.map(\.name) == ["Baza"])
        #expect(s.last?.servers.map(\.name) == ["Router"])
    }

    @Test func nazwyGrup() {
        #expect(ServerList.groupNames(servers) == ["Klienci", "Produkcja", "produkcja2"])
    }
}

@Suite struct ValidationTests {
    @Test func wymaganyAdresIPort() {
        #expect(ServerValidation.problems(Server(host: "")) == ["edit.err.host"])
        #expect(ServerValidation.problems(Server(host: "a b")) == ["edit.err.hostspace"])
        #expect(ServerValidation.problems(Server(host: "h", port: 0)) == ["edit.err.port"])
        #expect(ServerValidation.problems(Server(host: "h", port: 70000)) == ["edit.err.port"])
        #expect(ServerValidation.problems(Server(host: "h")).isEmpty)
    }

    @Test func porzadkowanie() {
        let n = ServerValidation.normalized(Server(name: "  x ", host: " h\n", tags: [" prod", "", "PROD", "web "]))
        #expect(n.name == "x")
        #expect(n.host == "h")
        #expect(n.tags == ["prod", "web"])
    }
}
