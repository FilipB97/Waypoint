using System;
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

        /// <summary>Zbiór znaków dla wybranych klas (po ewentualnym wykluczeniu mylących).</summary>
        public static string BuildPool(bool upper, bool lower, bool digits, bool symbols, bool excludeAmbiguous)
        {
            var sb = new StringBuilder();
            if (upper) sb.Append(Upper);
            if (lower) sb.Append(Lower);
            if (digits) sb.Append(Digits);
            if (symbols) sb.Append(Symbols);
            var pool = sb.ToString();
            if (excludeAmbiguous)
                pool = new string(pool.Where(c => Ambiguous.IndexOf(c) < 0).ToArray());
            return pool;
        }

        public static string GeneratePassword(int length, bool upper, bool lower, bool digits, bool symbols,
                                               bool excludeAmbiguous)
        {
            string pool = BuildPool(upper, lower, digits, symbols, excludeAmbiguous);
            if (length <= 0 || pool.Length == 0) return "";
            var sb = new StringBuilder(length);
            for (int i = 0; i < length; i++)
                sb.Append(pool[RandomNumberGenerator.GetInt32(pool.Length)]);
            return sb.ToString();
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
