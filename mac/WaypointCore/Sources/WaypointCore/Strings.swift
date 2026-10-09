import Foundation

/// Teksty interfejsu (pl/en) — odpowiednik Strings.pl.xaml / Strings.en.xaml z wersji Windows.
///
/// Celowo w kodzie, a nie w zasobach pakietu: zasoby SwiftPM w ręcznie składanej paczce .app trzeba
/// by kłaść w katalogu głównym .app, co psuje podpis. Przy okazji test parzystości (pl ↔ en) działa
/// zwykłym `swift test`, także na Linuksie.
public enum Strings {
    public static let pl: [String: String] = [
        "menu.newserver": "Nowy serwer…",
        "menu.import": "Importuj z Waypoint dla Windows…",

        "list.search": "Szukaj serwera",
        "list.pinned": "Przypięte",
        "list.ungrouped": "Bez grupy",
        "list.copyname": "%@ (kopia)",

        "act.connect": "Połącz",
        "act.edit": "Edytuj…",
        "act.duplicate": "Duplikuj",
        "act.pin": "Przypnij",
        "act.unpin": "Odepnij",
        "act.copyhost": "Kopiuj adres",
        "act.delete": "Usuń…",

        "del.title": "Usunąć serwer?",
        "del.msg": "„%@” zniknie z listy. Tego nie da się cofnąć.",
        "del.confirm": "Usuń",

        "empty.title": "Brak serwerów",
        "empty.desc": "Dodaj pierwszy serwer albo zaimportuj listę z Waypointa dla Windows (Ustawienia → Eksportuj profil…).",
        "detail.none": "Wybierz serwer z listy",
        "detail.unsupported": "Ten rodzaj połączenia działa na razie tylko w wersji Windows. Wpis zostaje na liście, żeby import niczego nie gubił.",

        "f.name": "Nazwa",
        "f.name.ph": "np. Produkcja — web",
        "f.protocol": "Protokół",
        "f.protocol.win": "tylko Windows",
        "f.host": "Adres",
        "f.port": "Port",
        "f.user": "Użytkownik",
        "f.domain": "Domena",
        "f.key": "Klucz prywatny",
        "f.group": "Grupa",
        "f.group.ph": "np. Produkcja",
        "f.group.pick": "Wybierz istniejącą grupę",
        "f.tags": "Tagi",
        "f.tags.ph": "oddzielone przecinkami",
        "f.pinned": "Przypięty na górze listy",
        "f.tunnels": "Tunele",
        "f.tunnels.hint": "Jak ssh -L: portLokalny:host:portZdalny, po jednym w wierszu.",
        "f.notes": "Notatki",
        "f.ftpenc": "Szyfrowanie",
        "f.ftpenc.explicit": "Jawne FTPS (zalecane)",
        "f.ftpenc.implicit": "Niejawne FTPS",
        "f.ftpenc.none": "Brak (zwykły FTP)",
        "f.ftpanon": "Logowanie anonimowe",

        "edit.sec.login": "Logowanie",
        "edit.sec.org": "Porządek",
        "edit.choose": "Wybierz…",
        "edit.err.host": "Podaj adres serwera.",
        "edit.err.hostspace": "Adres nie może zawierać spacji.",
        "edit.err.port": "Port musi być liczbą od 1 do 65535.",

        "btn.cancel": "Anuluj",
        "btn.add": "Dodaj",
        "btn.save": "Zapisz",

        "import.title": "Import serwerów",
        "import.message": "Wybierz plik profilu wyeksportowany z Waypointa dla Windows albo servers.json.",
        "import.done.title": "Zaimportowano",
        "import.done.msg": "Nowe serwery: %ld, zaktualizowane: %ld. Hasła nie są przenoszone — podasz je przy pierwszym połączeniu.",
        "import.err.empty": "Plik nie zawiera żadnych serwerów.",
        "import.err.format": "To nie jest plik profilu Waypointa ani lista serwerów.",

        "alert.corrupt.title": "Nie udało się odczytać listy serwerów",
        "alert.corrupt.msg": "Uszkodzony plik został odłożony jako:\n%@\nLista została odtworzona z kopii zapasowej (jeśli była).",
        "alert.save.title": "Nie udało się zapisać listy serwerów",

        "connect.notyet": "Łączenie tym protokołem pojawi się w jednym z kolejnych kroków.",

        "ssh.warn.winkey": "Ścieżka klucza pochodzi z Windows i nie istnieje na tym Macu — ssh spróbuje kluczy z agenta i domyślnych. Popraw ją w edycji serwera.",
        "ssh.warn.nokey": "Nie znaleziono pliku klucza wskazanego w serwerze — ssh spróbuje kluczy z agenta i domyślnych.",
        "ssh.warn.tunnel": "Pominięto tunel o niepoprawnym formacie (oczekiwane portLokalny:host:portZdalny).",

        "tab.close": "Zamknij kartę",
        "tab.reconnect": "Połącz ponownie",
        "close.title": "Zamknąć „%@”?",
        "close.msg": "Połączenie jest aktywne — zostanie zakończone.",
        "close.confirm": "Zamknij",
        "quit.title": "Zakończyć Waypoint?",
        "quit.msg": "Aktywne połączenia: %ld. Wszystkie zostaną zakończone.",
        "quit.confirm": "Zakończ",
        "ended.clean": "Połączenie zakończone",
        "ended.code": "Połączenie przerwane (kod %@)",

        "prompt.password": "Hasło do %@",
        "prompt.passphrase": "Hasło klucza prywatnego",
        "prompt.hostkey": "Nieznany klucz serwera",
        "prompt.other": "%@ pyta",
        "prompt.retry": "Poprzednie hasło zostało odrzucone.",
        "prompt.save": "Zapisz w Pęku kluczy",
        "prompt.login": "Zaloguj",
        "prompt.yes": "Ufaj i połącz",
        "prompt.no": "Przerwij",
        "prompt.saved": "Hasło zapisane w Pęku kluczy",
        "prompt.savefail": "Nie udało się zapisać hasła w Pęku kluczy",

        "detail.password": "Hasło",
        "detail.password.saved": "zapisane w Pęku kluczy",
        "detail.password.forget": "Zapomnij",
    ]

    public static let en: [String: String] = [
        "menu.newserver": "New Server…",
        "menu.import": "Import from Waypoint for Windows…",

        "list.search": "Search servers",
        "list.pinned": "Pinned",
        "list.ungrouped": "No group",
        "list.copyname": "%@ (copy)",

        "act.connect": "Connect",
        "act.edit": "Edit…",
        "act.duplicate": "Duplicate",
        "act.pin": "Pin",
        "act.unpin": "Unpin",
        "act.copyhost": "Copy Address",
        "act.delete": "Delete…",

        "del.title": "Delete server?",
        "del.msg": "“%@” will be removed from the list. This cannot be undone.",
        "del.confirm": "Delete",

        "empty.title": "No servers",
        "empty.desc": "Add your first server or import the list from Waypoint for Windows (Settings → Export profile…).",
        "detail.none": "Select a server",
        "detail.unsupported": "This connection type currently works only in the Windows version. The entry stays on the list so importing loses nothing.",

        "f.name": "Name",
        "f.name.ph": "e.g. Production — web",
        "f.protocol": "Protocol",
        "f.protocol.win": "Windows only",
        "f.host": "Address",
        "f.port": "Port",
        "f.user": "User",
        "f.domain": "Domain",
        "f.key": "Private key",
        "f.group": "Group",
        "f.group.ph": "e.g. Production",
        "f.group.pick": "Choose an existing group",
        "f.tags": "Tags",
        "f.tags.ph": "comma-separated",
        "f.pinned": "Pinned to the top of the list",
        "f.tunnels": "Tunnels",
        "f.tunnels.hint": "Like ssh -L: localPort:host:remotePort, one per line.",
        "f.notes": "Notes",
        "f.ftpenc": "Encryption",
        "f.ftpenc.explicit": "Explicit FTPS (recommended)",
        "f.ftpenc.implicit": "Implicit FTPS",
        "f.ftpenc.none": "None (plain FTP)",
        "f.ftpanon": "Anonymous login",

        "edit.sec.login": "Login",
        "edit.sec.org": "Organization",
        "edit.choose": "Choose…",
        "edit.err.host": "Enter the server address.",
        "edit.err.hostspace": "The address cannot contain spaces.",
        "edit.err.port": "Port must be a number from 1 to 65535.",

        "btn.cancel": "Cancel",
        "btn.add": "Add",
        "btn.save": "Save",

        "import.title": "Import servers",
        "import.message": "Choose a profile file exported from Waypoint for Windows, or servers.json.",
        "import.done.title": "Imported",
        "import.done.msg": "New servers: %ld, updated: %ld. Passwords are not transferred — you will enter them on first connect.",
        "import.err.empty": "The file contains no servers.",
        "import.err.format": "This is not a Waypoint profile or server list.",

        "alert.corrupt.title": "Could not read the server list",
        "alert.corrupt.msg": "The damaged file was moved aside as:\n%@\nThe list was restored from the backup (if there was one).",
        "alert.save.title": "Could not save the server list",

        "connect.notyet": "Connecting with this protocol is coming in one of the next steps.",

        "ssh.warn.winkey": "The key path comes from Windows and does not exist on this Mac — ssh will try agent and default keys. Fix it in the server settings.",
        "ssh.warn.nokey": "The key file set for this server was not found — ssh will try agent and default keys.",
        "ssh.warn.tunnel": "Skipped a tunnel with an invalid format (expected localPort:host:remotePort).",

        "tab.close": "Close Tab",
        "tab.reconnect": "Reconnect",
        "close.title": "Close “%@”?",
        "close.msg": "The connection is active and will be ended.",
        "close.confirm": "Close",
        "quit.title": "Quit Waypoint?",
        "quit.msg": "Active connections: %ld. All of them will be ended.",
        "quit.confirm": "Quit",
        "ended.clean": "Connection closed",
        "ended.code": "Connection lost (code %@)",

        "prompt.password": "Password for %@",
        "prompt.passphrase": "Private key passphrase",
        "prompt.hostkey": "Unknown server key",
        "prompt.other": "%@ asks",
        "prompt.retry": "The previous password was rejected.",
        "prompt.save": "Save in Keychain",
        "prompt.login": "Log In",
        "prompt.yes": "Trust and Connect",
        "prompt.no": "Cancel",
        "prompt.saved": "Password saved in Keychain",
        "prompt.savefail": "Could not save the password in Keychain",

        "detail.password": "Password",
        "detail.password.saved": "saved in Keychain",
        "detail.password.forget": "Forget",
    ]

    /// Język interfejsu: polski, gdy jest pierwszym preferowanym językiem systemu, w przeciwnym razie angielski.
    public static let current: [String: String] = {
        let first = Locale.preferredLanguages.first?.lowercased() ?? "en"
        return first.hasPrefix("pl") ? pl : en
    }()
}

/// Tekst interfejsu po kluczu; brakujący klucz widać od razu (zamiast pustej etykiety).
public func L(_ key: String) -> String {
    Strings.current[key] ?? Strings.en[key] ?? key
}
