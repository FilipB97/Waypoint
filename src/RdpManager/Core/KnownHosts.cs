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
        /// <summary>Serializuje odczyt-modyfikację-zapis pliku (terminal + SFTP + wiele sesji naraz).</summary>
        public static readonly object Sync = new object();

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

        public static Dictionary<string, string> Load(string dir) => Load(dir, out _);

        /// <summary>Jak <see cref="Load(string)"/>, ale ustawia <paramref name="storeUnreadable"/>=true, gdy plik
        /// ISTNIEJE, lecz nie dało się go odczytać/sparsować (uszkodzony). Wołający MUSI wtedy działać
        /// fail-closed (potraktować hosta jak ZMIANĘ klucza), a nie wracać do TOFU „nowy host" — inaczej po
        /// uszkodzeniu magazynu zmieniony klucz (MITM) wyglądałby jak nowy i pytanie nie ostrzegłoby o ataku.</summary>
        public static Dictionary<string, string> Load(string dir, out bool storeUnreadable)
        {
            storeUnreadable = false;
            var path = Path.Combine(dir, "known_hosts.json");
            try
            {
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                           ?? new Dictionary<string, string>();
            }
            catch
            {
                // Uszkodzony plik: odłóż na bok (.corrupt), zgłoś i zasygnalizuj wołającemu, żeby nie wracał
                // po cichu do TOFU (dotąd zmieniony klucz po uszkodzeniu magazynu wyglądał jak „nowy host").
                storeUnreadable = true;
                AtomicFile.PreserveCorrupt(path);
                HealthNotices.Add(HealthNoticeKind.FileQuarantined, Path.GetFileName(path));
            }
            return new Dictionary<string, string>();
        }

        public static void Save(string dir, Dictionary<string, string> store)
        {
            try
            {
                Directory.CreateDirectory(dir);
                AtomicFile.WriteAllText(Path.Combine(dir, "known_hosts.json"),
                    JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* nieudany zapis = pytanie wróci przy kolejnym połączeniu */ }
        }
    }
}
