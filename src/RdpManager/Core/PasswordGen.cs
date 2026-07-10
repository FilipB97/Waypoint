using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace RdpManager.Core
{
    /// <summary>
    /// Generator sekretów: hasła (wybór klas znaków), tokeny hex, GUID.
    /// Losowość z <see cref="RandomNumberGenerator"/> (krypto), bez modulo-bias
    /// (GetInt32). Czysta logika — jednostkowo testowalna.
    /// </summary>
    public static class PasswordGen
    {
        public const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        public const string Lower = "abcdefghijklmnopqrstuvwxyz";
        public const string Digits = "0123456789";
        public const string Symbols = "!@#$%^&*()-_=+[]{};:,.?/";
        // Znaki mylące wizualnie (O/0, I/l/1, itp.) — do opcjonalnego wykluczenia.
        public const string Ambiguous = "O0oIl1|`'\"{}[]()/\\;:.,";

        // Odfiltrowuje znaki mylące z jednej klasy (gdy włączone wykluczenie).
        private static string Filter(string set, bool excludeAmbiguous)
            => excludeAmbiguous ? new string(set.Where(c => Ambiguous.IndexOf(c) < 0).ToArray()) : set;

        /// <summary>Zbiór znaków dla wybranych klas (po ewentualnym wykluczeniu mylących).</summary>
        public static string BuildPool(bool upper, bool lower, bool digits, bool symbols, bool excludeAmbiguous)
        {
            var sb = new StringBuilder();
            if (upper) sb.Append(Filter(Upper, excludeAmbiguous));
            if (lower) sb.Append(Filter(Lower, excludeAmbiguous));
            if (digits) sb.Append(Filter(Digits, excludeAmbiguous));
            if (symbols) sb.Append(Filter(Symbols, excludeAmbiguous));
            return sb.ToString();
        }

        public static string GeneratePassword(int length, bool upper, bool lower, bool digits, bool symbols,
                                               bool excludeAmbiguous)
        {
            string pool = BuildPool(upper, lower, digits, symbols, excludeAmbiguous);
            if (length <= 0 || pool.Length == 0) return "";

            // Gwarancja: po ≥1 znaku z KAŻDEJ wybranej klasy (o ile długość na to pozwala) — inaczej
            // wygenerowane hasło mogło nie spełnić polityki złożoności serwera. Reszta z pełnej puli, na końcu tasujemy.
            var classes = new List<string>();
            if (upper) classes.Add(Filter(Upper, excludeAmbiguous));
            if (lower) classes.Add(Filter(Lower, excludeAmbiguous));
            if (digits) classes.Add(Filter(Digits, excludeAmbiguous));
            if (symbols) classes.Add(Filter(Symbols, excludeAmbiguous));
            classes.RemoveAll(s => s.Length == 0);

            var chars = new char[length];
            int p = 0;
            foreach (var cls in classes)
                if (p < length) chars[p++] = cls[RandomNumberGenerator.GetInt32(cls.Length)];
            for (; p < length; p++)
                chars[p] = pool[RandomNumberGenerator.GetInt32(pool.Length)];

            // Fisher-Yates (krypto-RNG) — rozrzuć gwarantowane znaki po całej długości.
            for (int i = length - 1; i > 0; i--)
            {
                int j = RandomNumberGenerator.GetInt32(i + 1);
                (chars[i], chars[j]) = (chars[j], chars[i]);
            }
            return new string(chars);
        }

        /// <summary>Token hex (małe litery), <paramref name="byteCount"/> bajtów losowych → 2×hex znaków.</summary>
        public static string GenerateHexToken(int byteCount)
        {
            if (byteCount <= 0) return "";
            var bytes = new byte[byteCount];
            RandomNumberGenerator.Fill(bytes);
            var sb = new StringBuilder(byteCount * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static string GenerateGuid() => Guid.NewGuid().ToString();

        /// <summary>Przybliżona entropia hasła w bitach: length · log2(rozmiar puli).</summary>
        public static double EntropyBits(int length, int poolSize)
        {
            if (length <= 0 || poolSize <= 1) return 0;
            return length * Math.Log(poolSize, 2);
        }
    }
}
