using System;
using System.Text;
using RdpManager.Core;
using Xunit;

namespace RdpManager.Tests
{
    // Rozpoznawanie i przygotowanie treści do podglądu pliku. Cała logika jest bez WPF właśnie po to,
    // żeby dało się ją sprawdzić tutaj — okno podglądu tylko rysuje to, co powstaje w FilePreview.
    public class FilePreviewTests
    {
        private static byte[] Utf8(string s) => new UTF8Encoding(false).GetBytes(s);

        [Theory]
        [InlineData("zdjecie.PNG", PreviewKind.Image)]
        [InlineData("a.jpeg", PreviewKind.Image)]
        [InlineData("dane.json", PreviewKind.Json)]
        [InlineData("README.md", PreviewKind.Markdown)]
        [InlineData("skrypt.sh", PreviewKind.Text)]
        [InlineData("bez-rozszerzenia", PreviewKind.Text)]
        public void KindFor_RozpoznajePoRozszerzeniu(string name, PreviewKind expected)
            => Assert.Equal(expected, FilePreview.KindFor(name, Utf8("{\"a\":1}")));

        [Fact]
        public void KindFor_TrescBinarnaWygrywaZRozszerzeniemTekstowym()
        {
            // Plik „.json” z bajtem zerowym to nie JSON — inaczej podgląd próbowałby go tokenizować.
            var data = new byte[] { (byte)'{', 0x00, (byte)'}' };
            Assert.Equal(PreviewKind.Binary, FilePreview.KindFor("dane.json", data));
        }

        [Fact]
        public void KindFor_RozszerzenieObrazuNieJestPrzebijanePrzezSniffing()
        {
            // Obrazy SĄ binarne — gdyby sniffing szedł pierwszy, żaden PNG nie trafiłby do dekodera.
            var png = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00 };
            Assert.Equal(PreviewKind.Image, FilePreview.KindFor("a.png", png));
        }

        [Fact]
        public void LooksBinary_PustyPlikJestTekstem() => Assert.False(FilePreview.LooksBinary(Array.Empty<byte>()));

        [Fact]
        public void LooksBinary_ZwyklyTekstZTabulatoremILamaniemLinii()
            => Assert.False(FilePreview.LooksBinary(Utf8("linia\tjeden\r\nlinia dwa\n")));

        [Fact]
        public void LooksBinary_SekwencjeAnsiWLoguToNadalTekst()
        {
            // Kolorowany log zawiera bajt Escape (0x1B). To nadal tekst — gdyby sniffing go odrzucał,
            // podgląd każdego loga z kolorami kończyłby się zrzutem szesnastkowym.
            var data = new byte[] { 0x1B, (byte)'[', (byte)'3', (byte)'1', (byte)'m',
                                    (byte)'E', (byte)'R', (byte)'R', 0x1B, (byte)'[', (byte)'0', (byte)'m', 0x0A };
            Assert.False(FilePreview.LooksBinary(data));
        }

        [Fact]
        public void LooksBinary_BajtZerowyPrzesadza()
            => Assert.True(FilePreview.LooksBinary(new byte[] { (byte)'a', (byte)'b', 0x00, (byte)'c' }));

        [Fact]
        public void LooksBinary_DuzoZnakowSterujacychBezZer()
        {
            var data = new byte[100];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 4 == 0 ? 0x01 : 'a');
            Assert.True(FilePreview.LooksBinary(data));
        }

        [Fact]
        public void DecodeText_Utf8BezBomZachowujeZnakiDiakrytyczne()
        {
            string text = FilePreview.DecodeText(Utf8("zażółć gęślą jaźń"), out string enc);
            Assert.Equal("zażółć gęślą jaźń", text);
            Assert.Equal("UTF-8", enc);
        }

        [Fact]
        public void DecodeText_BomJestZjadanyANieWidocznyWTresci()
        {
            var withBom = Cat(new byte[] { 0xEF, 0xBB, 0xBF }, Utf8("ok"));
            string text = FilePreview.DecodeText(withBom, out string enc);
            Assert.Equal("ok", text);
            Assert.Contains("BOM", enc);
        }

        [Fact]
        public void DecodeText_Utf16LeRozpoznanePoBom()
        {
            var data = Cat(new byte[] { 0xFF, 0xFE }, Encoding.Unicode.GetBytes("abc"));
            Assert.Equal("abc", FilePreview.DecodeText(data, out string enc));
            Assert.Equal("UTF-16 LE", enc);
        }

        [Fact]
        public void DecodeText_NiepoprawnyUtf8SpadaDoLatin1ZamiastRzucac()
        {
            // 0xB1 to samodzielny bajt kontynuacji — niepoprawny UTF-8. Podgląd ma pokazać cokolwiek
            // i POWIEDZIEĆ, że to Latin-1, zamiast wyświetlić błąd w miejscu treści.
            var data = new byte[] { (byte)'a', 0xB1, (byte)'b' };
            string text = FilePreview.DecodeText(data, out string enc);
            Assert.Equal("Latin-1", enc);
            Assert.Equal(3, text.Length);
        }

        [Fact]
        public void LimitLines_KrotkiTekstNieJestRuszany()
        {
            string text = FilePreview.LimitLines("a\nb\nc", out bool truncated, maxLines: 10);
            Assert.False(truncated);
            Assert.Equal("a\nb\nc", text);
        }

        [Fact]
        public void LimitLines_DlugiTekstJestObcinanyIOznaczany()
        {
            string src = string.Join("\n", System.Linq.Enumerable.Range(1, 100));
            string text = FilePreview.LimitLines(src, out bool truncated, maxLines: 5);
            Assert.True(truncated);
            Assert.Equal(4, text.Split('\n').Length - 1);   // 5 linii = 4 znaki nowej linii
        }

        [Fact]
        public void HexDump_MaFormatJakXxd()
        {
            string dump = FilePreview.HexDump(Utf8("AB"));
            Assert.StartsWith("00000000  41 42", dump);
            Assert.EndsWith("AB\n", dump);
        }

        [Fact]
        public void HexDump_NiedrukowalneJakoKropki()
        {
            string dump = FilePreview.HexDump(new byte[] { 0x00, 0x7F, (byte)'x' });
            Assert.Contains("..x", dump);
        }

        [Fact]
        public void HexDump_ObcinaISygnalizujeIleZostalo()
        {
            string dump = FilePreview.HexDump(new byte[64], maxBytes: 16);
            Assert.Contains("… +48 B", dump);
        }

        private static byte[] Cat(byte[] a, byte[] b)
        {
            var r = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, r, 0, a.Length);
            Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
            return r;
        }
    }
}
