import Foundation

/// Lokalny panel menedżera plików (lewa strona, jak DualFilePanel w Windows): lista katalogu na Macu.
public struct LocalEntry: Identifiable, Equatable, Sendable {
    public var url: URL
    public var name: String
    public var isDirectory: Bool
    public var size: UInt64
    public var modified: Date?
    public var isHidden: Bool
    public var id: String { url.path }
}

public enum LocalListing {
    /// Katalogi najpierw, potem pliki; w obu grupach nazwy jak w Finderze (naturalnie, bez wielkości liter).
    public static func list(_ dir: URL, showHidden: Bool = false) throws -> [LocalEntry] {
        let keys: [URLResourceKey] = [.isDirectoryKey, .fileSizeKey, .contentModificationDateKey, .isHiddenKey]
        let urls = try FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: keys, options: [])
        let entries = urls.compactMap { u -> LocalEntry? in
            let v = try? u.resourceValues(forKeys: Set(keys))
            let hidden = (v?.isHidden ?? false) || u.lastPathComponent.hasPrefix(".")
            if hidden && !showHidden { return nil }
            return LocalEntry(url: u, name: u.lastPathComponent, isDirectory: v?.isDirectory ?? false,
                              size: UInt64(v?.fileSize ?? 0), modified: v?.contentModificationDate, isHidden: hidden)
        }
        return sorted(entries)
    }

    public static func sorted(_ e: [LocalEntry]) -> [LocalEntry] {
        e.sorted {
            if $0.isDirectory != $1.isDirectory { return $0.isDirectory }
            return $0.name.localizedStandardCompare($1.name) == .orderedAscending
        }
    }

    /// Okruszki ścieżki: (nazwa, URL) od korzenia; katalog domowy jako „~".
    public static func breadcrumbs(_ dir: URL, home: String = NSHomeDirectory()) -> [(name: String, url: URL)] {
        let path = dir.standardizedFileURL.path
        var out: [(String, URL)] = []
        var start = "/"
        var rest = path
        if path == home || path.hasPrefix(home + "/") {
            out.append(("~", URL(fileURLWithPath: home, isDirectory: true)))
            start = home
            rest = String(path.dropFirst(home.count))
        } else {
            out.append(("/", URL(fileURLWithPath: "/", isDirectory: true)))
        }
        var cur = URL(fileURLWithPath: start, isDirectory: true)
        for part in rest.split(separator: "/") {
            cur.appendPathComponent(String(part), isDirectory: true)
            out.append((String(part), cur))
        }
        return out
    }
}
