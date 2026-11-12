import Foundation
import Testing
@testable import WaypointCore

/// Sortowanie list plików po kolumnie tabeli (panel zdalny SFTP/FTP i lokalny).
/// Testy czarnoskrzynkowe ze specyfikacji: katalogi zawsze przed plikami, w grupach wg komparatorów.
@Suite struct FileSortTests {
    // MARK: - Pomocnicze

    private static let dirType: UInt32 = 0o040000
    private static let fileType: UInt32 = 0o100000
    private static let linkType: UInt32 = 0o120000

    private func file(_ name: String, size: UInt64? = nil, mtime: UInt32? = nil, mode: UInt32 = 0o644) -> SftpEntry {
        SftpEntry(name: name, path: "/r/\(name)",
                  attributes: SftpAttributes(size: size, permissions: Self.fileType | mode, modifyTime: mtime))
    }

    private func dir(_ name: String, size: UInt64? = nil, mtime: UInt32? = nil, mode: UInt32 = 0o755) -> SftpEntry {
        SftpEntry(name: name, path: "/r/\(name)",
                  attributes: SftpAttributes(size: size, permissions: Self.dirType | mode, modifyTime: mtime))
    }

    private func link(_ name: String, toDir: Bool, size: UInt64? = nil) -> SftpEntry {
        SftpEntry(name: name, path: "/r/\(name)",
                  attributes: SftpAttributes(size: size, permissions: Self.linkType | 0o777),
                  linkTargetIsDirectory: toDir)
    }

    private func local(_ name: String, dir: Bool = false, size: UInt64 = 0, modified: Date? = nil) -> LocalEntry {
        LocalEntry(url: URL(fileURLWithPath: "/tmp/x/\(name)"), name: name, isDirectory: dir,
                   size: size, modified: modified, isHidden: false)
    }

    private func sortRemote(_ e: [SftpEntry], _ order: [KeyPathComparator<SftpEntry>]) -> [String] {
        FileSort.sorted(e, isDirectory: { $0.isDirectory }, by: order).map(\.name)
    }

    private func sortLocal(_ e: [LocalEntry], _ order: [KeyPathComparator<LocalEntry>]) -> [String] {
        FileSort.sorted(e, isDirectory: { $0.isDirectory }, by: order).map(\.name)
    }

    // MARK: - Rozmiar

    @Test func rozmiarRosnaco_katalogiNajpierw() {
        let e = [file("b", size: 300), dir("D2", size: 4096), file("a", size: 10), dir("D1", size: 64), file("c", size: 2000)]
        #expect(sortRemote(e, [KeyPathComparator(\.size)]) == ["D1", "D2", "a", "b", "c"])
    }

    @Test func rozmiarMalejaco_katalogiNadalNajpierw() {
        let e = [file("b", size: 300), dir("D2", size: 4096), file("a", size: 10), dir("D1", size: 64), file("c", size: 2000)]
        #expect(sortRemote(e, [KeyPathComparator(\.size, order: .reverse)]) == ["D2", "D1", "c", "b", "a"])
    }

    @Test func rozmiarBrakujacyTraktowanyJakoZero() {
        let e = [file("duzy", size: 5), file("brak"), file("jeden", size: 1)]
        #expect(sortRemote(e, [KeyPathComparator(\.size)]) == ["brak", "jeden", "duzy"])
    }

    // MARK: - Data modyfikacji

    @Test func klucz_modifiedSortKey_dlaNilToDistantPast() {
        #expect(file("x").modifiedSortKey == .distantPast)
        #expect(file("x", mtime: 1_700_000_000).modifiedSortKey == Date(timeIntervalSince1970: 1_700_000_000))
        #expect(local("x").modifiedSortKey == .distantPast)
        let d = Date(timeIntervalSince1970: 1_600_000_000)
        #expect(local("x", modified: d).modifiedSortKey == d)
    }

    @Test func dataRosnaco_nilNaPoczatku() {
        let e = [file("nowy", mtime: 2_000_000), file("bezDaty"), file("stary", mtime: 1_000), dir("Kat", mtime: 5)]
        #expect(sortRemote(e, [KeyPathComparator(\.modifiedSortKey)]) == ["Kat", "bezDaty", "stary", "nowy"])
    }

    @Test func dataMalejaco_nilNaKoncu_katalogiNajpierw() {
        let e = [file("nowy", mtime: 2_000_000), file("bezDaty"), file("stary", mtime: 1_000),
                 dir("KatStary", mtime: 5), dir("KatNowy", mtime: 9_999_999)]
        #expect(sortRemote(e, [KeyPathComparator(\.modifiedSortKey, order: .reverse)])
                == ["KatNowy", "KatStary", "nowy", "stary", "bezDaty"])
    }

    @Test func lokalne_dataRosnacoIMalejaco() {
        let t1 = Date(timeIntervalSince1970: 1_000), t2 = Date(timeIntervalSince1970: 2_000)
        let e = [local("b", modified: t2), local("brak"), local("a", modified: t1), local("Dir", dir: true, modified: t1)]
        #expect(sortLocal(e, [KeyPathComparator(\.modifiedSortKey)]) == ["Dir", "brak", "a", "b"])
        #expect(sortLocal(e, [KeyPathComparator(\.modifiedSortKey, order: .reverse)]) == ["Dir", "b", "a", "brak"])
    }

    // MARK: - Uprawnienia

    @Test func klucz_modeSortKey_dlaNilToZero() {
        let bez = SftpEntry(name: "x", path: "/r/x", attributes: SftpAttributes())
        #expect(bez.modeSortKey == 0)
        #expect(file("a", mode: 0o600).modeSortKey < file("b", mode: 0o644).modeSortKey)
    }

    @Test func uprawnieniaRosnacoIMalejaco() {
        let bez = SftpEntry(name: "bez", path: "/r/bez", attributes: SftpAttributes(size: 1))
        let e = [file("p755", mode: 0o755), bez, file("p600", mode: 0o600), file("p644", mode: 0o644)]
        #expect(sortRemote(e, [KeyPathComparator(\.modeSortKey)]) == ["bez", "p600", "p644", "p755"])
        #expect(sortRemote(e, [KeyPathComparator(\.modeSortKey, order: .reverse)]) == ["p755", "p644", "p600", "bez"])
    }

    // MARK: - Nazwa

    @Test func nazwaNaturalnie_bezWielkosciLiter() {
        let e = [file("file10"), file("File2"), file("file1"), dir("katalog10"), dir("Katalog9")]
        #expect(sortRemote(e, [KeyPathComparator(\.name, comparator: .localizedStandard)])
                == ["Katalog9", "katalog10", "file1", "File2", "file10"])
    }

    @Test func nazwaMalejaco_katalogiNadalNajpierw() {
        let e = [file("file10"), file("file2"), dir("A"), dir("B"), file("file1")]
        #expect(sortRemote(e, [KeyPathComparator(\.name, comparator: .localizedStandard, order: .reverse)])
                == ["B", "A", "file10", "file2", "file1"])
    }

    @Test func nazwaPolskieZnaki() {
        let e = [file("żaba.txt"), file("łódź.txt"), file("ala.txt"), file("Źródło.txt")]
        let out = sortRemote(e, [KeyPathComparator(\.name, comparator: .localizedStandard)])
        #expect(out.first == "ala.txt")
        #expect(Set(out) == Set(e.map(\.name)))
    }

    @Test func lokalne_nazwaNaturalnie() {
        let e = [local("plik10.txt", size: 3), local("plik2.txt"), local("zeta", dir: true), local("Alfa", dir: true)]
        #expect(sortLocal(e, [KeyPathComparator(\.name, comparator: .localizedStandard)])
                == ["Alfa", "zeta", "plik2.txt", "plik10.txt"])
    }

    // MARK: - Drugi komparator, puste order

    @Test func drugiKomparatorRozstrzygaRemis() {
        let e = [file("c", size: 10), file("a", size: 10), file("b", size: 5), dir("Y", size: 1), dir("X", size: 1)]
        let order = [KeyPathComparator<SftpEntry>(\.size), KeyPathComparator(\.name, comparator: .localizedStandard)]
        #expect(sortRemote(e, order) == ["X", "Y", "b", "a", "c"])
        let order2 = [KeyPathComparator<SftpEntry>(\.size, order: .reverse),
                      KeyPathComparator(\.name, comparator: .localizedStandard, order: .reverse)]
        #expect(sortRemote(e, order2) == ["Y", "X", "c", "a", "b"])
    }

    @Test func lokalne_drugiKomparator() {
        let t = Date(timeIntervalSince1970: 1_000)
        let e = [local("b", size: 1, modified: t), local("a", size: 1, modified: t), local("c", size: 0, modified: t)]
        let order = [KeyPathComparator<LocalEntry>(\.modifiedSortKey), KeyPathComparator(\.size),
                     KeyPathComparator(\.name, comparator: .localizedStandard)]
        #expect(sortLocal(e, order) == ["c", "a", "b"])
    }

    @Test func pusteOrder_katalogiNajpierw() {
        let e = [file("f1"), dir("D1"), file("f2"), link("L", toDir: true), dir("D2")]
        let out = FileSort.sorted(e, isDirectory: { $0.isDirectory }, by: [])
        #expect(out.count == e.count)
        #expect(out.prefix(3).allSatisfy { $0.isDirectory })
        #expect(out.suffix(2).allSatisfy { !$0.isDirectory })
        #expect(Set(out.map(\.name)) == Set(e.map(\.name)))
    }

    @Test func pustaLista() {
        #expect(FileSort.sorted([SftpEntry](), isDirectory: { $0.isDirectory },
                                by: [KeyPathComparator(\.size)]).isEmpty)
    }

    // MARK: - Dowiązania

    @Test func dowiazanieDoKataloguToKatalog() {
        let e = [file("a", size: 1), link("doKatalogu", toDir: true, size: 999), link("doPliku", toDir: false, size: 2), dir("Z", size: 0)]
        #expect(sortRemote(e, [KeyPathComparator(\.size)]) == ["Z", "doKatalogu", "a", "doPliku"])
        #expect(sortRemote(e, [KeyPathComparator(\.size, order: .reverse)]) == ["doKatalogu", "Z", "doPliku", "a"])
    }

    // MARK: - Duża lista

    @Test func duzaLista_zachowujeElementyIKolejnosc() {
        var e: [SftpEntry] = []
        for i in 0..<2000 { e.append(i % 5 == 0 ? dir("d\(i)", size: UInt64(i)) : file("f\(i)", size: UInt64(5000 - i))) }
        let out = FileSort.sorted(e.shuffled(), isDirectory: { $0.isDirectory }, by: [KeyPathComparator(\.size)])
        #expect(out.count == 2000)
        let firstFile = out.firstIndex { !$0.isDirectory }!
        #expect(firstFile == 400)
        #expect(out[..<firstFile].allSatisfy { $0.isDirectory })
        #expect(zip(out[..<firstFile], out[..<firstFile].dropFirst()).allSatisfy { $0.size <= $1.size })
        #expect(zip(out[firstFile...], out[firstFile...].dropFirst()).allSatisfy { $0.size <= $1.size })
    }
}
