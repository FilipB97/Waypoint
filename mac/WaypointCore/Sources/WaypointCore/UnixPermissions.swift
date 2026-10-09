import Foundation

/// Uprawnienia uniksowe w postaci jak w `ls -l` (kolumna w panelu plików).
public enum UnixPermissions {
    /// 0o755 → „rwxr-xr-x"; s/S i t/T dla setuid/setgid/sticky; `directory` dodaje „d" z przodu.
    public static func symbolic(_ mode: Int, directory: Bool = false) -> String {
        func triplet(_ bits: Int, _ special: Bool, _ ch: Character) -> String {
            let r = bits & 4 != 0 ? "r" : "-", w = bits & 2 != 0 ? "w" : "-"
            let x = bits & 1 != 0
            let last: String = special ? String(x ? ch : Character(ch.uppercased())) : (x ? "x" : "-")
            return r + w + last
        }
        return (directory ? "d" : "-")
            + triplet((mode >> 6) & 7, mode & 0o4000 != 0, "s")
            + triplet((mode >> 3) & 7, mode & 0o2000 != 0, "s")
            + triplet(mode & 7, mode & 0o1000 != 0, "t")
    }
}
