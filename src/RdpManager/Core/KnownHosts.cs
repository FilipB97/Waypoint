using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace RdpManager.Core
{
    /// <summary>
    /// Magazyn znanych kluczy hostów SSH (TOFU — trust on first use):
    /// %APPDATA%\RdpManager\known_hosts.json. Klucz wpisu "host:port" (host małymi literami),
    /// wartość: odcisk "SHA256:&lt;base64&gt;" (format jak w OpenSSH).
    /// </summary>
    public static class KnownHosts
    {
        public enum Status { Unknown, Match, Mismatch }

        /// <summary>Odcisk SHA256 klucza publicznego hosta w notacji OpenSSH (base64 bez '=').</summary>
        public static string Fingerprint(byte[] hostKey)
        {
            using (var sha = SHA256.Create())
                return "SHA256:" + Convert.ToBase64String(sha.ComputeHash(hostKey ?? Array.Empty<byte>())).TrimEnd('=');
        }

        public static string EntryKey(string host, int port)
            => (host ?? "").Trim().ToLowerInvariant() + ":" + port;

        public static Status Check(Dictionary<string, string> store, string host, int port, string fingerprint)
        {
            if (store == null || !store.TryGetValue(EntryKey(host, port), out var known)) return Status.Unknown;
            return string.Equals(known, fingerprint, StringComparison.Ordinal) ? Status.Match : Status.Mismatch;
        }

        public static Dictionary<string, string> Load(string dir)
        {
            try
            {
                var path = Path.Combine(dir, "known_hosts.json");
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                           ?? new Dictionary<string, string>();
            }
            catch { /* uszkodzony plik — start od pustego; użytkownik potwierdzi klucze ponownie */ }
            return new Dictionary<string, string>();
        }

        public static void Save(string dir, Dictionary<string, string> store)
        {
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "known_hosts.json"),
                    JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* nieudany zapis = pytanie wróci przy kolejnym połączeniu */ }
        }
    }
}
