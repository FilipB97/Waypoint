using System;
using System.Collections.Generic;
using System.Text;

namespace RdpManager.Core
{
    /// <summary>Jak pokazać zawartość pliku w podglądzie.</summary>
    public enum PreviewKind
    {
        /// <summary>Zwykły tekst/kod — monospace z numeracją linii.</summary>
        Text,
        /// <summary>JSON — kolorowany tymi samymi tokenami co konsola REST.</summary>
        Json,
        /// <summary>Markdown — renderowany przez <see cref="MarkdownLite"/>.</summary>
        Markdown,
        /// <summary>Obraz — dekodowany przez WPF.</summary>
        Image,
        /// <summary>Dane binarne — zrzut szesnastkowy.</summary>
        Binary
    }

    /// <summary>
    /// Rozpoznawanie i przygotowanie treści do podglądu pliku (panel plików). Czysta logika, bez WPF —
    /// całość jest testowalna, a okno podglądu tylko rysuje to, co tu powstaje.
    ///
    /// Zdalny system plików (<see cref="IRemoteFs"/>) nie umie czytać fragmentu pliku — jest tylko
    /// pobranie CAŁOŚCI do strumienia. Dlatego rozmiar bramkuje panel: powyżej <see cref="SoftLimitBytes"/>
    /// pyta, czy naprawdę ciągnąć plik przez sieć, a <see cref="HardLimitBytes"/> jest granicą, powyżej
    /// której podgląd nie ma sensu (i tak trafiłby w limit pamięci albo w kilkuminutowe pobieranie).
    /// </summary>
    public static class FilePreview
    {
        /// <summary>Powyżej tego rozmiaru podgląd pyta o potwierdzenie (pobranie idzie przez sieć).</summary>
        public const long SoftLimitBytes = 2L * 1024 * 1024;
        /// <summary>Powyżej tego rozmiaru podgląd jest odmawiany.</summary>
        public const long HardLimitBytes = 64L * 1024 * 1024;
        /// <summary>Ile bajtów wystarczy, by rozpoznać treść binarną.</summary>
        public const int SniffBytes = 8000;
        /// <summary>Limit linii pokazywanych w podglądzie tekstowym (reszta obcięta z adnotacją).</summary>
        public const int MaxTextLines = 20000;

        private static readonly HashSet<string> ImageExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".tif", ".tiff", ".webp" };

        private static readonly HashSet<string> MarkdownExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".md", ".markdown", ".mdown" };

        /// <summary>
        /// Rodzaj podglądu: najpierw rozszerzenie (obrazy/JSON/Markdown mają jednoznaczne), potem treść.
        /// Rozszerzenie samo nie wystarcza — plik „.json" bywa pusty albo binarny — więc dla tekstowych
        /// kandydatów i tak decyduje wynik <see cref="LooksBinary"/>.
        /// </summary>
        public static PreviewKind KindFor(string fileName, byte[] head)
        {
            string ext = ExtensionOf(fileName);
            if (ImageExt.Contains(ext)) return PreviewKind.Image;
            if (LooksBinary(head)) return PreviewKind.Binary;
            if (MarkdownExt.Contains(ext)) return PreviewKind.Markdown;
            if (string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase)) return PreviewKind.Json;
            return PreviewKind.Text;
        }

        private static string ExtensionOf(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return "";
            int dot = fileName.LastIndexOf('.');
            return dot < 0 ? "" : fileName.Substring(dot);
        }

        /// <summary>
        /// Czy to dane binarne. Kryterium jak w git/grep: bajt zerowy w próbce przesądza od razu,
        /// bo w żadnym praktycznym kodowaniu tekstowym (poza UTF-16, które łapiemy po BOM) nie występuje.
        /// Dodatkowo próg udziału znaków sterujących — łapie pliki bez zer, ale i tak nieczytelne.
        /// </summary>
        public static bool LooksBinary(byte[] head)
        {
            if (head == null || head.Length == 0) return false;   // pusty plik pokazujemy jako tekst
            if (HasBom(head)) return false;

            int n = Math.Min(head.Length, SniffBytes);
            int ctrl = 0;
            for (int i = 0; i < n; i++)
            {
                byte b = head[i];
                if (b == 0) return true;
                // Sterujące poza tabulatorem, LF, CR, wysunięciem strony i Escape (sekwencje ANSI w logach).
                if (b < 0x20 && b != 0x09 && b != 0x0A && b != 0x0D && b != 0x0C && b != 0x1B) ctrl++;
            }
            return ctrl * 100 / n > 10;
        }

        private static bool HasBom(byte[] d)
            => (d.Length >= 3 && d[0] == 0xEF && d[1] == 0xBB && d[2] == 0xBF)
            || (d.Length >= 2 && d[0] == 0xFF && d[1] == 0xFE)
            || (d.Length >= 2 && d[0] == 0xFE && d[1] == 0xFF);

        /// <summary>
        /// Dekoduje bajty na tekst. Kolejność: BOM (jednoznaczny) → UTF-8 w trybie ścisłym → Latin-1.
        /// Latin-1 jest awaryjne i NIGDY nie rzuca, więc podgląd pokazuje cokolwiek zamiast błędu; że
        /// tak się stało, mówi <paramref name="encodingName"/> (okno wypisuje to w stopce, żeby nie
        /// sugerować, że polskie znaki w takim pliku są poprawne).
        /// </summary>
        public static string DecodeText(byte[] data, out string encodingName)
        {
            encodingName = "UTF-8";
            if (data == null || data.Length == 0) return "";

            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            { encodingName = "UTF-8 (BOM)"; return new UTF8Encoding(false).GetString(data, 3, data.Length - 3); }
            if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
            { encodingName = "UTF-16 LE"; return Encoding.Unicode.GetString(data, 2, data.Length - 2); }
            if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
            { encodingName = "UTF-16 BE"; return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2); }

            try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(data); }
            catch (DecoderFallbackException)
            {
                encodingName = "Latin-1";   // nie-UTF-8: pokazujemy bajt-w-znak zamiast odmawiać
                return Encoding.Latin1.GetString(data);
            }
        }

        /// <summary>Obcina tekst do <see cref="MaxTextLines"/> linii; <paramref name="truncated"/> mówi, czy uciął.</summary>
        public static string LimitLines(string text, out bool truncated, int maxLines = MaxTextLines)
        {
            truncated = false;
            if (string.IsNullOrEmpty(text)) return text ?? "";
            int line = 0, i = 0;
            for (; i < text.Length; i++)
            {
                if (text[i] != '\n') continue;
                if (++line < maxLines) continue;
                truncated = true;
                return text.Substring(0, i);
            }
            return text;
        }

        /// <summary>
        /// Klasyczny zrzut szesnastkowy: offset, 16 bajtów szesnastkowo, ta sama szesnastka jako znaki
        /// (niedrukowalne jako kropka). Format taki sam jak w `xxd`/`hexdump -C`, żeby dało się porównać
        /// wynik z narzędziem wiersza poleceń.
        /// </summary>
        public static string HexDump(byte[] data, int maxBytes = 64 * 1024)
        {
            if (data == null || data.Length == 0) return "";
            int n = Math.Min(data.Length, maxBytes);
            var sb = new StringBuilder(n * 4);
            var ascii = new StringBuilder(16);

            for (int off = 0; off < n; off += 16)
            {
                sb.Append(off.ToString("x8")).Append("  ");
                ascii.Clear();
                for (int i = 0; i < 16; i++)
                {
                    if (off + i < n)
                    {
                        byte b = data[off + i];
                        sb.Append(b.ToString("x2")).Append(' ');
                        ascii.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
                    }
                    else sb.Append("   ");
                    if (i == 7) sb.Append(' ');
                }
                sb.Append(' ').Append(ascii).Append('\n');
            }
            // Znacznik obcięcia bez słów — Core nie zna języka interfejsu (stopka okna opisuje to po ludzku).
            if (data.Length > n) sb.Append("… +").Append(data.Length - n).Append(" B\n");
            return sb.ToString();
        }
    }
}
