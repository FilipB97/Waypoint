import Foundation
#if canImport(Darwin)
import Darwin
#elseif canImport(Glibc)
import Glibc
#endif

/// Jednorazowe polecenie przez `ssh` (bez terminala): stdin podany z góry, stdout/stderr zebrane.
/// posix_spawn zamiast Foundation.Process: na Linuksie Process potrafi zawiesić się na wait4()
/// innego, długo żyjącego procesu ssh (np. transportu SFTP tej samej karty), a tu oba działają naraz.
public enum SshExec {
    public struct Result: Sendable {
        public var status: Int32
        public var stdout: Data
        public var stderr: Data
        public var stdoutText: String { String(decoding: stdout, as: UTF8.self) }
        public var stderrText: String { String(decoding: stderr, as: UTF8.self) }
    }

    public static func run(executable: String, arguments: [String], environment: [String: String],
                           stdin input: Data = Data(), timeout: TimeInterval = 60) throws -> Result {
        signal(SIGPIPE, SIG_IGN)
        var inPipe: [Int32] = [0, 0], outPipe: [Int32] = [0, 0], errPipe: [Int32] = [0, 0]
        guard pipe(&inPipe) == 0, pipe(&outPipe) == 0, pipe(&errPipe) == 0 else { throw POSIXError(.EMFILE) }

        #if canImport(Darwin)
        var actions: posix_spawn_file_actions_t?
        #else
        var actions = posix_spawn_file_actions_t()
        #endif
        posix_spawn_file_actions_init(&actions)
        defer { posix_spawn_file_actions_destroy(&actions) }
        posix_spawn_file_actions_adddup2(&actions, inPipe[0], 0)
        posix_spawn_file_actions_adddup2(&actions, outPipe[1], 1)
        posix_spawn_file_actions_adddup2(&actions, errPipe[1], 2)
        for fd in [inPipe[0], inPipe[1], outPipe[0], outPipe[1], errPipe[0], errPipe[1]] {
            posix_spawn_file_actions_addclose(&actions, fd)
        }

        let argv = ([executable] + arguments).map { strdup($0) } + [nil]
        let envp = environment.map { strdup("\($0.key)=\($0.value)") } + [nil]
        defer { argv.forEach { free($0) }; envp.forEach { free($0) } }

        var pid: pid_t = 0
        let rc = posix_spawn(&pid, executable, &actions, nil, argv, envp)
        close(inPipe[0]); close(outPipe[1]); close(errPipe[1])
        guard rc == 0 else {
            close(inPipe[1]); close(outPipe[0]); close(errPipe[0])
            throw POSIXError(POSIXErrorCode(rawValue: rc) ?? .ENOENT)
        }

        // stdin w osobnym wątku (mały: hasło + nic), odczyt obu strumieni przez poll — bez zakleszczeń.
        let stdinFD = inPipe[1]
        let writer = Thread {
            input.withUnsafeBytes { raw in
                var off = 0
                while off < raw.count {
                    let n = write(stdinFD, raw.baseAddress! + off, raw.count - off)
                    if n <= 0 { break }
                    off += n
                }
            }
            close(stdinFD)
        }
        writer.start()

        var out = Data(), err = Data()
        var open = [outPipe[0], errPipe[0]]
        let deadline = Date().addingTimeInterval(timeout)
        var buf = [UInt8](repeating: 0, count: 16 * 1024)
        while !open.isEmpty {
            if Date() > deadline { kill(pid, SIGTERM); break }
            var fds = open.map { pollfd(fd: $0, events: Int16(POLLIN), revents: 0) }
            let r = poll(&fds, nfds_t(fds.count), 250)
            if r < 0 && errno != EINTR { break }
            for f in fds where f.revents != 0 {
                let n = read(f.fd, &buf, buf.count)
                if n > 0 {
                    if f.fd == outPipe[0] { out.append(contentsOf: buf[0..<n]) } else { err.append(contentsOf: buf[0..<n]) }
                } else {
                    open.removeAll { $0 == f.fd }
                }
            }
        }
        close(outPipe[0]); close(errPipe[0])
        var status: Int32 = 0
        while waitpid(pid, &status, 0) < 0 && errno == EINTR {}
        let exitCode: Int32 = (status & 0x7f) == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f)
        return Result(status: exitCode, stdout: out, stderr: err)
    }
}
