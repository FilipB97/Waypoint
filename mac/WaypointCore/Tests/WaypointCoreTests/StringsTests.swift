import Foundation
import Testing
@testable import WaypointCore

@Suite struct StringsTests {
    @Test func tePolaWObuJezykach() {
        #expect(Set(Strings.pl.keys) == Set(Strings.en.keys))
    }

    @Test func teSameSymboleFormatowania() {
        let spec = try! NSRegularExpression(pattern: "%(@|ld|d)")
        func specs(_ s: String) -> [String] {
            spec.matches(in: s, range: NSRange(s.startIndex..., in: s)).map { (s as NSString).substring(with: $0.range) }
        }
        for (k, v) in Strings.pl { #expect(specs(v) == specs(Strings.en[k] ?? ""), "klucz \(k)") }
    }

    /// Każdy klucz użyty w aplikacji (L("…") w ../App, klucze błędów z ServerValidation) istnieje.
    /// Brakujący klucz nie wywróciłby aplikacji, tylko pokazał użytkownikowi „edit.err.host".
    @Test func kluczeUzyteWAplikacjiIstnieja() throws {
        let root = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
            .appendingPathComponent("../../..").standardized
        let re = try NSRegularExpression(pattern: #"(?:L\(|append\()"([a-z0-9.]+)"\)"#)
        var used = Set<String>()
        for dir in ["App/Sources", "WaypointCore/Sources"] {
            let base = root.appendingPathComponent(dir)
            guard let e = FileManager.default.enumerator(at: base, includingPropertiesForKeys: nil) else { continue }
            for case let url as URL in e where url.pathExtension == "swift" {
                let text = try String(contentsOf: url, encoding: .utf8)
                for m in re.matches(in: text, range: NSRange(text.startIndex..., in: text)) {
                    used.insert((text as NSString).substring(with: m.range(at: 1)))
                }
            }
        }
        #expect(used.count > 20, "nie znaleziono źródeł aplikacji pod \(root.path)")
        let missing = used.subtracting(Strings.pl.keys).sorted()
        #expect(missing.isEmpty, "brak tekstów: \(missing)")
    }
}
