// Tylko macOS: CommonCrypto i CryptoKit nie istnieją na Linuksie (tam rdzeń buduje się bez tej funkcji).
#if canImport(CommonCrypto) && canImport(CryptoKit)
import CommonCrypto
import CryptoKit
import Foundation

/// Plik z hasłami z „Eksportuj hasła…" w wersji Windows (docs/specs/password-export.md):
/// PBKDF2-SHA256 + AES-256-GCM. Klucze = konta w Pęku kluczy (`<id serwera>`, `profile:<id profilu>`).
public enum PasswordExport {
    public enum Failure: Error, Equatable {
        case unrecognized
        case wrongPassword
    }

    private static let formatName = "waypoint-passwords"
    private static let kdfName = "PBKDF2-SHA256"
    private static let maxIterations = 10_000_000

    private struct FileData: Codable {
        var format: String
        var version: Int
        var kdf: String
        var iterations: Int
        var salt: String
        var nonce: String
        var data: String
        enum CodingKeys: String, CodingKey {
            case format = "Format", version = "Version", kdf = "Kdf", iterations = "Iterations"
            case salt = "Salt", nonce = "Nonce", data = "Data"
        }
    }

    private struct Payload: Codable {
        var passwords: [String: String]
        enum CodingKeys: String, CodingKey { case passwords = "Passwords" }
    }

    public static func encrypt(_ passwords: [String: String], password: String, iterations: Int = 600_000) throws -> Data {
        let salt = Data((0..<16).map { _ in UInt8.random(in: .min ... .max) })
        let key = try deriveKey(password, salt: salt, iterations: iterations)
        let box = try AES.GCM.seal(JSONEncoder().encode(Payload(passwords: passwords)), using: key)
        let file = FileData(format: formatName, version: 1, kdf: kdfName, iterations: iterations,
                            salt: salt.base64EncodedString(), nonce: Data(box.nonce).base64EncodedString(),
                            data: (box.ciphertext + box.tag).base64EncodedString())
        let e = JSONEncoder()
        e.outputFormatting = [.prettyPrinted, .sortedKeys]
        return try e.encode(file)
    }

    public static func decrypt(_ data: Data, password: String) throws -> [String: String] {
        guard let file = try? JSONDecoder().decode(FileData.self, from: data),
              file.format == formatName, file.version == 1, file.kdf == kdfName,
              (1...maxIterations).contains(file.iterations),
              let salt = Data(base64Encoded: file.salt),
              let nonceData = Data(base64Encoded: file.nonce), let nonce = try? AES.GCM.Nonce(data: nonceData),
              let sealed = Data(base64Encoded: file.data), sealed.count >= 16
        else { throw Failure.unrecognized }

        let key = try deriveKey(password, salt: salt, iterations: file.iterations)
        let plain: Data
        do {
            let box = try AES.GCM.SealedBox(nonce: nonce, ciphertext: sealed.dropLast(16), tag: sealed.suffix(16))
            plain = try AES.GCM.open(box, using: key)
        } catch { throw Failure.wrongPassword }
        guard let payload = try? JSONDecoder().decode(Payload.self, from: plain) else { throw Failure.unrecognized }
        return payload.passwords
    }

    /// Tylko hasła, które mają gdzie trafić na Macu: istniejący serwer albo profil poświadczeń.
    /// Puste wartości nie kasują zapisanych haseł — liczą się jako pominięte.
    public static func select(_ passwords: [String: String], servers: [Server], profiles: [CredentialProfile])
        -> (accepted: [String: String], skipped: Int) {
        let accounts = Set(servers.map(\.id) + profiles.map(\.keychainAccount))
        let accepted = passwords.filter { accounts.contains($0.key) && !$0.value.isEmpty }
        return (accepted, passwords.count - accepted.count)
    }

    private static func deriveKey(_ password: String, salt: Data, iterations: Int) throws -> SymmetricKey {
        let pw = Array(password.utf8)
        var key = [UInt8](repeating: 0, count: 32)
        let status = salt.withUnsafeBytes { s in
            CCKeyDerivationPBKDF(CCPBKDFAlgorithm(kCCPBKDF2), password, pw.count,
                                 s.bindMemory(to: UInt8.self).baseAddress, salt.count,
                                 CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA256), UInt32(iterations), &key, key.count)
        }
        guard status == kCCSuccess else { throw Failure.unrecognized }
        return SymmetricKey(data: key)
    }
}
#endif
