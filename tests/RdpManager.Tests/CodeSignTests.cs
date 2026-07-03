using RdpManager.Core;
using Xunit;

namespace RdpManager.Tests
{
    public class CodeSignTests
    {
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
    }
}
