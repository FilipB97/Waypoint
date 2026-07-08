using System;

namespace RdpManager.Core
{
    /// <summary>
    /// Walidacja adresów przed uruchomieniem w domyślnej przeglądarce (wpisy WWW, w tym zaimportowane
    /// z RDCMan). Dopuszcza tylko http/https — host zawierający dowolny inny schemat (np. "file://",
    /// zarejestrowany handler) uruchomiłby coś innego niż przeglądarkę, bez pytania (Process.Start
    /// z UseShellExecute=true).
    /// </summary>
    public static class UrlValidation
    {
        /// <summary>Dokleja "https://" gdy brak schematu, po czym sprawdza, czy wynik to bezpieczny URL
        /// do otwarcia w przeglądarce (http/https). Publiczne dla testów.</summary>
        public static bool TryNormalizeWebUrl(string raw, out Uri uri)
        {
            uri = null;
            string url = (raw ?? "").Trim();
            if (url.Length == 0) return false;
            if (!url.Contains("://")) url = "https://" + url;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return false;
            if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
            uri = parsed;
            return true;
        }
    }
}
