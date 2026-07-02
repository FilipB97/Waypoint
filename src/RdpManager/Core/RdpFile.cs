using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RdpManager.Models;

namespace RdpManager.Core
{
    /// <summary>
    /// Import/eksport plików <c>.rdp</c> (format mstsc). Linie mają postać
    /// <c>klucz:typ:wartość</c>, gdzie typ to <c>s</c> (tekst) lub <c>i</c> (liczba).
    /// Czysta logika — bez WPF/ActiveX, w pełni testowalna.
    /// </summary>
    public static class RdpFile
    {
        /// <summary>Parsuje zawartość pliku .rdp na słownik klucz -&gt; wartość (typ pomijamy).</summary>
        public static Dictionary<string, string> ParseRaw(string content)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(content)) return map;

            foreach (var rawLine in content.Replace("\r\n", "\n").Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0) continue;

                // klucz może zawierać spacje; typ to jeden znak; wartość — reszta (może mieć ':').
                int firstColon = line.IndexOf(':');
                if (firstColon <= 0 || firstColon + 2 >= line.Length) continue;
                int secondColon = line.IndexOf(':', firstColon + 1);
                if (secondColon < 0) continue;

                string key = line.Substring(0, firstColon).Trim();
                string value = line.Substring(secondColon + 1);
                if (key.Length == 0) continue;
                map[key] = value;
            }
            return map;
        }

        /// <summary>Buduje <see cref="ServerInfo"/> z zawartości pliku .rdp.</summary>
        public static ServerInfo Parse(string content)
        {
            var map = ParseRaw(content);
            var s = new ServerInfo();

            if (map.TryGetValue("full address", out var addr) && !string.IsNullOrWhiteSpace(addr))
            {
                var (host, port) = SplitHostPort(addr.Trim());
                s.Host = host;
                // Port spoza 1..65535 ignorujemy (zostaje domyślny 3389) — plik .rdp bywa obcy.
                if (port.HasValue && port.Value >= 1 && port.Value <= 65535) s.Port = port.Value;
            }

            if (map.TryGetValue("username", out var user) && !string.IsNullOrWhiteSpace(user))
            {
                var (domain, name) = SplitDomainUser(user.Trim());
                s.Username = name;
                if (!string.IsNullOrEmpty(domain)) s.Domain = domain;
            }
            if (map.TryGetValue("domain", out var dom) && !string.IsNullOrWhiteSpace(dom))
                s.Domain = dom.Trim();

            if (TryGetInt(map, "redirectclipboard", out var clip)) s.RedirectClipboard = clip != 0;
            if (TryGetInt(map, "redirectprinters", out var prn)) s.RedirectPrinters = prn != 0;
            if (map.TryGetValue("drivestoredirect", out var drv))
                s.RedirectDrives = !string.IsNullOrWhiteSpace(drv);
            if (TryGetInt(map, "audiomode", out var audio)) s.AudioMode = Clamp(audio, 0, 2);
            if (TryGetInt(map, "authentication level", out var auth)) s.AuthenticationLevel = Clamp(auth, 0, 2);
            if (TryGetInt(map, "use multimon", out var mm)) s.UseAllMonitors = mm != 0;
            if (TryGetInt(map, "administrative session", out var admin)) s.AdminSession = admin != 0;

            if (map.TryGetValue("gatewayhostname", out var gw) && !string.IsNullOrWhiteSpace(gw))
                s.GatewayHostname = gw.Trim();
            if (TryGetInt(map, "gatewayusagemethod", out var gwm)) s.GatewayUsageMethod = Clamp(gwm, 0, 2);

            // Nazwa: z hosta, jeśli plik jej nie niesie (mstsc nie zapisuje przyjaznej nazwy).
            s.Name = string.IsNullOrWhiteSpace(s.Host) ? "Zaimportowany" : s.Host;
            s.Initials = RdpUtils.MakeInitials(s.Name);
            return s;
        }

        /// <summary>Serializuje <see cref="ServerInfo"/> do zawartości pliku .rdp (zgodnej z mstsc).</summary>
        public static string Serialize(ServerInfo s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            var sb = new StringBuilder();

            string addr = s.Host ?? "";
            if (s.Port != 0 && s.Port != 3389) addr = addr + ":" + s.Port.ToString(CultureInfo.InvariantCulture);
            sb.Append("full address:s:").Append(addr).Append("\r\n");

            if (!s.UseWindowsAccount && !string.IsNullOrEmpty(s.Username))
            {
                string user = string.IsNullOrEmpty(s.Domain) ? s.Username : s.Domain + "\\" + s.Username;
                sb.Append("username:s:").Append(user).Append("\r\n");
            }

            sb.Append("redirectclipboard:i:").Append(s.RedirectClipboard ? "1" : "0").Append("\r\n");
            sb.Append("redirectprinters:i:").Append(s.RedirectPrinters ? "1" : "0").Append("\r\n");
            sb.Append("drivestoredirect:s:").Append(s.RedirectDrives ? "*" : "").Append("\r\n");
            sb.Append("audiomode:i:").Append(Clamp(s.AudioMode, 0, 2).ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("authentication level:i:").Append(Clamp(s.AuthenticationLevel, 0, 2).ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("use multimon:i:").Append(s.UseAllMonitors ? "1" : "0").Append("\r\n");
            sb.Append("administrative session:i:").Append(s.AdminSession ? "1" : "0").Append("\r\n");

            if (!string.IsNullOrWhiteSpace(s.GatewayHostname))
            {
                sb.Append("gatewayhostname:s:").Append(s.GatewayHostname).Append("\r\n");
                int usage = s.GatewayUsageMethod == 0 ? 1 : s.GatewayUsageMethod;
                sb.Append("gatewayusagemethod:i:").Append(usage.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            }

            return sb.ToString();
        }

        // "host:3389" -> ("host", 3389); "host" -> ("host", null); IPv6 w [..] obsłużone.
        internal static (string host, int? port) SplitHostPort(string addr)
        {
            if (string.IsNullOrEmpty(addr)) return ("", null);
            if (addr.StartsWith("[")) // IPv6 literał, np. [::1]:3389
            {
                int end = addr.IndexOf(']');
                if (end > 0)
                {
                    string h = addr.Substring(1, end - 1);
                    if (end + 1 < addr.Length && addr[end + 1] == ':' &&
                        int.TryParse(addr.Substring(end + 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p6))
                        return (h, p6);
                    return (h, null);
                }
            }
            int idx = addr.LastIndexOf(':');
            if (idx > 0 && idx < addr.Length - 1 &&
                addr.IndexOf(':') == idx && // dokładnie jeden ':' -> host:port (nie goły IPv6)
                int.TryParse(addr.Substring(idx + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
                return (addr.Substring(0, idx), port);
            return (addr, null);
        }

        // "DOMENA\\user" -> ("DOMENA","user"); "user@domena" -> ("domena","user"); "user" -> ("","user").
        internal static (string domain, string user) SplitDomainUser(string value)
        {
            int slash = value.IndexOf('\\');
            if (slash > 0) return (value.Substring(0, slash), value.Substring(slash + 1));
            int at = value.IndexOf('@');
            if (at > 0) return (value.Substring(at + 1), value.Substring(0, at));
            return ("", value);
        }

        private static bool TryGetInt(Dictionary<string, string> map, string key, out int value)
        {
            value = 0;
            return map.TryGetValue(key, out var raw) &&
                   int.TryParse((raw ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
