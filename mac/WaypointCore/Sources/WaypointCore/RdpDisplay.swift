import Foundation

/// Rozmiar i skala pulpitu zdalnego na Macu. Ustawienia (⌘,) dają wartości domyślne, serwer może je
/// nadpisać (pola tylko dla Maca w `Server.extra` — wersja Windows dopasowuje pulpit do okna).
public enum RdpDisplay {
    /// „fit" = rozdzielczość z rozmiaru karty; inaczej stała „SZERxWYS".
    public static let fit = "fit"
    public static let resolutions = ["1280x720", "1280x800", "1366x768", "1440x900", "1600x900", "1680x1050",
                                     "1920x1080", "1920x1200", "2560x1440", "2560x1600", "3840x2160"]
    /// Skala pulpitu (rozmiar tekstu i elementów Windows); 0 = automatycznie.
    public static let scales = [0, 100, 125, 150, 175, 200, 250, 300]

    public struct Mode: Equatable, Sendable {
        public var width: Int
        public var height: Int
        public var scalePercent: Int
    }

    /// „1920x1080" → (1920, 1080); nil dla „fit" i bzdur.
    public static func parse(_ text: String) -> (Int, Int)? {
        let p = text.lowercased().split(separator: "x").map { Int($0.trimmingCharacters(in: .whitespaces)) }
        guard p.count == 2, let w = p[0], let h = p[1], (200...8192).contains(w), (200...8192).contains(h) else { return nil }
        return (w, h)
    }

    /// Rozmiar sesji w pikselach i skala dla karty o rozmiarze `points` na ekranie o skali `backing`.
    ///  * dopasowanie do okna: piksele fizyczne (`retina`) albo punkty (większe, lekko rozmyte elementy);
    ///  * skala automatyczna: 200% przy pikselach Retina (elementy wielkości jak w macOS), inaczej 100%.
    public static func mode(points: (width: Double, height: Double), backing: Double,
                            resolution: String, scale: Int, retina: Bool) -> Mode {
        if let (w, h) = parse(resolution) {
            return Mode(width: w & ~1, height: h, scalePercent: scale > 0 ? scale : 100)
        }
        let factor = retina ? max(1, backing) : 1
        var w = Int(points.width * factor), h = Int(points.height * factor)
        if points.width < 320 || points.height < 240 { w = Int(1280 * factor); h = Int(800 * factor) }
        w = min(max(w, 200), 8192) & ~1   // RDP wymaga parzystej szerokości
        h = min(max(h, 200), 8192)
        let auto = factor >= 2 ? 200 : 100
        return Mode(width: w, height: h, scalePercent: scale > 0 ? scale : auto)
    }
}

public extension Server {
    /// Rozdzielczość RDP tylko dla tego serwera („" = jak w Ustawieniach).
    var macRdpResolution: String {
        get { if case .string(let v)? = extra["MacRdpResolution"] { return v } else { return "" } }
        set { if newValue.isEmpty { extra.removeValue(forKey: "MacRdpResolution") } else { extra["MacRdpResolution"] = .string(newValue) } }
    }
    /// Skala RDP tylko dla tego serwera (0 = jak w Ustawieniach).
    var macRdpScale: Int {
        get { if case .number(let n)? = extra["MacRdpScale"] { return Int(n) } else { return 0 } }
        set { if newValue == 0 { extra.removeValue(forKey: "MacRdpScale") } else { extra["MacRdpScale"] = .number(Double(newValue)) } }
    }
}
