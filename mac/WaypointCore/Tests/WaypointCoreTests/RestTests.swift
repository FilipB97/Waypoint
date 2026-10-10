import Foundation
import Testing
@testable import WaypointCore

/// Te same przypadki co RestTests / RestPostmanTests / RestAuthResolveTests w wersji Windows.
@Suite struct RestBuildTests {
    func req(_ url: String, _ ps: (String, String, Bool)...) -> RestRequest {
        var r = RestRequest(url: url)
        r.queryParams = ps.map { RestKeyValue(key: $0.0, value: $0.1, enabled: $0.2) }
        return r
    }

    @Test func adresy() {
        #expect(RestBuild.url(RestRequest(url: "api.example.com/v1"), [:]) == "https://api.example.com/v1")
        #expect(RestBuild.url(req("https://x.test/a", ("q", "1", true), ("skip", "no", false), ("", "x", true), ("t", "a b", true)), [:])
                == "https://x.test/a?q=1&t=a%20b")
        #expect(RestBuild.url(req("https://x.test/a?z=0", ("q", "1", true)), [:]) == "https://x.test/a?z=0&q=1")
        #expect(RestBuild.url(req("{{base}}/u", ("id", "{{v}}", true)), ["base": "https://x.test", "v": "9"]) == "https://x.test/u?id=9")
        #expect(RestBuild.url(RestRequest(url: "https://x.test/a%7Eb%2Fc"), [:]) == "https://x.test/a%7Eb%2Fc")
        #expect(RestBuild.url(RestRequest(url: "https://x.test/a/../b?q=1+2"), [:]) == "https://x.test/a/../b?q=1+2")
    }

    @Test func zmienne() {
        let vars = ["a": "1", "name": "bob"]
        #expect(RestBuild.subst("{{a}}/{{name}}", vars) == "1/bob")
        #expect(RestBuild.subst("{{a}}{{b}}", vars) == "1{{b}}")
        #expect(RestBuild.subst("{{a}}", [:]) == "{{a}}")
        #expect(RestBuild.subst("Bearer {{ a }}", vars) == "Bearer 1")
        #expect(RestBuild.variables(in: "{{x}} i znowu {{x}} {{y}}") == ["x", "y"])
        #expect(RestBuild.variables(in: #"{"json":{"x":1}}"#).isEmpty)
    }

    @Test func formularz() {
        let vars = ["u": "bob", "sec": "a+b/c="]
        #expect(RestBuild.formBody(raw: "username={{u}}&client_secret={{sec}}&grant_type=password", vars)
                == "username=bob&client_secret=a%2Bb%2Fc%3D&grant_type=password")
        let fields = [RestKeyValue(key: "username", value: "{{u}}"), RestKeyValue(key: "client_secret", value: "{{sec}}"),
                      RestKeyValue(key: "skip", value: "x", enabled: false), RestKeyValue(key: "", value: "y")]
        #expect(RestBuild.formBody(fields: fields, vars) == "username=bob&client_secret=a%2Bb%2Fc%3D")
        #expect(RestBuild.formBody(raw: "", [:]).isEmpty)
    }

    @Test func gotoweZadanie() {
        var r = RestRequest(method: "POST", url: "https://api.example.com/token")
        r.bodyContentType = "application/x-www-form-urlencoded"
        r.formFields = [RestKeyValue(key: "grant_type", value: "password"), RestKeyValue(key: "scope", value: "{{scope}}")]
        let p = RestBuild.prepare(r, vars: ["scope": "openid email"], authType: 1, username: "", secret: "sekret")
        #expect(p.body == "grant_type=password&scope=openid%20email")
        #expect(p.header("Accept") == "*/*" && p.header("Authorization") == "Bearer sekret")
        #expect(p.header("Content-Type")?.contains("x-www-form-urlencoded") == true)
        #expect(p.header("User-Agent")?.hasPrefix("Waypoint") == true)

        var get = RestRequest(method: "get", url: "https://api.example.com/x")
        get.body = #"{"a":1}"#
        get.headers = [RestKeyValue(key: "Accept", value: "application/json")]
        let g = RestBuild.prepare(get, vars: [:], authType: 2, username: "u", secret: "p")
        #expect(g.method == "GET" && g.body == nil && g.header("Accept") == "application/json")
        #expect(g.header("Authorization") == "Basic " + Data("u:p".utf8).base64EncodedString())

        var json = RestRequest(method: "PUT", url: "x")
        json.body = "{\r\n \"a\": \"{{v}}\"\r\n}"
        let j = RestBuild.prepare(json, vars: ["v": "1"], authType: 1, username: "", secret: "")
        #expect(j.body == "{\n \"a\": \"1\"\n}" && j.header("Content-Type") == "application/json; charset=utf-8")
        #expect(j.header("Authorization") == nil)   // Bearer bez tokenu — bez nagłówka
    }

    @Test func audytZmiennych() {
        var r = RestRequest(method: "POST", url: "{{base}}/token")
        r.bodyContentType = "application/x-www-form-urlencoded"
        r.queryParams = [RestKeyValue(key: "v", value: "{{ver}}")]
        r.headers = [RestKeyValue(key: "X-Api", value: "{{api}}")]
        r.formFields = [RestKeyValue(key: "client_secret", value: "{{client_secret}}")]
        let a = RestBuild.audit(r, secret: "{{token}}", username: "", vars: ["base": "https://x", "token": ""])
        #expect(Set(a.missing) == ["ver", "api", "client_secret"] && a.empty == ["token"])
        var get = RestRequest(url: "x")
        get.body = "{{nie_liczy_sie}}"
        #expect(RestBuild.audit(get, secret: "", username: "", vars: [:]).missing.isEmpty)
    }

    @Test func ladnyJson() {
        #expect(RestHTTP.pretty(#"{"b":1,"a":[1,2]}"#) == "{\n  \"a\" : [\n    1,\n    2\n  ],\n  \"b\" : 1\n}")
        #expect(RestHTTP.pretty("nie json") == "nie json")
        #expect(RestHTTP.reasonPhrase(200) == "OK" && RestHTTP.reasonPhrase(404) == "Not Found" && RestHTTP.reasonPhrase(299).isEmpty)
    }
}

@Suite struct RestStoreTests {
    @Test func plikZWindowsIDziedziczenie() throws {
        let json = #"""
        {"srv1":{"BaseUrl":"https://api.test","AuthType":1,"AuthUsername":"","Nowe":true,
          "Folders":[{"Id":"f1","Name":"A","ParentId":"","AuthType":2,"AuthUsername":"adm"},
                     {"Id":"f2","Name":"B","ParentId":"f1","AuthType":3},
                     {"Id":"c1","Name":"Cykl","ParentId":"c2","AuthType":3},{"Id":"c2","Name":"Cykl2","ParentId":"c1","AuthType":3}],
          "Requests":[{"Id":"r1","Name":"x","Method":"POST","Url":"{{base}}","FolderId":"f2","AuthType":3,
                       "Headers":[{"Enabled":false,"Key":"X","Value":"1"}]}],
          "History":[{"Method":"GET","Url":"u","Status":200,"ElapsedMs":42,"WhenIso":"2026-01-01"}]}}
        """#
        let all = try JSONDecoder().decode([String: RestCollection].self, from: Data(json.utf8))
        let c = all["srv1"]!
        #expect(c.baseUrl == "https://api.test" && c.requests[0].method == "POST" && !c.requests[0].headers[0].enabled)
        #expect(c.history[0].status == 200 && c.history[0].elapsedMs == 42)
        let auth = c.resolveAuth(from: "f2", collectionAccount: "restcoll:srv1")
        #expect(auth.type == 2 && auth.username == "adm" && auth.account == "restfolder:f1")
        #expect(c.resolveAuth(from: "", collectionAccount: "restcoll:srv1").account == "restcoll:srv1")
        #expect(c.resolveAuth(from: "c1", collectionAccount: "k").type == 1)   // cykl nie zawiesza
        let out = String(decoding: try JSONEncoder().encode(c), as: UTF8.self)
        #expect(out.contains("\"Nowe\":true") && out.contains("\"SchemaVersion\":2"))
    }

    @Test func zapisSrodowiskaIMigracja() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("wp-rest-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: dir) }
        var c = RestCollection()
        c.environments = [RestEnvironment(name: "dev", variables: [RestVariable(key: "token", value: "abc")])]
        c.record(RestHistoryEntry(method: "GET", url: "u", status: 200, elapsedMs: 1, whenIso: ""))
        try RestStore(directory: dir).put(c, for: "srv")
        #expect(RestStore(directory: dir).collection(for: "srv").history.count == 1)
        let envs = EnvironmentStore(directory: dir).load()   // pierwszy odczyt — migracja z kolekcji
        #expect(envs.map(\.name) == ["dev"] && envs[0].dictionary == ["token": "abc"])
        #expect(FileManager.default.fileExists(atPath: dir.appendingPathComponent("environments.json").path))
        let st = EnvironmentStore(directory: dir)
        st.activeId = envs[0].id
        #expect(st.activeId == envs[0].id)
        try RestStore(directory: dir).remove("srv")
        #expect(RestStore(directory: dir).loadAll().isEmpty)
    }
}

@Suite struct PostmanTests {
    static let sample = #"""
    {"info":{"name":"My API"},
     "header":[{"key":"X-Coll","value":"1"}],
     "item":[{"name":"Users","auth":{"type":"basic","basic":[{"key":"username","value":"fu"},{"key":"password","value":"fp"}]},
              "item":[{"name":"Get user","event":[{"listen":"test","script":{"exec":["pm.test('ok', function(){});"]}}],
                       "request":{"method":"GET","header":[{"key":"Accept","value":"application/json"}],
                       "url":{"raw":"https://api.example.com/users?id=1","query":[{"key":"id","value":"1"},{"key":"skip","value":"x","disabled":true}]},
                       "auth":{"type":"bearer","bearer":[{"key":"token","value":"secret-token"}]}}}]},
             {"name":"Create","request":{"method":"post","url":{"raw":"https://api.example.com/users"},
              "body":{"mode":"raw","raw":"[1,2,3]","options":{"raw":{"language":"json"}}},
              "auth":{"type":"basic","basic":[{"key":"username","value":"u"},{"key":"password","value":"p"}]}}},
             {"name":"Token","request":{"method":"POST","url":"https://x/oauth2/token",
              "body":{"mode":"urlencoded","urlencoded":[{"key":"username","value":"{{username}}"},{"key":"grant_type","value":"password"},
                                                       {"key":"skip","value":"x","disabled":true}]}}}],
     "variable":[{"key":"base_url","value":"https://api.example.com"}]}
    """#

    @Test func kolekcja() throws {
        let r = try PostmanImport.parse(Data(Self.sample.utf8))
        #expect(r.name == "My API" && r.requestCount == 3 && r.collection.folders.map(\.name) == ["Users"])
        let folder = r.collection.folders[0]
        #expect(folder.authType == 2 && folder.authUsername == "fu" && r.secrets[folder.keychainAccount] == "fp")
        let get = r.collection.requests.first { $0.name == "Get user" }!
        #expect(get.method == "GET" && get.url == "https://api.example.com/users" && get.folderId == folder.id)
        #expect(get.queryParams.contains { $0.key == "id" && $0.enabled } && get.queryParams.contains { $0.key == "skip" && !$0.enabled })
        #expect(get.headers.map(\.key) == ["Accept", "X-Coll"])   // nagłówek kolekcji dołączony
        #expect(get.authType == 1 && r.secrets[get.keychainAccount] == "secret-token")
        #expect(get.testScript.contains("pm.test"))
        let post = r.collection.requests.first { $0.name == "Create" }!
        #expect(post.method == "POST" && post.body == "[1,2,3]" && post.bodyContentType == "application/json")
        #expect(post.folderId.isEmpty && post.authType == 2 && post.authUsername == "u" && r.secrets[post.keychainAccount] == "p")
        let tok = r.collection.requests.first { $0.name == "Token" }!
        #expect(tok.bodyContentType == "application/x-www-form-urlencoded" && tok.body == "username={{username}}&grant_type=password")
        #expect(tok.formFields.count == 3 && tok.authType == 3)
        #expect(r.collection.environments.first?.variables.first?.key == "base_url")
        #expect(r.collection.activeEnvironmentId == r.collection.environments.first?.id)
    }

    @Test func srodowisko() throws {
        let json = #"{"name":"prod","values":[{"key":"host","value":"x"},{"key":"token","value":"tajne","type":"secret"}]}"#
        #expect(PostmanImport.looksLikeEnvironment(Data(json.utf8)))
        #expect(!PostmanImport.looksLikeEnvironment(Data(Self.sample.utf8)))
        let (env, blanked) = try PostmanImport.parseEnvironment(Data(json.utf8))
        #expect(env.name == "prod" && env.dictionary == ["host": "x", "token": ""] && blanked == ["token"])
        #expect(throws: PostmanImport.Failure.notCollection) { try PostmanImport.parse(Data(json.utf8)) }
    }
}
