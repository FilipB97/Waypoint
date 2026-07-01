using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using RdpManager.Models;

namespace RdpManager.Core
{
    /// <summary>Zawartość kopii profilu: ustawienia + lista serwerów (bez haseł).</summary>
    public class ProfileData
    {
        public int Version { get; set; } = 1;
        public AppSettings Settings { get; set; }
        public List<ServerInfo> Servers { get; set; } = new List<ServerInfo>();
    }

    /// <summary>
    /// Eksport/import całego profilu (serwery + ustawienia) do jednego pliku JSON.
    /// Hasła NIE są zawarte — pozostają w Windows Credential Manager. Czysta logika, testowalna.
    /// </summary>
    public static class ProfileBackup
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions { WriteIndented = true };

        public static string Serialize(AppSettings settings, IEnumerable<ServerInfo> servers)
        {
            var data = new ProfileData
            {
                Settings = settings,
                Servers = (servers ?? Enumerable.Empty<ServerInfo>()).ToList()
            };
            return JsonSerializer.Serialize(data, Options);
        }

        /// <summary>Parsuje kopię; zwraca null przy pustych/niepoprawnych danych.</summary>
        public static ProfileData Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            var data = JsonSerializer.Deserialize<ProfileData>(json, Options);
            if (data == null) return null;
            data.Servers ??= new List<ServerInfo>();
            return data;
        }
    }
}
