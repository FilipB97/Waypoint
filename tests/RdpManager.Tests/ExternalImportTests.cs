using System.Linq;
using RdpManager.Core;
using RdpManager.Models;
using Xunit;

namespace RdpManager.Tests
{
    public class ExternalImportTests
    {
        private const string MrngXml = @"<?xml version='1.0' encoding='utf-8'?>
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
</mrng:Connections>";

        [Fact]
        public void MRemoteNg_ParsesRdpAndSsh_SkipsOtherProtocols()
        {
            var r = ExternalImport.ParseMRemoteNg(MrngXml);

            Assert.Equal(4, r.Servers.Count);
            Assert.Equal(1, r.UnsupportedProtocol);   // VNC pominięty

            var web1 = r.Servers.Single(s => s.Name == "web1");
            Assert.Equal("10.0.0.1", web1.Host);
            Assert.Equal(3390, web1.Port);
            Assert.Equal(RemoteProtocol.Rdp, web1.Protocol);
            Assert.Equal("admin", web1.Username);
            Assert.Equal("CORP", web1.Domain);
            Assert.Equal("Prod", web1.Group);

            var lin1 = r.Servers.Single(s => s.Name == "lin1");
            Assert.Equal(RemoteProtocol.Ssh, lin1.Protocol);
            Assert.Equal(22, lin1.Port);
            Assert.Equal("root", lin1.Username);
            Assert.Equal("", lin1.Domain);            // SSH bez domeny

            var web2 = r.Servers.Single(s => s.Name == "web2");
            Assert.Equal("Prod / Wewn", web2.Group);  // zagnieżdżone kontenery → ścieżka

            var bare = r.Servers.Single(s => s.Name == "bare");
            Assert.Equal(3389, bare.Port);            // niepoprawny port → domyślny
            Assert.Equal("mRemoteNG", bare.Group);    // poza kontenerem → grupa domyślna
        }

        private const string RdgXml = @"<?xml version='1.0' encoding='utf-8'?>
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
</RDCMan>";

        [Fact]
        public void RdcMan_ParsesServersWithInheritedCredentialsAndPorts()
        {
            var r = ExternalImport.ParseRdcMan(RdgXml, 3389);

            Assert.Equal(2, r.Servers.Count);
            Assert.Equal(0, r.UnsupportedProtocol);

            var dc1 = r.Servers.Single(s => s.Name == "DC1");
            Assert.Equal("10.1.1.1", dc1.Host);
            Assert.Equal(3390, dc1.Port);             // host:port rozbite
            Assert.Equal("fileuser", dc1.Username);   // poświadczenia z poziomu pliku
            Assert.Equal("CORP", dc1.Domain);
            Assert.Equal("RDCMan", dc1.Group);
            Assert.Equal(RemoteProtocol.Rdp, dc1.Protocol);

            var web1 = r.Servers.Single(s => s.Host == "web1.corp.local");
            Assert.Equal("web1.corp.local", web1.Name);   // brak displayName → host
            Assert.Equal(3389, web1.Port);
            Assert.Equal("produser", web1.Username);      // nadpisane na poziomie grupy
            Assert.Equal("PROD", web1.Domain);
            Assert.Equal("Prod", web1.Group);
        }

        private const string RdmXml = @"<?xml version='1.0' encoding='utf-8'?>
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
</Connections>";

        [Fact]
        public void Rdm_MapsProtocols_SkipsGroups_CountsUnsupported()
        {
            var r = ExternalImport.ParseRdm(RdmXml);

            Assert.Equal(5, r.Servers.Count);        // folder pominięty, VNC nie dodany
            Assert.Equal(1, r.UnsupportedProtocol);  // VNC

            var dc1 = r.Servers.Single(s => s.Name == "DC1");
            Assert.Equal(RemoteProtocol.Rdp, dc1.Protocol);
            Assert.Equal("10.2.2.1", dc1.Host);
            Assert.Equal(3390, dc1.Port);
            Assert.Equal("admin", dc1.Username);
            Assert.Equal("CORP", dc1.Domain);
            Assert.Equal("Klienci / ACME", dc1.Group);   // backslash → " / "

            var web2 = r.Servers.Single(s => s.Name == "web2");
            Assert.Equal("10.2.2.6", web2.Host);         // host z <Url>, port rozbity
            Assert.Equal(3392, web2.Port);
            Assert.Equal("Klienci / ACME / Web", web2.Group);

            var lin1 = r.Servers.Single(s => s.Name == "lin1");
            Assert.Equal(RemoteProtocol.Ssh, lin1.Protocol);
            Assert.Equal(22, lin1.Port);                 // brak <Port> → domyślny
            Assert.Equal("", lin1.Domain);

            var sw = r.Servers.Single(s => s.Name == "switch1");
            Assert.Equal(RemoteProtocol.Telnet, sw.Protocol);
            Assert.Equal("10.2.2.3", sw.Host);
            Assert.Equal(23, sw.Port);
            Assert.Equal("RDM", sw.Group);               // brak <Group> → domyślna

            var portal = r.Servers.Single(s => s.Name == "Portal");
            Assert.Equal(RemoteProtocol.Http, portal.Protocol);
            Assert.Equal("https://portal.example.com", portal.Host);   // WWW: pełny URL w Host
        }

        [Fact]
        public void Parsers_EmptyDocuments_ReturnNoServers()
        {
            Assert.Empty(ExternalImport.ParseMRemoteNg("<Connections/>").Servers);
            Assert.Empty(ExternalImport.ParseRdcMan("<RDCMan/>").Servers);
            Assert.Empty(ExternalImport.ParseRdm("<Connections/>").Servers);
        }
    }
}
