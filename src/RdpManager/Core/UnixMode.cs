using System;
using System.Globalization;
using System.Text;

namespace RdpManager.Core
{
    /// <summary>
    /// Bity uprawnień uniksowych (st_mode &amp; 07777). SSH.NET wystawia je jako kilkanaście osobnych
    /// flag, a do przywrócenia uprawnień po zapisie potrzebna jest jedna liczba — stąd ta klasa.
    ///
    /// UWAGA: stałe są SZESNASTKOWE, nie „ósemkowe z zerem na początku". C# NIE MA literałów ósemkowych —
    /// „0400" w C# to dziesiętne 400, a nie ósemkowe 0400 (= 256). Pierwsza wersja tej klasy tak właśnie
    /// wyglądała i dla pliku rwxr-xr-x liczyła dziesiętne 755 zamiast 0x1ED; wyłapała to dopiero próba
    /// zapisu na prawdziwym serwerze OpenSSH. Z tego samego powodu testy porównują przez
    /// Convert.ToInt32("755", 8), a nie przez 0755 — tamto przeszłoby także z błędnym kodem.
    /// </summary>
    public static class UnixMode
    {
        private const int SetUid = 0x800, SetGid = 0x400, Sticky = 0x200;      // 04000, 02000, 01000
        public const int UR = 0x100, UW = 0x80, UX = 0x40;                         // 0400, 0200, 0100
        public const int GR = 0x20, GW = 0x10, GX = 0x8;                           // 0040, 0020, 0010
        public const int OR = 0x4, OW = 0x2, OX = 0x1;                             // 0004, 0002, 0001
        private const int All = 0xFFF;                                             // 07777

        public static int Compose(bool setUid, bool setGid, bool sticky,
                                  bool ur, bool uw, bool ux,
                                  bool gr, bool gw, bool gx,
                                  bool or, bool ow, bool ox)
        {
            int m = 0;
            if (setUid) m |= SetUid; if (setGid) m |= SetGid; if (sticky) m |= Sticky;
            if (ur) m |= UR; if (uw) m |= UW; if (ux) m |= UX;
            if (gr) m |= GR; if (gw) m |= GW; if (gx) m |= GX;
            if (or) m |= OR; if (ow) m |= OW; if (ox) m |= OX;
            return m;
        }

        /// <summary>„0755", „4755" — tak, jak podaje się je chmod.</summary>
        public static string Octal(int mode) => Convert.ToString(mode & All, 8).PadLeft(4, '0');

        /// <summary>
        /// Postać, jakiej chce SSH.NET w ChangePermissions / SetPermissions: ósemkowe CYFRY zapisane jako
        /// liczba dziesiętna (0x1ED → 755). Dokumentacja: „The permission mode as an octal number
        /// (e.g., 755, 644, 1777)". Podanie tam prawdziwej wartości bitowej kończy się
        /// ArgumentOutOfRangeException albo — gorzej — cichym ustawieniem innych uprawnień.
        /// </summary>
        public static short ForSshNet(int mode)
            => short.Parse(Convert.ToString(mode & All, 8), NumberStyles.None, CultureInfo.InvariantCulture);

        /// <summary>„rwxr-xr-x" (z s/S i t/T dla bitów specjalnych), jak w ls -l.</summary>
        public static string Symbolic(int mode)
        {
            var sb = new StringBuilder(9);
            Triplet(sb, mode >> 6, (mode & SetUid) != 0, 's');
            Triplet(sb, mode >> 3, (mode & SetGid) != 0, 's');
            Triplet(sb, mode, (mode & Sticky) != 0, 't');
            return sb.ToString();
        }

        private static void Triplet(StringBuilder sb, int bits, bool special, char specialChar)
        {
            sb.Append((bits & 4) != 0 ? 'r' : '-');
            sb.Append((bits & 2) != 0 ? 'w' : '-');
            bool x = (bits & 1) != 0;
            sb.Append(special ? (x ? specialChar : char.ToUpperInvariant(specialChar)) : (x ? 'x' : '-'));
        }
    }
}
