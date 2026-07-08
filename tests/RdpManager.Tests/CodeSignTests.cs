using System;
using System.IO;
using RdpManager.Core;
using Xunit;

namespace RdpManager.Tests
{
    public class CodeSignTests
    {
        // Plik systemowy Windows, zawsze obecny i podpisany przez Microsoft — stabilna fikstura
        // do testów WinVerifyTrust (nie zależy od naszego własnego, jeszcze niepodpisanego builda).
        private static string SystemSignedFile()
            => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "kernel32.dll");

        private const string ThumbA = "AABBCCDDEEFF00112233445566778899AABBCCDD";
        private const string ThumbB = "0123456789ABCDEF0123456789ABCDEF01234567";

        [Fact]
        public void SamePublisher_IsMatch_AndAcceptable()
        {
            var v = CodeSign.Compare(ThumbA, ThumbA);
            Assert.Equal(CodeSign.Verdict.Match, v);
            Assert.True(CodeSign.IsAcceptable(v));
        }

        [Fact]
        public void ThumbprintComparison_IsCaseInsensitive()
        {
            var v = CodeSign.Compare(ThumbA.ToLowerInvariant(), ThumbA.ToUpperInvariant());
            Assert.Equal(CodeSign.Verdict.Match, v);
        }

        [Fact]
        public void DifferentPublisher_IsMismatch_AndRejected()
        {
            var v = CodeSign.Compare(ThumbA, ThumbB);
            Assert.Equal(CodeSign.Verdict.Mismatch, v);
            Assert.False(CodeSign.IsAcceptable(v));
        }

        [Fact]
        public void SignedCurrent_UnsignedDownload_IsRejected()
        {
            var v = CodeSign.Compare(ThumbA, null);
            Assert.Equal(CodeSign.Verdict.DownloadUnsigned, v);
            Assert.False(CodeSign.IsAcceptable(v));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void UnsignedCurrent_PassesThrough_ForTransition(string running)
        {
            // Bieżąca aplikacja niepodpisana (np. przejście ze starego, jeszcze niepodpisanego
            // wydania na pierwsze podpisane) — nie mamy odcisku do przypięcia, więc przepuszczamy.
            var v = CodeSign.Compare(running, ThumbA);
            Assert.Equal(CodeSign.Verdict.CurrentUnsigned, v);
            Assert.True(CodeSign.IsAcceptable(v));
        }

        [Fact]
        public void UnsignedFile_HasNullThumbprint()
        {
            // Ten plik testowy nie jest podpisany Authenticode → brak odcisku (bez wyjątku).
            Assert.Null(CodeSign.SignerThumbprint(typeof(CodeSignTests).Assembly.Location));
        }

        [Fact]
        public void MissingFile_HasNullThumbprint()
        {
            Assert.Null(CodeSign.SignerThumbprint(@"C:\nope\does-not-exist.exe"));
        }

        [Fact]
        public void HasValidSignature_SystemSignedFile_ReturnsTrue()
        {
            Assert.True(CodeSign.HasValidSignature(SystemSignedFile()));
        }

        [Fact]
        public void HasValidSignature_UnsignedFile_ReturnsFalse()
        {
            // Assembly testów samo nie jest podpisane Authenticode (jak w UnsignedFile_HasNullThumbprint).
            Assert.False(CodeSign.HasValidSignature(typeof(CodeSignTests).Assembly.Location));
        }

        [Fact]
        public void HasValidSignature_MissingFile_ReturnsFalse()
        {
            Assert.False(CodeSign.HasValidSignature(@"C:\nope\does-not-exist.exe"));
        }

        [Fact]
        public void VerifyPublisher_TamperedButStillCertified_ReturnsDownloadTampered()
        {
            // Kopia podpisanego pliku systemowego ze zmienionym bajtem W ŚRODKU treści (nie w tabeli
            // certyfikatu na końcu pliku) — SignerThumbprint nadal wyciągnie certyfikat (jest nietknięty),
            // ale WinVerifyTrust wykryje, że podpis już nie pokrywa (zmienionej) treści pliku.
            string src = SystemSignedFile();
            string tampered = Path.Combine(Path.GetTempPath(), "waypoint-tampered-" + Guid.NewGuid().ToString("N") + ".dll");
            File.Copy(src, tampered);
            try
            {
                using (var fs = new FileStream(tampered, FileMode.Open, FileAccess.ReadWrite))
                {
                    long mid = fs.Length / 2;
                    fs.Seek(mid, SeekOrigin.Begin);
                    int b = fs.ReadByte();
                    fs.Seek(mid, SeekOrigin.Begin);
                    fs.WriteByte((byte)(b ^ 0xFF));
                }

                Assert.NotNull(CodeSign.SignerThumbprint(tampered));   // certyfikat nadal "obecny"
                Assert.False(CodeSign.HasValidSignature(tampered));    // ale sygnatura już nie pasuje

                var verdict = CodeSign.VerifyPublisher(tampered, src);
                Assert.Equal(CodeSign.Verdict.DownloadTampered, verdict);
                Assert.False(CodeSign.IsAcceptable(verdict));
            }
            finally { try { File.Delete(tampered); } catch { } }
        }
    }
}
