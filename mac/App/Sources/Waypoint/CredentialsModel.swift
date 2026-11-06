import AppKit
import UniformTypeIdentifiers
import WaypointCore

/// Profile poświadczeń, „Połącz jako…" i hasła w Pęku kluczy dla profili.
extension AppModel {
    func profile(for s: Server) -> CredentialProfile? {
        s.credentialProfileId.isEmpty ? nil : profiles.first { $0.id == s.credentialProfileId }
    }

    /// Zapis listy profili i haseł zmienionych w menedżerze (nil w słowniku = bez zmian, "" = usuń hasło).
    func saveProfiles(_ list: [CredentialProfile], passwords: [String: String]) {
        let removed = profiles.filter { p in !list.contains { $0.id == p.id } }
        for p in removed {
            let r = Credentials.detach(servers, profileId: p.id)
            servers = r.servers
            Keychain.delete(for: p.keychainAccount)
        }
        if !removed.isEmpty { persist() }
        for (id, pw) in passwords {
            guard let p = list.first(where: { $0.id == id }) else { continue }
            if pw.isEmpty { Keychain.delete(for: p.keychainAccount) }
            else { Keychain.save(pw, for: p.keychainAccount, label: "Waypoint — profil \(p.displayName)") }
        }
        profiles = list
        do { try CredentialProfileStore(directory: dataDirectory).save(list) }
        catch { alert = AppAlert(title: L("cred.title"), message: error.localizedDescription) }
    }

    func serversUsing(_ p: CredentialProfile) -> Int { servers.filter { $0.credentialProfileId == p.id }.count }

    /// `credprofiles.json` z wersji Windows (%APPDATA%\RdpManager). Hasła trzeba podać na Macu.
    func importProfilesFile() -> [CredentialProfile]? {
        let panel = NSOpenPanel()
        panel.title = L("cred.import")
        panel.message = L("cred.import.msg")
        panel.allowedContentTypes = [.json]
        guard panel.runModal() == .OK, let url = panel.url else { return nil }
        guard let data = try? Data(contentsOf: url), let list = try? CredentialProfileStore.decode(data), !list.isEmpty else {
            alert = AppAlert(title: L("cred.import"), message: L("import.err.format"))
            return nil
        }
        return list
    }

    /// „Połącz jako…": login (`DOMENA\user` albo `user`) i hasło tylko dla tego połączenia, albo —
    /// z „Zapamiętaj" — jako nowe dane logowania serwera (profil zdjęty, hasło w Pęku kluczy).
    func connectAs(_ s: Server, login: String, password: String, remember: Bool) {
        let (user, domain) = Credentials.splitLogin(login)
        let target = Credentials.connectAs(s, user: user, domain: domain)
        if !password.isEmpty { SessionPasswords.set(password, for: target) }
        if remember {
            saveLogin(of: target)
            if !password.isEmpty { Keychain.save(password, for: s.id, label: "Waypoint — \(s.displayName)") }
        }
        connectAsTarget = nil
        connect(target)
    }

    /// Login i domena połączenia zapisane jako dane logowania serwera na liście (profil zdjęty).
    func saveLogin(of target: Server) {
        guard let i = servers.firstIndex(where: { $0.id == target.id }) else { return }
        servers[i].username = target.username
        servers[i].domain = target.domain
        servers[i].credentialProfileId = ""
        persist()
    }
}
