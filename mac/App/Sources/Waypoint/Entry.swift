import AppKit
import Foundation
import WaypointCore

/// Punkt wejścia. Ten sam plik wykonywalny pełni dwie role:
///  * zwykle — aplikacja z oknem;
///  * gdy uruchomi go `ssh` jako `SSH_ASKPASS` (w środowisku jest gniazdo sesji) — krótki proces,
///    który przekazuje pytanie ssh do okna i wypisuje odpowiedź. Bez interfejsu, bez ikony w Docku;
///  * z `--waypoint-helper telnet|serial …` — klient Telnet / portu szeregowego w terminalu karty.
@main
enum Entry {
    static func main() {
        let env = ProcessInfo.processInfo.environment
        // Telnet / port szeregowy: ten plik uruchomiony w pseudo-terminalu karty (StreamHelper).
        if CommandLine.arguments.count > 1, CommandLine.arguments[1] == StreamHelper.flag {
            exit(StreamHelper.main(Array(CommandLine.arguments.dropFirst())))
        }
        if env[Askpass.socketEnv] != nil {
            setvbuf(stdout, nil, _IONBF, 0)
            exit(Askpass.runClient(arguments: CommandLine.arguments, environment: env))
        }
        WaypointApp.main()
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        MainActor.assumeIsolated { EditorWindowController.confirmQuit() && AppModel.shared.confirmQuit() }
            ? .terminateNow : .terminateCancel
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }

    func applicationDidFinishLaunching(_ notification: Notification) {
        MainActor.assumeIsolated {
            AppModel.shared.installKeyMonitor()
            Smoke.startIfRequested()
        }
    }
}
