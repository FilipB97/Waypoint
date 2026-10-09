import Foundation
import Testing
@testable import WaypointCore

/// Te same przypadki co SnippetVarsTests / CommandPaletteTests / RdpUtilsTests w wersji Windows.
@Suite struct SnippetVarsTests {
    let srv = Server(name: "app-01", host: "10.0.0.5", port: 22, username: "root", proto: .ssh, group: "Produkcja")

    @Test func podstawowe() {
        #expect(SnippetVars.expand("ssh {user}@{host} -p {port}", server: srv) == "ssh root@10.0.0.5 -p 22")
        #expect(SnippetVars.expand("{name} / {group} / {protocol}", server: srv) == "app-01 / Produkcja / ssh")
        #expect(SnippetVars.expand("{HOST}", server: srv) == "10.0.0.5")
    }

    @Test func skladniaPowlokiNietknieta() {
        let awk = "ps aux | awk '{print $1}' | sort | uniq -c"
        #expect(SnippetVars.expand(awk, server: srv) == awk)
        #expect(SnippetVars.expand("echo ${host}", server: srv) == "echo ${host}")
        #expect(SnippetVars.expand("echo ${HOME}/log", server: srv) == "echo ${HOME}/log")
        #expect(SnippetVars.expand("{{host}}", server: srv) == "{host}")
        #expect(SnippetVars.expand("systemctl status {serwis}", server: srv) == "systemctl status {serwis}")
        #expect(SnippetVars.expand("find / -name '{host", server: srv) == "find / -name '{host")
    }

    @Test func pustePolaIBrakSerwera() {
        let s = Server(name: "", host: "", port: 23, username: "", proto: .telnet)
        #expect(SnippetVars.expand("{host}|{user}| {port}", server: s) == "|| 23")
        #expect(SnippetVars.expand("{host}| {user}", server: nil) == "| ")
    }

    @Test func klawisze() {
        #expect(SnippetVars.keystrokes("uptime", sendEnter: true) == "uptime\r")
        #expect(SnippetVars.keystrokes("uptime", sendEnter: false) == "uptime")
        #expect(SnippetVars.keystrokes("cd /var/log\r\nls -la", sendEnter: true) == "cd /var/log\rls -la\r")
        #expect(SnippetVars.keystrokes("a\nb", sendEnter: true) == "a\rb\r")
        #expect(SnippetVars.keystrokes("uptime\n", sendEnter: true) == "uptime\r")
    }

    @Test func kazdaNazwaDziala() {
        for n in SnippetVars.names { #expect(SnippetVars.expand("{\(n)}", server: srv) != "{\(n)}" || n == "domain") }
    }
}

@Suite struct SnippetStoreTests {
    @Test func formatWindowsIPorzadki() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("wp-sn-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let json = #"[{"Id":"a1","Name":"","Command":"  journalctl -fu nginx\nkolejna","SendEnter":false},{"Id":"","Name":"pusty","Command":"   "}]"#
        try Data(json.utf8).write(to: dir.appendingPathComponent("snippets.json"))
        let store = SnippetStore(directory: dir)
        let list = store.load()
        #expect(list.count == 1)
        #expect(list[0].name == "journalctl -fu nginx")
        #expect(!list[0].sendEnter)
        try store.save(list + [CommandSnippet(name: "", command: "uptime")])
        let text = try String(contentsOf: store.fileURL, encoding: .utf8)
        #expect(text.contains("\"SendEnter\""))
        #expect(store.load().map(\.name) == ["journalctl -fu nginx", "uptime"])
    }

    @Test func dlugaNazwa() {
        #expect(SnippetStore.firstLine(String(repeating: "x", count: 60)).count == 48)
    }
}

@Suite struct CommandPaletteTests {
    @Test func kolejnoscJakosci() {
        let exact = CommandPalette.score("prod", "prod")
        let prefix = CommandPalette.score("prod-server", "prod")
        let boundary = CommandPalette.score("web prod", "prod")
        let substr = CommandPalette.score("webprodx", "prod")
        #expect(exact > prefix && prefix > boundary && boundary > substr && substr > 0)
        #expect(CommandPalette.score("dashboard", "xyz") < 0)
        #expect(CommandPalette.score("Dashboard", "dash") == CommandPalette.score("dashboard", "DASH"))
        #expect(CommandPalette.score("anything", "  ") == 0)
        #expect(CommandPalette.score("prod", "pro") > CommandPalette.score("prodxxxx", "pro"))
        #expect(CommandPalette.score("xprod", "prod") > CommandPalette.score("xxxxprod", "prod"))
    }
}

@Suite struct QuickConnectTests {
    @Test(arguments: [
        ("10.0.0.5", "10.0.0.5", 3389, "", ""), ("10.0.0.5:3390", "10.0.0.5", 3390, "", ""),
        ("adam@srv1", "srv1", 3389, "adam", ""), ("adam@srv1:3390", "srv1", 3390, "adam", ""),
        ("CORP\\adam@srv1", "srv1", 3389, "adam", "CORP"), ("  CORP\\adam@srv1:3390  ", "srv1", 3390, "adam", "CORP"),
        ("", "", 3389, "", ""), ("[::1]:2222", "::1", 2222, "", ""), ("fe80::1", "fe80::1", 3389, "", ""),
    ] as [(String, String, Int, String, String)])
    func formy(input: String, host: String, port: Int, user: String, domain: String) {
        #expect(QuickConnect.parse(input, defaultPort: 3389) == .init(host: host, port: port, user: user, domain: domain))
    }

    @Test func serwerTymczasowy() {
        #expect(QuickConnect.server(from: "root@web01")?.proto == .ssh)
        #expect(QuickConnect.server(from: "root@web01")?.port == 22)
        #expect(QuickConnect.server(from: "web01:3389")?.proto == .rdp)
        #expect(QuickConnect.server(from: "CORP\\jan@pc")?.proto == .rdp)
        #expect(QuickConnect.server(from: "CORP\\jan@pc")?.port == 3389)
        #expect(QuickConnect.server(from: "   ") == nil)
        #expect(QuickConnect.server(from: "dwa słowa") == nil)
    }
}
