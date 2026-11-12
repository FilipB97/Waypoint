import Foundation

/// Sortowanie list plików po kolumnach tabeli. Katalogi zawsze przed plikami (jak w Eksploratorze
/// i w wersji Windows), w obu grupach według kolumn wybranych w nagłówku tabeli.
public enum FileSort {
    public static func sorted<T>(_ items: [T], isDirectory: (T) -> Bool, by order: [KeyPathComparator<T>]) -> [T] {
        items.sorted { a, b in
            let da = isDirectory(a), db = isDirectory(b)
            if da != db { return da }
            for c in order {
                switch c.compare(a, b) {
                case .orderedAscending: return true
                case .orderedDescending: return false
                case .orderedSame: continue
                }
            }
            return false
        }
    }
}

/// Klucze sortowania kolumn bez wartości (brak daty / uprawnień) — na początku przy rosnącym porządku.
extension SftpEntry {
    public var modifiedSortKey: Date { attributes.modified ?? .distantPast }
    public var modeSortKey: UInt32 { attributes.mode ?? 0 }
}

extension LocalEntry {
    public var modifiedSortKey: Date { modified ?? .distantPast }
}
