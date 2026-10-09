import CurlShim
import Foundation

/// Szyfrowanie FTP — te same wartości co `FtpEncryption` w wersji Windows.
public enum FtpEncryption: Int, Sendable {
    case explicitTLS = 0   // FTP + AUTH TLS (FTPS jawne) — zalecane
    case implicitTLS = 1   // FTPS niejawne (zwykle port 990)
    case none = 2          // zwykły FTP — hasło idzie jawnym tekstem
    case auto = 3          // FTPS, jeśli serwer umie, inaczej zwykły FTP (jak „Auto" w Windows / FileZilla)
}

/// Klient FTP/FTPS na libcurl (systemowa biblioteka macOS). libcurl, a nie własna implementacja, bo FTPS
/// wymaga rzeczy, których Network.framework nie daje: przełączenia gotowego połączenia na TLS (AUTH TLS)
/// i wznowienia sesji TLS na kanale danych (vsftpd domyślnie odrzuca kanał danych bez niego).
///
/// Jeden uchwyt curl na całe życie klienta — połączenie sterujące jest utrzymywane między operacjami
/// (bez ponownego logowania przy każdym kliknięciu w folder). Metody blokujące, jedna naraz.
public final class FtpClient: RemoteFS, @unchecked Sendable {
    public let host: String
    public let port: Int
    public let encryption: FtpEncryption
    /// Zgoda na certyfikat, którego nie da się zweryfikować (samopodpisany). Ustawiana świadomie
    /// przez użytkownika dla konkretnego serwera; domyślnie weryfikacja jest pełna.
    public let acceptInvalidCertificate: Bool
    private let user: String
    private let password: String
    private var handle: UnsafeMutableRawPointer?
    private let lock = NSLock()
    public private(set) var homeDirectory = "/"

    public init(host: String, port: Int, user: String, password: String, encryption: FtpEncryption,
                acceptInvalidCertificate: Bool = false) throws {
        Self.globalInit
        self.host = host
        self.port = port
        self.user = user
        self.password = password
        self.encryption = encryption
        self.acceptInvalidCertificate = acceptInvalidCertificate
        handle = curl_easy_init()
        guard handle != nil else { throw SftpError.disconnected("libcurl") }
        // Logowanie + katalog startowy (PWD po zalogowaniu).
        try perform(url: url(forDirectory: "/"), configure: { h in _ = wp_set_long(h, CURLOPT_NOBODY, 1) })
        var entry: UnsafePointer<CChar>?
        if wp_get_str(handle, CURLINFO_FTP_ENTRY_PATH, &entry) == CURLE_OK, let entry {
            let p = Self.string(entry)
            homeDirectory = p.hasPrefix("/") ? p : "/" + p
        }
    }

    private static let globalInit: Void = { curl_global_init(Int(CURL_GLOBAL_DEFAULT)) }()

    deinit { close() }

    public func close() {
        lock.lock(); defer { lock.unlock() }
        if let h = handle { curl_easy_cleanup(h) }
        handle = nil
    }

    // MARK: Adresy

    /// Ścieżki zawsze bezwzględne: „%2F" na początku każe curl zacząć od korzenia, a nie od katalogu
    /// domowego (ftp://host/etc to dla curl „etc w katalogu domowym").
    func url(forFile path: String) -> String { FtpPaths.file(base: base, path) }
    func url(forDirectory path: String) -> String { FtpPaths.dir(base: base, path) }

    private var base: String {
        let scheme = encryption == .implicitTLS ? "ftps" : "ftp"
        let h = host.contains(":") && !host.hasPrefix("[") ? "[\(host)]" : host
        return "\(scheme)://\(h):\(port)"
    }


    // MARK: Wykonanie

    private final class Box {
        var out = Data()
        var sink: ((Data) throws -> Void)?
        var sinkError: Error?
        var source: ((Int) -> Data)?
        var progress: ((UInt64) -> Bool)?
        var cancelled = false
    }

    /// Wykonuje jedną operację na wspólnym uchwycie. Opcje są zerowane (curl_easy_reset zachowuje
    /// otwarte połączenie), potem ustawiane wspólne (logowanie, TLS, limity czasu) i te z `configure`.
    @discardableResult
    private func perform(url: String, quote: [String] = [], configure: (UnsafeMutableRawPointer) -> Void = { _ in },
                         box: Box = Box()) throws -> Box {
        lock.lock(); defer { lock.unlock() }
        guard let h = handle else { throw SftpError.disconnected("") }
        curl_easy_reset(h)
        _ = wp_set_str(h, CURLOPT_URL, url)
        _ = wp_set_str(h, CURLOPT_USERNAME, user)
        _ = wp_set_str(h, CURLOPT_PASSWORD, password)
        _ = wp_set_long(h, CURLOPT_CONNECTTIMEOUT, 15)
        _ = wp_set_long(h, CURLOPT_SERVER_RESPONSE_TIMEOUT, 30)
        _ = wp_set_long(h, CURLOPT_FTP_FILEMETHOD, Int(CURLFTPMETHOD_NOCWD.rawValue))
        _ = wp_set_long(h, CURLOPT_FTP_USE_EPSV, 1)
        _ = wp_set_long(h, CURLOPT_NOSIGNAL, 1)
        switch encryption {
        case .explicitTLS: _ = wp_set_long(h, CURLOPT_USE_SSL, Int(CURLUSESSL_ALL.rawValue))
        case .implicitTLS: break   // ftps:// — TLS od pierwszego bajtu
        case .none: _ = wp_set_long(h, CURLOPT_USE_SSL, Int(CURLUSESSL_NONE.rawValue))
        case .auto: _ = wp_set_long(h, CURLOPT_USE_SSL, Int(CURLUSESSL_TRY.rawValue))
        }
        if encryption != .none {
            // TLS 1.2: serwery FTPS wymagające wznowienia sesji TLS na kanale danych (vsftpd, proftpd)
            // z TLS 1.3 losowo odrzucają transfer (426/522 — bilet sesji przychodzi po uzgodnieniu).
            // Sprawdzone na vsftpd: z 1.3 co kilka operacji błąd, z 1.2 stabilnie.
            _ = wp_set_long(h, CURLOPT_SSLVERSION, Int(CURL_SSLVERSION_TLSv1_2) | Int(CURL_SSLVERSION_MAX_TLSv1_2))
        }
        if acceptInvalidCertificate {
            _ = wp_set_long(h, CURLOPT_SSL_VERIFYPEER, 0)
            _ = wp_set_long(h, CURLOPT_SSL_VERIFYHOST, 0)
        }
        var errbuf = [CChar](repeating: 0, count: Int(CURL_ERROR_SIZE) + 1)
        var slist: UnsafeMutablePointer<curl_slist>?
        defer { if let slist { curl_slist_free_all(slist) } }
        for q in quote { slist = curl_slist_append(slist, q) }
        if slist != nil { _ = wp_set_list(h, CURLOPT_QUOTE, slist) }

        let unmanaged = Unmanaged.passRetained(box)
        defer { unmanaged.release() }
        let ctx = unmanaged.toOpaque()
        _ = wp_set_write(h, { ptr, size, n, ud in
            let b = Unmanaged<Box>.fromOpaque(ud!).takeUnretainedValue()
            let count = size * n
            let d = Data(bytes: ptr!, count: count)
            if let sink = b.sink {
                do { try sink(d) } catch { b.sinkError = error; return 0 }
            } else {
                b.out.append(d)
            }
            return count
        }, ctx)
        if box.source != nil {
            _ = wp_set_read(h, { ptr, size, n, ud in
                let b = Unmanaged<Box>.fromOpaque(ud!).takeUnretainedValue()
                let chunk = b.source!(size * n)
                chunk.withUnsafeBytes { raw in if !raw.isEmpty { memcpy(ptr!, raw.baseAddress!, raw.count) } }
                return chunk.count
            }, ctx)
        }
        if box.progress != nil {
            _ = wp_set_progress(h, { ud, _, dlnow, _, ulnow in
                let b = Unmanaged<Box>.fromOpaque(ud!).takeUnretainedValue()
                let done = UInt64(max(dlnow, ulnow))
                if done > 0, let p = b.progress, !p(done) { b.cancelled = true; return 1 }
                return 0
            }, ctx)
        }
        errbuf.withUnsafeMutableBufferPointer { _ = wp_set_ptr(h, CURLOPT_ERRORBUFFER, $0.baseAddress) }
        configure(h)

        let rc = curl_easy_perform(h)
        _ = wp_set_ptr(h, CURLOPT_ERRORBUFFER, nil)
        if rc == CURLE_OK { return box }
        if box.cancelled { throw SftpError.cancelled }
        if let e = box.sinkError { throw e }
        var code: Int = 0
        _ = wp_get_long(h, CURLINFO_RESPONSE_CODE, &code)
        let detail = errbuf.withUnsafeBufferPointer { Self.string($0.baseAddress!) }.trimmingCharacters(in: .whitespacesAndNewlines)
        throw Self.map(rc, ftpCode: code, detail: detail.isEmpty ? Self.string(curl_easy_strerror(rc)) : detail)
    }

    static func string(_ p: UnsafePointer<CChar>) -> String {
        String(decoding: UnsafeRawBufferPointer(start: p, count: strlen(p)), as: UTF8.self)
    }

    static func map(_ rc: CURLcode, ftpCode: Int, detail: String) -> SftpError {
        switch rc {
        case CURLE_LOGIN_DENIED: return .authenticationFailed(detail)
        case CURLE_PEER_FAILED_VERIFICATION, CURLE_SSL_CACERT_BADFILE: return .certificateUntrusted(detail)
        case CURLE_REMOTE_ACCESS_DENIED where ftpCode == 550, CURLE_REMOTE_FILE_NOT_FOUND:
            return .status(.noSuchFile, detail)
        case CURLE_REMOTE_ACCESS_DENIED: return .status(.permissionDenied, detail)
        case CURLE_UPLOAD_FAILED, CURLE_QUOTE_ERROR:
            // 550 bywa i „brak pliku", i „brak uprawnień" — treść odpowiedzi serwera mówi więcej.
            let d = detail.lowercased()
            if d.contains("permission") || d.contains("denied") { return .status(.permissionDenied, detail) }
            if ftpCode == 550 && (d.contains("no such") || d.contains("not found")) { return .status(.noSuchFile, detail) }
            return .status(.failure, detail)
        case CURLE_COULDNT_CONNECT, CURLE_COULDNT_RESOLVE_HOST, CURLE_OPERATION_TIMEDOUT, CURLE_SEND_ERROR,
             CURLE_RECV_ERROR, CURLE_GOT_NOTHING, CURLE_SSL_CONNECT_ERROR, CURLE_USE_SSL_FAILED:
            return .disconnected(detail)
        default: return .status(.failure, detail)
        }
    }

    // MARK: RemoteFS

    public func realPath(_ path: String) throws -> String {
        if path == "." || path.isEmpty { return homeDirectory }
        let abs = path.hasPrefix("/") ? path : RemotePath.join(homeDirectory, path)
        // Normalizacja „..": FTP nie ma realpath, a pasek ścieżki potrzebuje prawdziwego katalogu.
        var parts: [Substring] = []
        for p in abs.split(separator: "/") {
            if p == "." { continue }
            if p == ".." { _ = parts.popLast(); continue }
            parts.append(p)
        }
        return "/" + parts.joined(separator: "/")
    }

    public func list(_ dir: String) throws -> [SftpEntry] {
        let b = try perform(url: url(forDirectory: dir))
        let text = String(data: b.out, encoding: .utf8) ?? String(decoding: b.out, as: UTF8.self)
        return FtpListing.parse(text).filter { $0.name != "." && $0.name != ".." }.map { item in
            var e = SftpEntry(name: item.name, path: RemotePath.join(dir, item.name), attributes: item.attributes)
            e.linkTargetIsDirectory = item.attributes.isSymlink && item.linkLooksLikeDirectory
            return e
        }
    }

    /// Rozmiar i czas pliku (SIZE + MDTM); katalog rozpoznajemy po tym, że da się do niego wejść.
    public func stat(_ path: String) throws -> SftpAttributes {
        do {
            try perform(url: url(forFile: path), configure: { h in
                _ = wp_set_long(h, CURLOPT_NOBODY, 1)
                _ = wp_set_long(h, CURLOPT_FILETIME, 1)
            })
            var size: curl_off_t = -1, time: curl_off_t = -1
            _ = wp_get_off(handle, CURLINFO_CONTENT_LENGTH_DOWNLOAD_T, &size)
            _ = wp_get_off(handle, CURLINFO_FILETIME_T, &time)
            return SftpAttributes(size: size >= 0 ? UInt64(size) : 0, permissions: 0o100000,
                                  accessTime: time >= 0 ? UInt32(time) : nil, modifyTime: time >= 0 ? UInt32(time) : nil)
        } catch let e as SftpError where e.code == .noSuchFile || e.code == .failure {
            // Może to katalog: jawne CWD (sam adres katalogu w trybie NOCWD niczego nie sprawdza).
            do { try command(["CWD \(path)"]) }
            catch { throw SftpError.status(.noSuchFile, (error as? SftpError)?.description ?? "") }
            return SftpAttributes(size: 0, permissions: 0o040000)
        }
    }

    public func lstat(_ path: String) throws -> SftpAttributes { try stat(path) }

    private func command(_ cmds: [String]) throws {
        try perform(url: url(forDirectory: "/"), quote: cmds, configure: { h in _ = wp_set_long(h, CURLOPT_NOBODY, 1) })
    }

    public func mkdir(_ path: String) throws { try command(["MKD \(path)"]) }
    public func rmdir(_ path: String) throws { try command(["RMD \(path)"]) }
    public func remove(_ path: String) throws { try command(["DELE \(path)"]) }

    /// FTP nie ma „podmień, jeśli istnieje" — przy `overwrite` najpierw usuwamy cel.
    public func rename(_ from: String, to: String, overwrite: Bool) throws {
        if overwrite { try? remove(to) }
        try command(["RNFR \(from)", "RNTO \(to)"])
    }

    public func download(_ path: String, to local: URL, progress: (UInt64) -> Bool) throws {
        _ = FileManager.default.createFile(atPath: local.path, contents: nil)
        let fh = try FileHandle(forWritingTo: local)
        let b = Box()
        b.sink = { try fh.write(contentsOf: $0) }
        do {
            try withoutActuallyEscaping(progress) { p in
                b.progress = { p($0) }
                defer { b.progress = nil }
                try perform(url: url(forFile: path), box: b)
            }
            try fh.close()
        } catch {
            try? fh.close()
            try? FileManager.default.removeItem(at: local)   // nie zostawiamy połowy pliku
            throw error
        }
    }

    public func upload(from local: URL, to path: String, overwrite: Bool, progress: (UInt64) -> Bool) throws {
        if !overwrite, (try? stat(path)) != nil { throw SftpError.status(.failure, "plik już istnieje: \(RemotePath.name(path))") }
        let fh = try FileHandle(forReadingFrom: local)
        defer { try? fh.close() }
        let size = (try? local.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0
        let b = Box()
        b.source = { max in (try? fh.read(upToCount: max)) ?? Data() }
        do {
            try withoutActuallyEscaping(progress) { p in
                b.progress = { p($0) }
                defer { b.progress = nil }
                try perform(url: url(forFile: path), configure: { h in
                    _ = wp_set_long(h, CURLOPT_UPLOAD, 1)
                    _ = wp_set_off(h, CURLOPT_INFILESIZE_LARGE, curl_off_t(size))
                }, box: b)
            }
        } catch SftpError.cancelled {
            try? remove(path)   // przerwany transfer — bez połowy pliku
            throw SftpError.cancelled
        }
    }

    public func readFile(_ path: String, limit: Int) throws -> Data {
        let b = Box()
        var out = Data()
        b.sink = { d in
            guard out.count + d.count <= limit else { throw SftpError.status(.failure, "plik większy niż \(limit) B") }
            out.append(d)
        }
        try perform(url: url(forFile: path), box: b)
        return out
    }

    public func writeFile(_ path: String, data: Data) throws {
        var offset = 0
        let b = Box()
        b.source = { max in
            guard offset < data.count else { return Data() }
            let chunk = data.subdata(in: offset..<min(data.count, offset + max))
            offset += chunk.count
            return chunk
        }
        try perform(url: url(forFile: path), configure: { h in
            _ = wp_set_long(h, CURLOPT_UPLOAD, 1)
            _ = wp_set_off(h, CURLOPT_INFILESIZE_LARGE, curl_off_t(data.count))
        }, box: b)
    }
}

/// Parser odpowiedzi LIST (FTP nie ma ustandaryzowanego formatu listy; MLSD obsługuje tylko część serwerów,
/// np. vsftpd go nie ma). Dwa formaty spotykane w praktyce: uniksowy `ls -l` i DOS/IIS.
public enum FtpListing {
    public struct Item: Equatable, Sendable {
        public var name: String
        public var attributes: SftpAttributes
        /// Dowiązanie: cel kończy się „/" albo nie ma kropki — zgadujemy katalog (sprawdzi to wejście).
        public var linkLooksLikeDirectory = false
    }

    public static func parse(_ text: String, now: Date = Date()) -> [Item] {
        text.split(whereSeparator: \.isNewline).compactMap { parseLine(String($0), now: now) }
    }

    static let months = ["jan": 1, "feb": 2, "mar": 3, "apr": 4, "may": 5, "jun": 6,
                         "jul": 7, "aug": 8, "sep": 9, "oct": 10, "nov": 11, "dec": 12]

    static func parseLine(_ line: String, now: Date) -> Item? {
        if line.hasPrefix("total ") { return nil }
        if let first = line.first, "-dlbcps".contains(first) { return parseUnix(line, now: now) }
        return parseDOS(line)
    }

    /// `drwxr-xr-x 2 wpt wpt 4096 Oct  9 14:27 nazwa z spacjami` / `... Mar 31  2024 plik`
    static func parseUnix(_ line: String, now: Date) -> Item? {
        let f = line.split(separator: " ", omittingEmptySubsequences: true)
        guard f.count >= 9 else { return nil }
        let perm = String(f[0])
        guard perm.count >= 10, let size = UInt64(f[4]) else { return nil }
        var mode: UInt32
        switch perm.first! {
        case "d": mode = 0o040000
        case "l": mode = 0o120000
        default: mode = 0o100000
        }
        let bits = Array(perm.dropFirst().prefix(9))
        let values: [UInt32] = [0o400, 0o200, 0o100, 0o040, 0o020, 0o010, 0o004, 0o002, 0o001]
        for (i, c) in bits.enumerated() where c != "-" && c != "S" && c != "T" { mode |= values[i] }
        if bits.count == 9 {
            if bits[2] == "s" || bits[2] == "S" { mode |= 0o4000 }
            if bits[5] == "s" || bits[5] == "S" { mode |= 0o2000 }
            if bits[8] == "t" || bits[8] == "T" { mode |= 0o1000 }
        }
        // Nazwa = wszystko po 8. polu, z zachowaniem spacji wewnątrz nazwy.
        guard let nameRange = rangeAfterFields(line, count: 8) else { return nil }
        var name = String(line[nameRange])
        var linkDir = false
        if mode & 0o170000 == 0o120000, let arrow = name.range(of: " -> ") {
            let target = String(name[arrow.upperBound...])
            name = String(name[..<arrow.lowerBound])
            linkDir = target.hasSuffix("/") || !target.contains(".")
        }
        let time = unixTime(month: String(f[5]), day: String(f[6]), yearOrTime: String(f[7]), now: now)
        return Item(name: name, attributes: SftpAttributes(size: size, permissions: mode, accessTime: time, modifyTime: time),
                    linkLooksLikeDirectory: linkDir)
    }

    private static func rangeAfterFields(_ line: String, count: Int) -> Range<String.Index>? {
        var i = line.startIndex
        for _ in 0..<count {
            while i < line.endIndex && line[i] == " " { i = line.index(after: i) }
            while i < line.endIndex && line[i] != " " { i = line.index(after: i) }
        }
        guard i < line.endIndex else { return nil }
        let start = line.index(after: i)   // dokładnie jedna spacja oddziela czas od nazwy
        return start < line.endIndex ? start..<line.endIndex : nil
    }

    private static func unixTime(month: String, day: String, yearOrTime: String, now: Date) -> UInt32? {
        guard let m = months[month.lowercased().prefix(3).description], let d = Int(day) else { return nil }
        var cal = Calendar(identifier: .gregorian)
        cal.timeZone = TimeZone(identifier: "UTC")!
        var c = DateComponents(month: m, day: d)
        if yearOrTime.contains(":") {
            let hm = yearOrTime.split(separator: ":")
            c.hour = Int(hm[0]); c.minute = Int(hm.count > 1 ? hm[1] : "0")
            // Bez roku = ostatnie 12 miesięcy (konwencja ls): data z przyszłości → rok wcześniej.
            let year = cal.component(.year, from: now)
            c.year = year
            if let date = cal.date(from: c), date > now.addingTimeInterval(86400) { c.year = year - 1 }
        } else {
            c.year = Int(yearOrTime)
        }
        return cal.date(from: c).map { UInt32($0.timeIntervalSince1970) }
    }

    /// `10-09-26  02:58PM       <DIR>          folder` / `10-09-26  02:58PM              1234 plik.txt`
    static func parseDOS(_ line: String) -> Item? {
        let f = line.split(separator: " ", omittingEmptySubsequences: true)
        guard f.count >= 4, f[0].contains("-") || f[0].contains("/") else { return nil }
        guard let nameRange = rangeAfterFields(line, count: 3) else { return nil }
        let name = String(line[nameRange]).trimmingCharacters(in: .whitespaces)
        let isDir = f[2].uppercased() == "<DIR>"
        let size = isDir ? 0 : (UInt64(f[2]) ?? 0)
        return Item(name: name, attributes: SftpAttributes(size: size, permissions: isDir ? 0o040000 : 0o100000))
    }
}

/// Adresy curl dla ścieżek FTP.
enum FtpPaths {
    static func encode(_ path: String) -> String {
        let allowed = CharacterSet.urlPathAllowed.subtracting(CharacterSet(charactersIn: ";?#%"))
        return path.split(separator: "/").map { String($0).addingPercentEncoding(withAllowedCharacters: allowed) ?? String($0) }
            .joined(separator: "/")
    }
    static func file(base: String, _ path: String) -> String { base + "/%2F" + encode(path) }
    static func dir(base: String, _ path: String) -> String {
        let e = encode(path)
        return base + "/%2F" + e + (e.isEmpty ? "" : "/")
    }
}
