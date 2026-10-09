import Foundation

public enum LineEnding: String, Sendable { case lf = "LF", crlf = "CRLF", cr = "CR" }

/// Format pliku otwartego w edytorze: kodowanie i końce linii TAKIE, JAKIE BYŁY na serwerze
/// (port `TextFileFormat.cs`). Edytor dostaje tekst z samymi „\n", zapis odtwarza format oryginału —
/// dopisanie linii do pliku z Linuksa nie wstawi w nim CRLF, a plik w Latin-1 nie stanie się po cichu UTF-8.
public struct TextFileFormat: Equatable, Sendable {
    public static let utf8 = "UTF-8", utf8BOM = "UTF-8 (BOM)", utf16LE = "UTF-16 LE", utf16BE = "UTF-16 BE", latin1 = "Latin-1"

    public private(set) var encodingName: String
    public private(set) var eol: LineEnding
    /// Plik miał różne końce linii — zapis ujednolici je do `eol`.
    public private(set) var mixedEol: Bool

    public var eolLabel: String { eol.rawValue }

    /// Dekoduje bajty: BOM (jednoznaczny) → ścisłe UTF-8 → Latin-1 (nigdy nie zawodzi).
    public static func detect(_ data: Data) -> (TextFileFormat, String) {
        let b = [UInt8](data.prefix(3))
        var enc = utf8
        var raw: String
        if b.count >= 3, b[0] == 0xEF, b[1] == 0xBB, b[2] == 0xBF {
            enc = utf8BOM; raw = String(decoding: data.dropFirst(3), as: UTF8.self)
        } else if b.count >= 2, b[0] == 0xFF, b[1] == 0xFE {
            enc = utf16LE; raw = String(data: data.dropFirst(2), encoding: .utf16LittleEndian) ?? ""
        } else if b.count >= 2, b[0] == 0xFE, b[1] == 0xFF {
            enc = utf16BE; raw = String(data: data.dropFirst(2), encoding: .utf16BigEndian) ?? ""
        } else if let s = String(data: data, encoding: .utf8) {
            raw = s
        } else {
            enc = latin1; raw = String(data: data, encoding: .isoLatin1) ?? ""
        }

        var lf = 0, crlf = 0, cr = 0
        var prevCR = false
        for u in raw.utf8 {
            if prevCR {
                prevCR = false
                if u == 0x0A { crlf += 1; continue }
                cr += 1
            }
            if u == 0x0D { prevCR = true } else if u == 0x0A { lf += 1 }
        }
        if prevCR { cr += 1 }
        // Remis rozstrzyga LF — edytujemy głównie pliki z serwerów linuksowych.
        var eol = LineEnding.lf
        if crlf > lf && crlf >= cr { eol = .crlf } else if cr > lf && cr > crlf { eol = .cr }
        let kinds = (lf > 0 ? 1 : 0) + (crlf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0)
        return (TextFileFormat(encodingName: enc, eol: eol, mixedEol: kinds > 1), normalizeToLF(raw))
    }

    public static func normalizeToLF(_ s: String) -> String {
        s.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n")
    }

    public enum EncodeResult: Equatable, Sendable {
        case ok(Data)
        /// Znaku nie da się zapisać w kodowaniu oryginału (Latin-1) — wołający proponuje UTF-8.
        case unencodable(String)
    }

    public func encode(_ text: String) -> EncodeResult {
        var s = Self.normalizeToLF(text)
        if eol == .crlf { s = s.replacingOccurrences(of: "\n", with: "\r\n") }
        else if eol == .cr { s = s.replacingOccurrences(of: "\n", with: "\r") }
        switch encodingName {
        case Self.latin1:
            if let bad = s.unicodeScalars.first(where: { $0.value > 0xFF }) {
                return .unencodable(String(Character(bad)))
            }
            return .ok(Data(s.unicodeScalars.map { UInt8($0.value) }))
        case Self.utf16LE: return .ok(Data([0xFF, 0xFE]) + (s.data(using: .utf16LittleEndian) ?? Data()))
        case Self.utf16BE: return .ok(Data([0xFE, 0xFF]) + (s.data(using: .utf16BigEndian) ?? Data()))
        case Self.utf8BOM: return .ok(Data([0xEF, 0xBB, 0xBF]) + Data(s.utf8))
        default: return .ok(Data(s.utf8))
        }
    }

    /// Ten sam format, ale UTF-8 bez BOM — gdy tekst nie mieści się w Latin-1.
    public func asUTF8() -> TextFileFormat { TextFileFormat(encodingName: Self.utf8, eol: eol, mixedEol: mixedEol) }

    /// Czy to dane binarne (jak git/grep): bajt zerowy przesądza, do tego próg znaków sterujących.
    public static func looksBinary(_ data: Data) -> Bool {
        let head = [UInt8](data.prefix(8000))
        guard !head.isEmpty else { return false }
        if head.count >= 2 && ((head[0] == 0xFF && head[1] == 0xFE) || (head[0] == 0xFE && head[1] == 0xFF)) { return false }
        if head.count >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF { return false }
        var ctrl = 0
        for b in head {
            if b == 0 { return true }
            if b < 0x20 && b != 0x09 && b != 0x0A && b != 0x0D && b != 0x0C && b != 0x1B { ctrl += 1 }
        }
        return ctrl * 100 / head.count > 10
    }
}
