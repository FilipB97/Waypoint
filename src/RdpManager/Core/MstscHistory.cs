using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace RdpManager.Core
{
    /// <summary>Jeden wpis historii mstsc: adres (host lub host:port) + ostatnio użyty login (jeśli jest).</summary>
    public sealed class MstscEntry
    {
        public string Address { get; set; }
        public string Username { get; set; }
    }

    /// <summary>
    /// Odczyt historii połączeń wbudowanego klienta RDP (mstsc) z rejestru bieżącego użytkownika.
    /// mstsc nie ma eksportu zbiorczego — trzyma tylko historię:
    ///  • hosty w  HKCU\Software\Microsoft\Terminal Server Client\Servers\&lt;host&gt;  (z wartością UsernameHint),
    ///  • ostatnio wpisane adresy w  ...\Terminal Server Client\Default  (wartości MRU0, MRU1, …).
    /// Hasła NIE są tu dostępne (mstsc trzyma je w Credential Managerze pod kluczem TERMSRV/&lt;host&gt;).
    /// </summary>
    public static class MstscHistory
    {
        public static List<MstscEntry> Read()
        {
            // Klucz = adres (bez uwzględniania wielkości liter); wartość = login (UsernameHint) lub "".
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using (var servers = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Terminal Server Client\Servers"))
                {
                    if (servers != null)
                        foreach (var host in servers.GetSubKeyNames())
                        {
                            if (string.IsNullOrWhiteSpace(host)) continue;
                            string user = "";
                            using (var sk = servers.OpenSubKey(host))
                                if (sk?.GetValue("UsernameHint") is string u) user = u;
                            map[host] = user;
                        }
                }
            }
            catch { /* brak klucza / brak dostępu — pomijamy */ }

            try
            {
                using (var def = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Terminal Server Client\Default"))
                {
                    if (def != null)
                        foreach (var name in def.GetValueNames())
                            if (name.StartsWith("MRU", StringComparison.OrdinalIgnoreCase)
                                && def.GetValue(name) is string addr
                                && !string.IsNullOrWhiteSpace(addr)
                                && !map.ContainsKey(addr))
                                map[addr] = "";
                }
            }
            catch { }

            var list = new List<MstscEntry>();
            foreach (var kv in map)
                list.Add(new MstscEntry { Address = kv.Key, Username = kv.Value });
            return list;
        }
    }
}
