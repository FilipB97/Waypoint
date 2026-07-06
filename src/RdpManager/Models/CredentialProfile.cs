using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RdpManager.Models
{
    /// <summary>
    /// Współdzielony profil poświadczeń: jeden login (domena + użytkownik) używany przez wiele serwerów,
    /// żeby nie wpisywać tego samego dla każdego z osobna. Hasło — jak wszędzie — NIE tu, tylko w Windows
    /// Credential Manager pod CredTarget. Serwer wskazuje profil przez ServerInfo.CredentialProfileId.
    /// </summary>
    public class CredentialProfile
    {
        /// <summary>Stały identyfikator — klucz hasła w Credential Manager i odnośnik z serwera.</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>Nazwa wyświetlana (np. „Domena ACME – admin").</summary>
        public string Name { get; set; } = "";
        public string Domain { get; set; } = "";
        public string Username { get; set; } = "";

        /// <summary>Klucz w Credential Manager (hasło NIE jest w tym modelu ani w JSON).</summary>
        [JsonIgnore]
        public string CredTarget => "RdpManager:profile:" + Id;

        /// <summary>Pola zapisane przez nowszą wersję — zachowaj przy load→save (jak w ServerInfo.Extra).</summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement> Extra { get; set; }
    }
}
