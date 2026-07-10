using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace RdpManager.Core
{
    /// <summary>
    /// Magazyn zaufanych certyfikatów FTPS (TOFU — trust on first use), wzorem <see cref="KnownHosts"/> (SSH):
    /// %APPDATA%\RdpManager\ftps_certs.json. Klucz wpisu "host:port" (host małymi literami), wartość:
    /// odcisk SHA-256 certyfikatu serwera (hex). Bez tego FtpFs akceptowałby DOWOLNY certyfikat (aktywny MITM).
    /// </summary>
    public static class FtpsCertPinning
    {
        /// <summary>Serializuje odczyt-modyfikację-zapis pliku (wiele sesji FTP naraz).</summary>
        public static readonly object Sync = new object();

        public enum Status { Unknown, Match, Mismatch }

        /// <summary>Odcisk SHA-256 certyfikatu serwera (hex, jak X509Certificate2.GetCertHashString).</summary>
        public static string Fingerprint(X509Certificate cert)
        {
            using (var c2 = new X509Certificate2(cert))
                return c2.GetCertHashString(HashAlgorithmName.SHA256);
        }

        public static string EntryKey(string host, int port)
            => (host ?? "").Trim().ToLowerInvariant() + ":" + port;

        public static Status Check(Dictionary<string, string> store, string host, int port, string fingerprint)
        {
            if (store == null || !store.TryGetValue(EntryKey(host, port), out var known)) return Status.Unknown;
            return string.Equals(known, fingerprint, StringComparison.OrdinalIgnoreCase) ? Status.Match : Status.Mismatch;
        }

        public static Dictionary<string, string> Load(string dir) => Load(dir, out _);

        /// <summary>Jak <see cref="Load(string)"/>, ale ustawia <paramref name="storeUnreadable"/>=true, gdy plik
        /// ISTNIEJE, lecz nie dało się go odczytać/sparsować (uszkodzony). Wołający MUSI wtedy działać
        /// fail-closed (potraktować certyfikat jak ZMIANĘ), a nie wracać do TOFU „nowy serwer".</summary>
        public static Dictionary<string, string> Load(string dir, out bool storeUnreadable)
        {
            storeUnreadable = false;
            var path = Path.Combine(dir, "ftps_certs.json");
            try
            {
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                           ?? new Dictionary<string, string>();
            }
            catch
            {
                // Uszkodzony plik: odłóż na bok (.corrupt), zgłoś i zasygnalizuj wołającemu, żeby nie wracał
                // po cichu do TOFU (zmieniony certyfikat po uszkodzeniu magazynu wyglądałby jak „nowy serwer").
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
                AtomicFile.WriteAllText(Path.Combine(dir, "ftps_certs.json"),
                    JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* nieudany zapis = pytanie wróci przy kolejnym połączeniu */ }
        }
    }
}
