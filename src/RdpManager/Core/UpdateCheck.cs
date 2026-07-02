using System;
using System.Text.Json;

namespace RdpManager.Core
{
    /// <summary>
    /// Sprawdzanie nowej wersji: parsowanie odpowiedzi GitHub `releases/latest`
    /// (pole „tag_name": „v1.2.0") i porównanie z wersją bieżącą. Sieć robi wołający.
    /// </summary>
    public static class UpdateCheck
    {
        /// <summary>Wersja z JSON-a releases/latest; null gdy odpowiedź nie ma sensownego taga.</summary>
        public static Version ParseLatest(string json)
        {
            try
            {
                using (var doc = JsonDocument.Parse(json))
                    return ParseTag(doc.RootElement.GetProperty("tag_name").GetString());
            }
            catch { return null; }
        }

        /// <summary>„v1.2.0" / „1.2" → Version; null gdy nieparsowalne.</summary>
        public static Version ParseTag(string tag)
        {
            tag = (tag ?? "").Trim().TrimStart('v', 'V');
            return Version.TryParse(tag, out var v) ? v : null;
        }

        public static bool IsNewer(Version latest, Version current)
            => latest != null && current != null && latest > current;
    }
}
