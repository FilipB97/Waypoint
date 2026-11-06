#if canImport(CommonCrypto) && canImport(CryptoKit)
import Foundation
import Testing
@testable import WaypointCore

/// Import haseł z Waypoint dla Windows (docs/specs/password-export.md). Te same przypadki co
/// PasswordExportTests w wersji Windows, w tym wspólny wektor z Node `crypto`.
@Suite struct PasswordExportTests {
    static let fast = 1000

    static let vectorURL = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
        .appendingPathComponent("../../../../tests/fixtures/password-export/vector-v1.json").standardized
    static let vectorPassword = "zażółć gęślą jaźń"
    static let vectorPasswords = ["srv-0001": "Hasło-Żółw#1", "profile:prof-0001": "zażółć"]

    /// Zaszyfrowany plik jako słownik, zmiana pola, z powrotem do Data.
    static func edit(_ data: Data, _ change: (inout [String: Any]) -> Void) throws -> Data {
        var obj = try #require(try JSONSerialization.jsonObject(with: data) as? [String: Any])
        change(&obj)
        return try JSONSerialization.data(withJSONObject: obj)
    }

    static func fields(_ data: Data) throws -> [String: Any] {
        try #require(try JSONSerialization.jsonObject(with: data) as? [String: Any])
    }

    static func bytes(_ field: Any?) -> Data? {
        (field as? String).flatMap { Data(base64Encoded: $0) }
    }

    static func sample() throws -> Data {
        try PasswordExport.encrypt(["s1": "tajne"], password: "dobre-haslo", iterations: fast)
    }

    // MARK: Kryterium 2: wektor z niezależnej implementacji

    @Test func K2_wektorZNodeOdszyfrowujeSie() throws {
        let data = try Data(contentsOf: Self.vectorURL)
        let result = try PasswordExport.decrypt(data, password: Self.vectorPassword)
        #expect(result == Self.vectorPasswords)
    }

    @Test func K2_wektorZNodeZlymHaslem() throws {
        let data = try Data(contentsOf: Self.vectorURL)
        #expect(throws: PasswordExport.Failure.wrongPassword) {
            try PasswordExport.decrypt(data, password: "zazolc gesla jazn")
        }
    }

    // MARK: Kryterium 1: zaszyfrowane odszyfrowuje się tym samym hasłem

    @Test func K1_rundaZPolskimiZnakami() throws {
        let passwords = ["abc123": "Hasło-Żółw#1", "profile:p1": "zażółć gęślą jaźń", "x": "\"\\/\n"]
        let data = try PasswordExport.encrypt(passwords, password: "ąęśćżźńół!", iterations: Self.fast)
        #expect(try PasswordExport.decrypt(data, password: "ąęśćżźńół!") == passwords)
    }

    @Test func K1_rundaPustegoSlownika() throws {
        let data = try PasswordExport.encrypt([:], password: "dobre-haslo", iterations: Self.fast)
        #expect(try PasswordExport.decrypt(data, password: "dobre-haslo") == [:])
    }

    @Test func K1_rundaDuzejLiczbyHasel() throws {
        var passwords: [String: String] = [:]
        for i in 0..<2000 { passwords["srv-\(i)"] = String(repeating: "ż", count: i % 50 + 1) }
        let data = try PasswordExport.encrypt(passwords, password: "dobre-haslo", iterations: Self.fast)
        #expect(try PasswordExport.decrypt(data, password: "dobre-haslo") == passwords)
    }

    @Test func K1_formatPlikuZgodnyZeSpecyfikacja() throws {
        let obj = try Self.fields(try Self.sample())
        #expect(obj["Format"] as? String == "waypoint-passwords")
        #expect(obj["Version"] as? Int == 1)
        #expect(obj["Kdf"] as? String == "PBKDF2-SHA256")
        #expect(obj["Iterations"] as? Int == Self.fast)
        let salt = try #require(Self.bytes(obj["Salt"]))
        let nonce = try #require(Self.bytes(obj["Nonce"]))
        let payload = try #require(Self.bytes(obj["Data"]))
        #expect(salt.count == 16)
        #expect(nonce.count == 12)
        #expect(payload.count > 16, "szyfrogram + tag 16 bajtów")
    }

    @Test func K1_domyslnie600000Iteracji() throws {
        let data = try PasswordExport.encrypt(["s1": "x"], password: "dobre-haslo")
        #expect(try Self.fields(data)["Iterations"] as? Int == 600_000)
    }

    /// Ten sam nonce z tym samym kluczem łamie AES-GCM: sól i nonce muszą być losowe.
    @Test func K1_kazdyEksportMaInnaSolINonce() throws {
        let a = try Self.fields(try Self.sample())
        let b = try Self.fields(try Self.sample())
        #expect(a["Salt"] as? String != b["Salt"] as? String)
        #expect(a["Nonce"] as? String != b["Nonce"] as? String)
    }

    // MARK: Kryterium 3: czytelne błędy bez awarii

    @Test func K3_zleHaslo() throws {
        let data = try Self.sample()
        #expect(throws: PasswordExport.Failure.wrongPassword) {
            try PasswordExport.decrypt(data, password: "zle-haslo")
        }
    }

    @Test func K3_pusteHaslo() throws {
        let data = try Self.sample()
        #expect(throws: PasswordExport.Failure.wrongPassword) {
            try PasswordExport.decrypt(data, password: "")
        }
    }

    @Test func K3_zmienionyBajtWData() throws {
        let original = try Self.sample()
        var payload = try #require(Self.bytes(try Self.fields(original)["Data"]))
        for index in [0, payload.count - 1] {
            var bytes = payload
            bytes[index] ^= 0x01
            let tampered = try Self.edit(original) { $0["Data"] = bytes.base64EncodedString() }
            #expect(throws: PasswordExport.Failure.wrongPassword) {
                try PasswordExport.decrypt(tampered, password: "dobre-haslo")
            }
        }
        payload.removeLast()
        let truncated = try Self.edit(original) { $0["Data"] = payload.base64EncodedString() }
        #expect(throws: PasswordExport.Failure.wrongPassword) {
            try PasswordExport.decrypt(truncated, password: "dobre-haslo")
        }
    }

    @Test func K3_obcyFormat() throws {
        let data = try Self.edit(try Self.sample()) { $0["Format"] = "keepass" }
        #expect(throws: PasswordExport.Failure.unrecognized) {
            try PasswordExport.decrypt(data, password: "dobre-haslo")
        }
    }

    @Test func K3_innaWersja() throws {
        let data = try Self.edit(try Self.sample()) { $0["Version"] = 2 }
        #expect(throws: PasswordExport.Failure.unrecognized) {
            try PasswordExport.decrypt(data, password: "dobre-haslo")
        }
    }

    @Test func K3_innyKdf() throws {
        let data = try Self.edit(try Self.sample()) { $0["Kdf"] = "scrypt" }
        #expect(throws: PasswordExport.Failure.unrecognized) {
            try PasswordExport.decrypt(data, password: "dobre-haslo")
        }
    }

    @Test(arguments: [0, -1, 10_000_001, 20_000_000])
    func K3_iteracjeSpozaZakresu(_ iterations: Int) throws {
        let data = try Self.edit(try Self.sample()) { $0["Iterations"] = iterations }
        #expect(throws: PasswordExport.Failure.unrecognized) {
            try PasswordExport.decrypt(data, password: "dobre-haslo")
        }
    }

    @Test func K3_brakujacePole() throws {
        let data = try Self.edit(try Self.sample()) { $0.removeValue(forKey: "Salt") }
        #expect(throws: PasswordExport.Failure.unrecognized) {
            try PasswordExport.decrypt(data, password: "dobre-haslo")
        }
    }

    @Test(arguments: ["", "nie json", "[]", "{}", "{\"Passwords\":{}}"])
    func K3_toNieJestPlikHasel(_ text: String) {
        #expect(throws: PasswordExport.Failure.unrecognized) {
            try PasswordExport.decrypt(Data(text.utf8), password: "dobre-haslo")
        }
    }

    // MARK: Kryterium 4: import tylko istniejących kont

    @Test func K4_selectZostawiaTylkoIstniejaceKonta() {
        let servers = [Server(name: "web", host: "10.0.0.1", proto: .ssh, group: ""),
                       Server(name: "db", host: "10.0.0.2", proto: .ssh, group: "")]
        let profiles = [CredentialProfile(id: "p1", name: "Domena", domain: "CORP", username: "admin")]
        let a = servers[0].id, b = servers[1].id
        let input = [
            a: "haslo-a",
            b: "",                       // puste: nie kasuje istniejącego hasła
            "nieznany-serwer": "x",
            "profile:p1": "haslo-p1",
            "profile:p9": "x",           // profil, którego nie ma na Macu
            "p1": "x",                   // id profilu bez przedrostka to nie konto profilu
            "profile:\(a)": "x",         // id serwera z przedrostkiem profilu
            "profile:": "x",
        ]
        let result = PasswordExport.select(input, servers: servers, profiles: profiles)
        #expect(result.accepted == [a: "haslo-a", "profile:p1": "haslo-p1"])
        #expect(result.skipped == 6)
    }

    @Test func K4_selectPustePusto() {
        let result = PasswordExport.select([:], servers: [], profiles: [])
        #expect(result.accepted.isEmpty)
        #expect(result.skipped == 0)
    }

    @Test func K4_selectBezPolaczenNaMacuPomijaWszystko() {
        let result = PasswordExport.select(["s1": "a", "profile:p1": "b"], servers: [], profiles: [])
        #expect(result.accepted.isEmpty)
        #expect(result.skipped == 2)
    }

    /// Wektor z Windows trafia na Maca z tymi samymi id: oba konta przechodzą.
    @Test func K4_wektorPoSelect() throws {
        let decoded = try PasswordExport.decrypt(try Data(contentsOf: Self.vectorURL), password: Self.vectorPassword)
        let servers = [Server(id: "srv-0001", name: "web", host: "h", proto: .rdp)]
        let profiles = [CredentialProfile(id: "prof-0001", name: "p")]
        let result = PasswordExport.select(decoded, servers: servers, profiles: profiles)
        #expect(result.accepted == Self.vectorPasswords)
        #expect(result.skipped == 0)
    }
}
#endif
