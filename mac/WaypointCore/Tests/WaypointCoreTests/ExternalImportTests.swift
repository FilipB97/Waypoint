import Foundation
import Testing
@testable import WaypointCore

/// Te same pliki i oczekiwania co ExternalImportTests w wersji Windows.
@Suite struct ExternalImportTests {
    static let mrng = #"""
<?xml version='1.0' encoding='utf-8'?>
<mrng:Connections xmlns:mrng='http://mremoteng.org' Name='Connections' ConfVersion='2.6'>
  <Node Name='Prod' Type='Container' Username='' Hostname=''>
    <Node Name='web1' Type='Connection' Username='admin' Domain='CORP' Hostname='10.0.0.1' Protocol='RDP' Port='3390' />
    <Node Name='lin1' Type='Connection' Username='root' Hostname='10.0.0.2' Protocol='SSH2' Port='22' />
    <Node Name='Wewn' Type='Container'>
      <Node Name='web2' Type='Connection' Hostname='10.0.0.5' Protocol='RDP' Port='3389' />
    </Node>
  </Node>
  <Node Name='vnc1' Type='Connection' Hostname='10.0.0.3' Protocol='VNC' Port='5900' />
  <Node Name='bare' Type='Connection' Hostname='10.0.0.4' Protocol='RDP' Port='abc' />
</mrng:Connections>
"""#
    static let rdg = #"""
<?xml version='1.0' encoding='utf-8'?>
<RDCMan programVersion='2.90' schemaVersion='3'>
  <file>
    <properties><expanded>True</expanded><name>Firma</name></properties>
    <logonCredentials inherit='None'><userName>fileuser</userName><domain>CORP</domain></logonCredentials>
    <server><properties><name>10.1.1.1:3390</name><displayName>DC1</displayName></properties></server>
    <group>
      <properties><expanded>False</expanded><name>Prod</name></properties>
      <logonCredentials inherit='None'><userName>produser</userName><domain>PROD</domain></logonCredentials>
      <server><properties><name>web1.corp.local</name></properties></server>
    </group>
  </file>
</RDCMan>
"""#
    static let rdm = #"""
<?xml version='1.0' encoding='utf-8'?>
<Connections>
  <Connection><ConnectionType>Group</ConnectionType><Name>Klienci</Name><Group></Group></Connection>
  <Connection>
    <ConnectionType>RDPConfigured</ConnectionType><Name>DC1</Name><Group>Klienci\ACME</Group>
    <Host>10.2.2.1</Host><Port>3390</Port><Username>admin</Username><Domain>CORP</Domain>
  </Connection>
  <Connection>
    <ConnectionType>RDPConfigured</ConnectionType><Name>web2</Name><Group>Klienci\ACME\Web</Group>
    <Url>10.2.2.6:3392</Url>
  </Connection>
  <Connection>
    <ConnectionType>SSHShell</ConnectionType><Name>lin1</Name><Group>Klienci\ACME</Group>
    <Host>10.2.2.2</Host><Username>root</Username>
  </Connection>
  <Connection><ConnectionType>Telnet</ConnectionType><Name>switch1</Name><Url>10.2.2.3</Url></Connection>
  <Connection><ConnectionType>WebBrowser</ConnectionType><Name>Portal</Name><Url>https://portal.example.com</Url></Connection>
  <Connection><ConnectionType>VNC</ConnectionType><Name>kiosk</Name><Host>10.2.2.9</Host></Connection>
</Connections>
"""#
    static let fz = #"""
<?xml version='1.0'?>
<FileZilla3 version='3.66.1' platform='windows'>
  <Servers>
    <Server>
      <Host>ftp.example.com</Host><Port>21</Port><Protocol>0</Protocol><Type>0</Type>
      <User>alice</User><Pass encoding='base64'>c2VjcmV0</Pass><Logontype>1</Logontype><Name>Prod FTP</Name>
    </Server>
    <Server>
      <Host>sftp.example.com</Host><Port>2222</Port><Protocol>1</Protocol>
      <User>bob</User><Pass encoding='base64'>cHc=</Pass><Logontype>1</Logontype><Keyfile>C:\keys\id</Keyfile><Name>Box</Name>
    </Server>
    <Folder expanded='1'>Klienci
      <Server>
        <Host>ftps.example.com</Host><Port>990</Port><Protocol>4</Protocol><Logontype>0</Logontype><Name>Public</Name>
      </Server>
    </Folder>
    <Server>
      <Host>web.example.com</Host><Port>443</Port><Protocol>3</Protocol><Name>Site</Name>
    </Server>
  </Servers>
</FileZilla3>
"""#

    func parse(_ s: String, _ src: ExternalImport.Source) throws -> ExternalImport.Result {
        try ExternalImport.parse(Data(s.utf8), as: src)
    }

    @Test func mRemoteNG() throws {
        let r = try parse(Self.mrng, .mRemoteNG)
        #expect(r.servers.count == 4 && r.unsupported == 1)
        let web1 = r.servers.first { $0.name == "web1" }!
        #expect(web1.host == "10.0.0.1" && web1.port == 3390 && web1.proto == .rdp)
        #expect(web1.username == "admin" && web1.domain == "CORP" && web1.group == "Prod")
        let lin1 = r.servers.first { $0.name == "lin1" }!
        #expect(lin1.proto == .ssh && lin1.port == 22 && lin1.username == "root" && lin1.domain.isEmpty)
        #expect(r.servers.first { $0.name == "web2" }!.group == "Prod / Wewn")
        let bare = r.servers.first { $0.name == "bare" }!
        #expect(bare.port == 3389 && bare.group == "mRemoteNG")
    }

    @Test func rdcMan() throws {
        let r = try parse(Self.rdg, .rdcMan)
        #expect(r.servers.count == 2 && r.unsupported == 0)
        let dc1 = r.servers.first { $0.name == "DC1" }!
        #expect(dc1.host == "10.1.1.1" && dc1.port == 3390 && dc1.username == "fileuser" && dc1.domain == "CORP")
        #expect(dc1.group == "RDCMan" && dc1.proto == .rdp)
        let web1 = r.servers.first { $0.host == "web1.corp.local" }!
        #expect(web1.name == "web1.corp.local" && web1.port == 3389 && web1.username == "produser")
        #expect(web1.domain == "PROD" && web1.group == "Prod")
    }

    @Test func rdm() throws {
        let r = try parse(Self.rdm, .rdm)
        #expect(r.servers.count == 5 && r.unsupported == 1)
        let dc1 = r.servers.first { $0.name == "DC1" }!
        #expect(dc1.proto == .rdp && dc1.host == "10.2.2.1" && dc1.port == 3390 && dc1.group == "Klienci / ACME")
        let web2 = r.servers.first { $0.name == "web2" }!
        #expect(web2.host == "10.2.2.6" && web2.port == 3392 && web2.group == "Klienci / ACME / Web")
        let lin1 = r.servers.first { $0.name == "lin1" }!
        #expect(lin1.proto == .ssh && lin1.port == 22 && lin1.domain.isEmpty)
        let sw = r.servers.first { $0.name == "switch1" }!
        #expect(sw.proto == .telnet && sw.host == "10.2.2.3" && sw.port == 23 && sw.group == "RDM")
        let portal = r.servers.first { $0.name == "Portal" }!
        #expect(portal.proto == .http && portal.host == "https://portal.example.com")
    }

    @Test func fileZilla() throws {
        let r = try parse(Self.fz, .fileZilla)
        #expect(r.servers.count == 3 && r.unsupported == 1)
        let ftp = r.servers.first { $0.name == "Prod FTP" }!
        #expect(ftp.proto == .ftp && ftp.port == 21 && ftp.username == "alice" && ftp.ftpEncryption == 3)
        #expect(!ftp.ftpAnonymous && ftp.group == "FileZilla" && r.passwords[ftp.id] == "secret")
        let sftp = r.servers.first { $0.name == "Box" }!
        #expect(sftp.proto == .sftp && sftp.port == 2222 && sftp.privateKeyPath == "C:\\keys\\id" && r.passwords[sftp.id] == "pw")
        let pub = r.servers.first { $0.name == "Public" }!
        #expect(pub.ftpEncryption == 1 && pub.ftpAnonymous && pub.username.isEmpty && pub.group == "Klienci")
        #expect(r.passwords[pub.id] == nil)
    }

    @Test func pusteIZle() {
        #expect(throws: ExternalImport.Failure.empty) { try parse("<Connections/>", .mRemoteNG) }
        #expect(throws: ExternalImport.Failure.empty) { try parse("<FileZilla3/>", .fileZilla) }
        #expect(throws: ExternalImport.Failure.notXML) { try parse("{json}", .rdcMan) }
    }

    @Test func scalanieBezDuplikatow() throws {
        let r = try parse(Self.fz, .fileZilla)
        let existing = [Server(host: "FTP.example.com", port: 21, proto: .ftp)]
        let m = ExternalImport.merge(existing, r)
        #expect(m.added == 2 && m.skipped == 1 && m.servers.count == 3)
        #expect(m.passwords.count == 1)   // hasło „secret" należało do pominiętego duplikatu
    }

    @Test func hostIPort() {
        #expect(ExternalImport.splitHostPort("h:3390", defaultPort: 3389) == ("h", 3390))
        #expect(ExternalImport.splitHostPort("[fe80::1]:4000", defaultPort: 3389) == ("fe80::1", 4000))
        #expect(ExternalImport.splitHostPort("fe80::1", defaultPort: 3389) == ("fe80::1", 3389))
        #expect(ExternalImport.splitHostPort("h:abc", defaultPort: 22) == ("h:abc", 22))
    }
}

@Suite struct ProfileExportTests {
    @Test func eksportIImport() throws {
        var s = Server(id: "s1", name: "web", host: "h", proto: .ssh)
        s.credentialProfileId = "p1"
        let data = try ProfileExport.serialize(servers: [s], profiles: [CredentialProfile(id: "p1", name: "ACME", username: "adm")])
        let json = String(decoding: data, as: UTF8.self)
        #expect(json.contains("\"Version\" : 1") && !json.contains("Settings"))
        #expect(try ProfileImport.parse(data).map(\.id) == ["s1"])            // ten sam import co z Windows
        #expect(ProfileExport.credentialProfiles(in: data).map(\.username) == ["adm"])
        let bare = try ProfileExport.serialize(servers: [s], profiles: [])
        #expect(!String(decoding: bare, as: UTF8.self).contains("CredentialProfiles"))
        #expect(ProfileExport.credentialProfiles(in: Data(#"{"Version":1,"Servers":[]}"#.utf8)).isEmpty)
    }
}
