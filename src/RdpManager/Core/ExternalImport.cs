using System;
using System.Collections.Generic;
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
    }
}
