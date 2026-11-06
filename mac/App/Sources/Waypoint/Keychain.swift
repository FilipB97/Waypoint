import Foundation
import Security

/// Hasła w Pęku kluczy macOS (odpowiednik Windows Credential Manager w wersji Windows).
///
/// Wszystkie hasła leżą w jednym wpisie („sejf": usługa „Waypoint", konto `__waypoint_vault__`, JSON
/// konto → hasło). Powód: macOS pyta o hasło systemu za każdym razem, gdy aplikacja o innym podpisie
/// niż ta, która utworzyła wpis, chce go odczytać — a podpis ad-hoc zmienia się z każdą wersją. Przy
/// osobnym wpisie na serwer po aktualizacji było to jedno pytanie na serwer, z sejfem — jedno w ogóle
/// („Zawsze pozwalaj" je wycisza do następnej wersji). Stały podpis własnym certyfikatem tego nie usuwa:
/// macOS przypina wpis do kompilacji (`partition_id: cdhash:…`) każdej aplikacji bez Team ID od Apple,
/// a każdy zapis przypina go od nowa — zdjęcie tego warunku hasłem logowania nie pomaga (sprawdzone).
///
/// Starsze wersje zapisywały osobne wpisy (konto = identyfikator serwera / profilu) — są czytane
/// i przenoszone do sejfu przy pierwszym użyciu.
enum Keychain {
    static let service = "Waypoint"
    static let vaultAccount = "__waypoint_vault__"

    private static let lock = NSLock()
    private static var vault: [String: String]?

    private static func query(_ account: String) -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword,
         kSecAttrService as String: service,
         kSecAttrAccount as String: account]
    }

    private static func readItem(_ account: String) -> Data? {
        var q = query(account)
        q[kSecReturnData as String] = true
        q[kSecMatchLimit as String] = kSecMatchLimitOne
        var out: CFTypeRef?
        guard SecItemCopyMatching(q as CFDictionary, &out) == errSecSuccess else { return nil }
        return out as? Data
    }

    private static func itemExists(_ account: String) -> Bool {
        var q = query(account)
        q[kSecReturnAttributes as String] = true   // bez danych — nie wywołuje pytania o dostęp
        return SecItemCopyMatching(q as CFDictionary, nil) == errSecSuccess
    }

    private static func writeItem(_ account: String, _ data: Data, label: String, description: String) -> Bool {
        let update: [String: Any] = [kSecValueData as String: data, kSecAttrLabel as String: label]
        let status = SecItemUpdate(query(account) as CFDictionary, update as CFDictionary)
        if status == errSecSuccess { return true }
        guard status == errSecItemNotFound else { return false }
        var add = query(account)
        add[kSecValueData as String] = data
        add[kSecAttrLabel as String] = label
        add[kSecAttrDescription as String] = description
        return SecItemAdd(add as CFDictionary, nil) == errSecSuccess
    }

    /// Sejf w pamięci po pierwszym odczycie (jedno pytanie systemu na uruchomienie, nie na hasło).
    private static func loadVault() -> [String: String] {
        if let v = vault { return v }
        let v = readItem(vaultAccount).flatMap { try? JSONDecoder().decode([String: String].self, from: $0) } ?? [:]
        vault = v
        return v
    }

    private static func storeVault(_ v: [String: String]) -> Bool {
        guard let data = try? JSONEncoder().encode(v),
              writeItem(vaultAccount, data, label: "Waypoint — hasła", description: "Waypoint — sejf haseł") else { return false }
        vault = v
        return true
    }

    static func password(for id: String) -> String? {
        lock.lock(); defer { lock.unlock() }
        let v = loadVault()
        if let p = v[id] { return p }
        // Wpis ze starszej wersji → do sejfu (stary zostaje — starsza wersja aplikacji dalej go znajdzie).
        guard let data = readItem(id), let p = String(data: data, encoding: .utf8) else { return nil }
        var nv = v
        nv[id] = p
        _ = storeVault(nv)
        return p
    }

    static func hasPassword(for id: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return loadVault()[id] != nil || itemExists(id)
    }

    @discardableResult
    static func save(_ password: String, for id: String, label: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        var v = loadVault()
        v[id] = password
        guard storeVault(v) else { return false }
        if itemExists(id) { SecItemDelete(query(id) as CFDictionary) }   // nieaktualny stary wpis
        return true
    }

    static func delete(for id: String) {
        lock.lock(); defer { lock.unlock() }
        var v = loadVault()
        if v.removeValue(forKey: id) != nil { _ = storeVault(v) }
        if itemExists(id) { SecItemDelete(query(id) as CFDictionary) }
    }

    /// Czy aplikacja ma podpis certyfikatem (stały między wersjami), a nie ad-hoc.
    static let isStableSignature: Bool = {
        var code: SecCode?
        guard SecCodeCopySelf([], &code) == errSecSuccess, let code else { return false }
        var staticCode: SecStaticCode?
        guard SecCodeCopyStaticCode(code, [], &staticCode) == errSecSuccess, let staticCode else { return false }
        var info: CFDictionary?
        guard SecCodeCopySigningInformation(staticCode, SecCSFlags(rawValue: kSecCSSigningInformation), &info) == errSecSuccess,
              let dict = info as? [String: Any] else { return false }
        return !((dict[kSecCodeInfoCertificates as String] as? [Any]) ?? []).isEmpty
    }()
}
