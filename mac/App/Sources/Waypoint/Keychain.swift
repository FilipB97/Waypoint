import Foundation
import Security

/// Hasła serwerów w Pęku kluczy macOS (odpowiednik Windows Credential Manager w wersji Windows).
/// Usługa „Waypoint", konto = identyfikator serwera — ten sam, który ma wpis w servers.json.
enum Keychain {
    static let service = "Waypoint"

    private static func query(_ id: String) -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword,
         kSecAttrService as String: service,
         kSecAttrAccount as String: id]
    }

    static func password(for id: String) -> String? {
        var q = query(id)
        q[kSecReturnData as String] = true
        q[kSecMatchLimit as String] = kSecMatchLimitOne
        var out: CFTypeRef?
        guard SecItemCopyMatching(q as CFDictionary, &out) == errSecSuccess, let data = out as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    static func hasPassword(for id: String) -> Bool {
        var q = query(id)
        q[kSecReturnAttributes as String] = true
        return SecItemCopyMatching(q as CFDictionary, nil) == errSecSuccess
    }

    @discardableResult
    static func save(_ password: String, for id: String, label: String) -> Bool {
        let data = Data(password.utf8)
        let update: [String: Any] = [kSecValueData as String: data, kSecAttrLabel as String: label]
        let status = SecItemUpdate(query(id) as CFDictionary, update as CFDictionary)
        if status == errSecSuccess { return true }
        guard status == errSecItemNotFound else { return false }
        var add = query(id)
        add[kSecValueData as String] = data
        add[kSecAttrLabel as String] = label
        add[kSecAttrDescription as String] = "Waypoint — hasło serwera"
        return SecItemAdd(add as CFDictionary, nil) == errSecSuccess
    }

    static func delete(for id: String) {
        SecItemDelete(query(id) as CFDictionary)
    }
}
