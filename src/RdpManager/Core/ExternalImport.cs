using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using RdpManager.Models;

namespace RdpManager.Core
{
    /// <summary>
    /// Import listy połączeń z innych menedżerów: mRemoteNG (confCons.xml) i RDCMan (.rdg).
    /// Czyste parsowanie XML → ServerInfo, bez haseł (oba formaty szyfrują je własnymi kluczami);
    /// dedup i zapis robi wołający.
    /// </summary>
    public static class ExternalImport
    {
        /// <summary>Wynik importu: rozpoznane serwery + liczba pominiętych z powodu protokołu.</summary>
        public sealed class Result
        {
            public List<ServerInfo> Servers { get; } = new List<ServerInfo>();
            public int UnsupportedProtocol { get; set; }

            /// <summary>Hasła do zapisania w Credential Managerze (klucz = ServerInfo.Id). Puste dla źródeł
            /// szyfrujących hasła własnym kluczem (mRemoteNG/RDCMan/RDM); wypełniane przez import FileZilli.</summary>
            public Dictionary<string, string> Passwords { get; } = new Dictionary<string, string>();
        }

        // ---------- mRemoteNG (confCons.xml) ----------

        /// <summary>Parsuje mRemoteNG confCons.xml. Obsługiwane protokoły: RDP i SSH1/SSH2.</summary>
        public static Result ParseMRemoteNg(string xml)
        {
            var result = new Result();
            var root = XDocument.Parse(xml).Root;
            if (root != null) DescendMrng(root, "", result);
            return result;
        }

        private static void DescendMrng(XElement parent, string path, Result result)
        {
            foreach (var node in parent.Elements())
            {
                if (!string.Equals(node.Name.LocalName, "Node", StringComparison.OrdinalIgnoreCase)) continue;
                string type = (string)node.Attribute("Type") ?? "";
                string name = ((string)node.Attribute("Name") ?? "").Trim();

                if (string.Equals(type, "Container", StringComparison.OrdinalIgnoreCase))
                {
                    DescendMrng(node, string.IsNullOrEmpty(path) ? name : path + " / " + name, result);
                    continue;
                }
                if (!string.Equals(type, "Connection", StringComparison.OrdinalIgnoreCase)) continue;

                string protocol = ((string)node.Attribute("Protocol") ?? "").Trim().ToUpperInvariant();
                bool ssh = protocol == "SSH2" || protocol == "SSH1";
                if (!ssh && protocol != "RDP") { result.UnsupportedProtocol++; continue; }

                string host = ((string)node.Attribute("Hostname") ?? "").Trim();
                if (host.Length == 0) continue;
                int defPort = ssh ? 22 : 3389;
                int port = int.TryParse((string)node.Attribute("Port"), out var p) && p >= 1 && p <= 65535 ? p : defPort;

                string display = string.IsNullOrWhiteSpace(name) ? host : name;
                result.Servers.Add(new ServerInfo
                {
                    Name = display,
                    Host = host,
                    Port = port,
                    Protocol = ssh ? RemoteProtocol.Ssh : RemoteProtocol.Rdp,
                    Username = ((string)node.Attribute("Username") ?? "").Trim(),
                    Domain = ssh ? "" : ((string)node.Attribute("Domain") ?? "").Trim(),
                    Group = string.IsNullOrEmpty(path) ? "mRemoteNG" : path,
                    Initials = RdpUtils.MakeInitials(display),
                    Status = ServerStatus.Offline
                });
            }
        }

        // ---------- RDCMan (.rdg) ----------

        /// <summary>
        /// Parsuje RDCMan .rdg (tylko RDP). Login/domena dziedziczone z <c>logonCredentials</c>
        /// pliku/grup; adres „host:port" rozbijany jak przy imporcie z mstsc.
        /// </summary>
        public static Result ParseRdcMan(string xml, int defaultPort = 3389)
        {
            var result = new Result();
            var file = XDocument.Parse(xml).Root?.Element("file");
            if (file != null) DescendRdg(file, "", "", "", result, defaultPort);
            return result;
        }

        private static void DescendRdg(XElement parent, string path, string user, string domain,
                                       Result result, int defaultPort)
        {
            var creds = parent.Element("logonCredentials");
            if (creds != null)
            {
                user = ((string)creds.Element("userName") ?? user).Trim();
                domain = ((string)creds.Element("domain") ?? domain).Trim();
            }

            foreach (var server in parent.Elements("server"))
            {
                var props = server.Element("properties");
                // starsze schematy (v1/2) trzymają <name> bezpośrednio pod <server>
                string address = ((string)props?.Element("name") ?? (string)server.Element("name") ?? "").Trim();
                if (address.Length == 0) continue;
                var (host, port) = RdpUtils.SplitHostPort(address, defaultPort);
                string display = ((string)props?.Element("displayName")
                                  ?? (string)server.Element("displayName") ?? "").Trim();

                string su = user, sd = domain;
                var sc = server.Element("logonCredentials");
                if (sc != null)
                {
                    su = ((string)sc.Element("userName") ?? su).Trim();
                    sd = ((string)sc.Element("domain") ?? sd).Trim();
                }

                string name = display.Length > 0 ? display : host;
                result.Servers.Add(new ServerInfo
                {
                    Name = name,
                    Host = host,
                    Port = port,
                    Username = su,
                    Domain = sd,
                    Group = string.IsNullOrEmpty(path) ? "RDCMan" : path,
                    Initials = RdpUtils.MakeInitials(name),
                    Status = ServerStatus.Offline
                });
            }

            foreach (var group in parent.Elements("group"))
            {
                string gname = ((string)group.Element("properties")?.Element("name") ?? "").Trim();
                DescendRdg(group, string.IsNullOrEmpty(path) ? gname : path + " / " + gname,
                           user, domain, result, defaultPort);
            }
        }

        // ---------- Devolutions Remote Desktop Manager (eksport XML .rdm/.xml) ----------

        /// <summary>
        /// Parsuje eksport XML z RDM (root Connections lub ArrayOfConnection). Mapowanie typów:
        /// RDPConfigured→RDP, SSHShell→SSH, Telnet→Telnet, WebBrowser→WWW. Wpisy-foldery
        /// (ConnectionType=Group) pomijane; ścieżka grupy brana z pola &lt;Group&gt; każdego wpisu.
        /// Haseł nie importujemy (RDM trzyma je zaszyfrowane własnym kluczem/skarbcem).
        /// </summary>
        public static Result ParseRdm(string xml)
        {
            var result = new Result();
            var root = XDocument.Parse(xml).Root;
            if (root == null) return result;

            foreach (var node in root.Descendants().Where(e => LocalIs(e, "Connection")))
            {
                string type = Elem(node, "ConnectionType").ToUpperInvariant();
                if (type.Length == 0 || type == "GROUP") continue;   // folder / pusty — nie połączenie

                RemoteProtocol proto; int defPort;
                switch (type)
                {
                    case "RDPCONFIGURED": proto = RemoteProtocol.Rdp;    defPort = 3389; break;
                    case "SSHSHELL":      proto = RemoteProtocol.Ssh;    defPort = 22;   break;
                    case "TELNET":        proto = RemoteProtocol.Telnet; defPort = 23;   break;
                    case "WEBBROWSER":    proto = RemoteProtocol.Http;   defPort = 443;  break;
                    default: result.UnsupportedProtocol++; continue;
                }

                string host = FirstNonEmpty(Elem(node, "Host"), Elem(node, "HostName"), Elem(node, "Url"));
                if (host.Length == 0) continue;

                int port = defPort;
                if (proto != RemoteProtocol.Http)   // dla WWW cały adres (z ewentualnym portem) zostaje w Host
                {
                    var (h, p) = RdpUtils.SplitHostPort(host, defPort);
                    host = h; port = p;
                    if (int.TryParse(Elem(node, "Port"), out var pe) && pe >= 1 && pe <= 65535) port = pe;
                }

                string name = Elem(node, "Name");
                string display = name.Length > 0 ? name : host;
                result.Servers.Add(new ServerInfo
                {
                    Name = display,
                    Host = host,
                    Port = port,
                    Protocol = proto,
                    Username = FirstNonEmpty(Elem(node, "Username"), Elem(node, "UserName")),
                    Domain = proto == RemoteProtocol.Rdp ? Elem(node, "Domain") : "",
                    Group = RdmGroup(Elem(node, "Group")),
                    Initials = RdpUtils.MakeInitials(display),
                    Status = ServerStatus.Offline
                });
            }
            return result;
        }

        // ---------- FileZilla (sitemanager.xml) ----------

        /// <summary>
        /// Parsuje FileZilla sitemanager.xml. Protokoły: FTP(0)→FTP(Auto), SFTP(1)→SFTP,
        /// FTPS implicit(4)→FTP niejawne, FTPES explicit(5)→FTP jawne, plain(6)→FTP bez TLS; reszta pominięta.
        /// W przeciwieństwie do pozostałych źródeł FileZilla trzyma hasła odzyskiwalnie
        /// (Pass encoding="base64", gdy nie ustawiono hasła głównego) — dekodujemy je do <see cref="Result.Passwords"/>.
        /// Klucz SFTP z &lt;Keyfile&gt;; foldery → ścieżka grupy.
        /// </summary>
        public static Result ParseFileZilla(string xml)
        {
            var result = new Result();
            var servers = XDocument.Parse(xml).Root?.Element("Servers");
            if (servers != null) DescendFz(servers, "", result);
            return result;
        }

        private static void DescendFz(XElement parent, string path, Result result)
        {
            foreach (var node in parent.Elements())
            {
                if (string.Equals(node.Name.LocalName, "Folder", StringComparison.OrdinalIgnoreCase))
                {
                    // Nazwa folderu = bezpośredni tekst (mieszana zawartość: nazwa + zagnieżdżone <Server>).
                    string fname = string.Concat(node.Nodes().OfType<XText>().Select(t => t.Value)).Trim();
                    DescendFz(node, string.IsNullOrEmpty(path) ? fname : path + " / " + fname, result);
                    continue;
                }
                if (!string.Equals(node.Name.LocalName, "Server", StringComparison.OrdinalIgnoreCase)) continue;

                string host = Elem(node, "Host");
                if (host.Length == 0) continue;

                int proto = int.TryParse(Elem(node, "Protocol"), out var pr) ? pr : 0;
                RemoteProtocol rp; int enc = 0; int defPort;
                switch (proto)
                {
                    case 0: rp = RemoteProtocol.Ftp;  enc = 3; defPort = 21;  break;   // FTP → Auto (FTPS jeśli dostępne)
                    case 1: rp = RemoteProtocol.Sftp;          defPort = 22;  break;
                    case 4: rp = RemoteProtocol.Ftp;  enc = 1; defPort = 990; break;   // implicit FTPS
                    case 5: rp = RemoteProtocol.Ftp;  enc = 0; defPort = 21;  break;   // explicit FTPES
                    case 6: rp = RemoteProtocol.Ftp;  enc = 2; defPort = 21;  break;   // plain FTP
                    default: result.UnsupportedProtocol++; continue;                    // HTTP/HTTPS/S3/…
                }

                int port = int.TryParse(Elem(node, "Port"), out var p) && p >= 1 && p <= 65535 ? p : defPort;
                bool anon = int.TryParse(Elem(node, "Logontype"), out var lt) && lt == 0;

                string name = Elem(node, "Name");
                string display = name.Length > 0 ? name : host;
                var srv = new ServerInfo
                {
                    Name = display,
                    Host = host,
                    Port = port,
                    Protocol = rp,
                    Username = anon ? "" : Elem(node, "User"),
                    FtpEncryption = rp == RemoteProtocol.Ftp ? enc : 0,
                    FtpAnonymous = rp == RemoteProtocol.Ftp && anon,
                    PrivateKeyPath = rp == RemoteProtocol.Sftp ? Elem(node, "Keyfile") : "",
                    Group = string.IsNullOrEmpty(path) ? "FileZilla" : path,
                    Initials = RdpUtils.MakeInitials(display),
                    Status = ServerStatus.Offline
                };

                // Hasło: base64(UTF-8). Pomijamy, gdy chronione hasłem głównym FileZilli (encoding="crypt").
                var pass = node.Elements().FirstOrDefault(e =>
                    string.Equals(e.Name.LocalName, "Pass", StringComparison.OrdinalIgnoreCase));
                if (!anon && pass != null &&
                    string.Equals((string)pass.Attribute("encoding"), "base64", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        string pw = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(pass.Value.Trim()));
                        if (pw.Length > 0) result.Passwords[srv.Id] = pw;
                    }
                    catch { /* uszkodzony base64 — pomiń samo hasło, serwer i tak importujemy */ }
                }

                result.Servers.Add(srv);
            }
        }

        private static bool LocalIs(XElement e, string name) =>
            string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase);

        // Wartość pierwszego dziecka o danej nazwie lokalnej (ignoruje przestrzenie nazw).
        private static string Elem(XElement parent, string localName)
        {
            foreach (var c in parent.Elements())
                if (string.Equals(c.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))
                    return (c.Value ?? "").Trim();
            return "";
        }

        private static string FirstNonEmpty(params string[] vals)
        {
            foreach (var v in vals) if (!string.IsNullOrEmpty(v)) return v;
            return "";
        }

        // "Klienci\ACME\Serwery" → "Klienci / ACME / Serwery" (spójnie z importem mRemoteNG/RDCMan).
        private static string RdmGroup(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "RDM";
            var parts = raw.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries)
                           .Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
            return parts.Length == 0 ? "RDM" : string.Join(" / ", parts);
        }
    }
}
