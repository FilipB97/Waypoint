using System;
using System.Text;

namespace RdpManager.Core
{
    public enum LineEnding { LF, CRLF, CR }

    /// <summary>
    /// Format pliku tekstowego otwartego w edytorze: kodowanie i końce linii TAKIE, JAKIE BYŁY na dysku.
    ///
    /// Edytor dostaje tekst z samymi „\n" i zapis odtwarza z niego dokładnie format oryginału. Bez tego
    /// dopisanie jednej linii do pliku z Linuksa zamieniałoby w nim wszystkie końce linii na CRLF
    /// (skrypt z „\r" na końcu shebangu przestaje się uruchamiać: „bad interpreter"), a plik w Latin-1
    /// po cichu stawałby się UTF-8. Zapis zmienia więc wyłącznie to, co użytkownik zmienił w treści.
    /// </summary>
    public sealed class TextFileFormat
    {
        /// <summary>Etykieta kodowania — te same co w podglądzie (<see cref="FilePreview.DecodeText"/>).</summary>
        public string EncodingName { get; private set; }
        /// <summary>Przeważający koniec linii; plik bez żadnego końca linii liczy się jako LF.</summary>
        public LineEnding Eol { get; private set; }
        /// <summary>Plik miał różne końce linii — zapis ujednolici je do <see cref="Eol"/>.</summary>
        public bool MixedEol { get; private set; }

        public const string Utf8 = "UTF-8", Utf8Bom = "UTF-8 (BOM)", Utf16Le = "UTF-16 LE", Utf16Be = "UTF-16 BE", Latin1 = "Latin-1";

        /// <summary>
        /// Dekoduje bajty i zapamiętuje ich format. Zwrócony tekst ma końce linii znormalizowane do „\n"
        /// (tak go dostaje edytor), a <see cref="Encode"/> odtwarza z niego format pliku.
        /// </summary>
        public static TextFileFormat Detect(byte[] data, out string text)
        {
            string raw = FilePreview.DecodeText(data ?? Array.Empty<byte>(), out string enc);
            int lf = 0, crlf = 0, cr = 0;
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c == '\r')
                {
                    if (i + 1 < raw.Length && raw[i + 1] == '\n') { crlf++; i++; }
                    else cr++;
                }
                else if (c == '\n') lf++;
            }

            // Remis rozstrzyga LF — edytujemy głównie pliki z serwerów linuksowych.
            var eol = LineEnding.LF;
            if (crlf > lf && crlf >= cr) eol = LineEnding.CRLF;
            else if (cr > lf && cr > crlf) eol = LineEnding.CR;
            int kinds = (lf > 0 ? 1 : 0) + (crlf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);

            text = NormalizeToLf(raw);
            return new TextFileFormat { EncodingName = enc, Eol = eol, MixedEol = kinds > 1 };
        }

        public static string NormalizeToLf(string s)
            => string.IsNullOrEmpty(s) ? s ?? "" : s.Replace("\r\n", "\n").Replace('\r', '\n');

        public string EolLabel => Eol == LineEnding.CRLF ? "CRLF" : Eol == LineEnding.CR ? "CR" : "LF";

        /// <summary>
        /// Koduje tekst z edytora z powrotem do formatu pliku. Zwraca false, gdy znaku nie da się zapisać
        /// w kodowaniu oryginału (Latin-1 zna tylko 256 znaków; „ł" czy emoji by przepadły, a koder .NET
        /// po cichu wstawiłby w ich miejsce „?") — wtedy <paramref name="badChar"/> mówi, który znak,
        /// a wołający proponuje zapis jako UTF-8.
        /// </summary>
        public bool TryEncode(string text, out byte[] bytes, out string badChar)
        {
            bytes = null;
            badChar = null;
            string s = NormalizeToLf(text);
            if (Eol == LineEnding.CRLF) s = s.Replace("\n", "\r\n");
            else if (Eol == LineEnding.CR) s = s.Replace('\n', '\r');

            switch (EncodingName)
            {
                case Latin1:
                    for (int i = 0; i < s.Length; i++)
                    {
                        if (s[i] <= 'ÿ') continue;
                        badChar = char.IsHighSurrogate(s[i]) && i + 1 < s.Length ? s.Substring(i, 2) : s[i].ToString();
                        return false;
                    }
                    bytes = Encoding.Latin1.GetBytes(s);
                    return true;
                case Utf16Le: bytes = WithBom(new UnicodeEncoding(false, true), s); return true;
                case Utf16Be: bytes = WithBom(new UnicodeEncoding(true, true), s); return true;
                case Utf8Bom: bytes = WithBom(new UTF8Encoding(true), s); return true;
                default: bytes = new UTF8Encoding(false).GetBytes(s); return true;
            }
        }

        /// <summary>Ten sam format, ale w UTF-8 (bez BOM) — wyjście, gdy tekst nie mieści się w Latin-1.</summary>
        public TextFileFormat AsUtf8() => new TextFileFormat { EncodingName = Utf8, Eol = Eol, MixedEol = MixedEol };

        private static byte[] WithBom(Encoding e, string s)
        {
            byte[] pre = e.GetPreamble(), body = e.GetBytes(s);
            var all = new byte[pre.Length + body.Length];
            Buffer.BlockCopy(pre, 0, all, 0, pre.Length);
            Buffer.BlockCopy(body, 0, all, pre.Length, body.Length);
            return all;
        }

        public static bool SameBytes(byte[] a, byte[] b)
            => a != null && b != null && a.AsSpan().SequenceEqual(b);
    }
}
