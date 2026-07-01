using RdpManager.Core;
using RdpManager.Models;
using Xunit;

namespace RdpManager.Tests
{
    public class RdpUtilsTests
    {
        [Theory]
        [InlineData("prod-web1", "PW")]
        [InlineData("Ten komputer", "TK")]
        [InlineData("db.internal", "DI")]
        [InlineData("localhost", "LO")]
        [InlineData("", "")]
        [InlineData("   ", "")]
        public void MakeInitials_Works(string name, string expected)
        {
            Assert.Equal(expected, RdpUtils.MakeInitials(name));
        }

        [Theory]
        [InlineData(100, 200)]   // poniżej minimum
        [InlineData(199, 200)]
        [InlineData(200, 200)]
        [InlineData(801, 800)]   // wyrównanie do parzystej
        [InlineData(1920, 1920)]
        [InlineData(8193, 8192)] // powyżej maksimum
        public void NormalizeDim_ClampsAndMakesEven(int input, int expected)
        {
            int result = RdpUtils.NormalizeDim(input);
            Assert.Equal(expected, result);
            Assert.True(result % 2 == 0);
            Assert.InRange(result, RdpUtils.MinDim, RdpUtils.MaxDim);
        }

        [Fact]
        public void MatchesFilter_MatchesNameOrHostCaseInsensitive()
        {
            var s = new ServerInfo { Name = "prod-web1", Host = "192.0.2.10" };

            Assert.True(RdpUtils.MatchesFilter(s, ""));      // pusty filtr = wszystko
            Assert.True(RdpUtils.MatchesFilter(s, "  "));
            Assert.True(RdpUtils.MatchesFilter(s, "web"));   // po nazwie
            Assert.True(RdpUtils.MatchesFilter(s, "WEB"));   // bez uwzględniania wielkości liter
            Assert.True(RdpUtils.MatchesFilter(s, "192"));   // po hoście
            Assert.False(RdpUtils.MatchesFilter(s, "zzz"));
            Assert.False(RdpUtils.MatchesFilter(null, "web"));
        }

        [Theory]
        [InlineData("16", 16)]
        [InlineData("24", 24)]
        [InlineData("32", 32)]
        [InlineData(" 24 ", 24)]
        [InlineData("999", 32)]   // niedozwolone -> fallback
        [InlineData("abc", 32)]
        [InlineData(null, 32)]
        public void ParseColorDepth_ValidatesAllowedValues(string text, int expected)
        {
            Assert.Equal(expected, RdpUtils.ParseColorDepth(text));
        }

        [Theory]
        [InlineData("Upłynął limit czasu.", 264, 3, "Upłynął limit czasu (kod 264/3)")]
        [InlineData(null, 0, 0, "rozłączono (kod 0/0)")]
        [InlineData("   ", 1, 2, "rozłączono (kod 1/2)")]
        public void FormatDisconnectReason_Formats(string desc, int reason, long ext, string expected)
        {
            Assert.Equal(expected, RdpUtils.FormatDisconnectReason(desc, reason, ext));
        }
    }
}
