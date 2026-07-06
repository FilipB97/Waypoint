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

        /// <summary>Wersja + adres i rozmiar assetu .exe (win-x64) do auto-aktualizacji. null gdy JSON bez sensu.</summary>
        public sealed class ReleaseInfo
        {
            public Version Version { get; set; }
            public string ExeUrl { get; set; }
            public long ExeSize { get; set; }
            public string HtmlUrl { get; set; }
            public string Notes { get; set; }   // treść wydania (markdown „body") = changelog
        }

        public static ReleaseInfo ParseRelease(string json)
        {
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    var info = new ReleaseInfo
                    {
                        Version = ParseTag(root.TryGetProperty("tag_name", out var t) ? t.GetString() : null),
                        HtmlUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() : null,
                        Notes = root.TryGetProperty("body", out var b) ? b.GetString() : null
                    };
                    if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                    {
                        string firstUrl = null; long firstSize = 0;
                        foreach (var a in assets.EnumerateArray())
                        {
                            string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                            string url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                            long size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0;
                            if (name.IndexOf("x64", StringComparison.OrdinalIgnoreCase) >= 0)   // preferuj win-x64
                            {
                                info.ExeUrl = url; info.ExeSize = size;
                                break;
                            }
                            if (firstUrl == null) { firstUrl = url; firstSize = size; }
                        }
                        if (info.ExeUrl == null) { info.ExeUrl = firstUrl; info.ExeSize = firstSize; }
                    }
                    return info;
                }
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
