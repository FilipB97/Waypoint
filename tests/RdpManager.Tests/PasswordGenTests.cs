using System;
using System.Linq;
using RdpManager.Core;
using Xunit;

namespace RdpManager.Tests
{
    public class PasswordGenTests
    {
        [Fact]
        public void BuildPool_IncludesOnlySelectedClasses()
        {
            Assert.Equal(PasswordGen.Digits, PasswordGen.BuildPool(false, false, true, false, false));
            var ul = PasswordGen.BuildPool(true, true, false, false, false);
            Assert.Contains('A', ul); Assert.Contains('z', ul);
            Assert.DoesNotContain('5', ul); Assert.DoesNotContain('!', ul);
        }

        [Fact]
        public void BuildPool_ExcludeAmbiguous_DropsConfusingChars()
        {
            var pool = PasswordGen.BuildPool(true, true, true, false, excludeAmbiguous: true);
            foreach (var c in "O0Il1") Assert.DoesNotContain(c, pool);
            Assert.Contains('A', pool);   // niekonfundujące zostają
        }

        [Theory]
        [InlineData(16)]
        [InlineData(1)]
        [InlineData(64)]
        public void GeneratePassword_HasRequestedLengthAndOnlyPoolChars(int len)
        {
            var pool = PasswordGen.BuildPool(true, true, true, true, false);
            var pw = PasswordGen.GeneratePassword(len, true, true, true, true, false);
            Assert.Equal(len, pw.Length);
            Assert.All(pw, c => Assert.Contains(c, pool));
        }

        [Fact]
        public void GeneratePassword_EmptyWhenNoClassesOrZeroLength()
        {
            Assert.Equal("", PasswordGen.GeneratePassword(16, false, false, false, false, false));
            Assert.Equal("", PasswordGen.GeneratePassword(0, true, true, true, true, false));
        }

        [Fact]
        public void GeneratePassword_IsRandom_NotConstant()
        {
            var a = PasswordGen.GeneratePassword(24, true, true, true, true, false);
            var b = PasswordGen.GeneratePassword(24, true, true, true, true, false);
            Assert.NotEqual(a, b);   // kolizja 62^24 pomijalna
        }

        [Theory]
        [InlineData(16, 32)]
        [InlineData(1, 2)]
        public void GenerateHexToken_HasDoubleLengthAndIsHex(int bytes, int expectedLen)
        {
            var t = PasswordGen.GenerateHexToken(bytes);
            Assert.Equal(expectedLen, t.Length);
            Assert.All(t, c => Assert.True(Uri.IsHexDigit(c)));
        }

        [Fact]
        public void GenerateGuid_ParsesAsGuid()
        {
            Assert.True(System.Guid.TryParse(PasswordGen.GenerateGuid(), out _));
        }

        [Fact]
        public void EntropyBits_MatchesFormula()
        {
            // 16 znaków z puli 64 → 96 bitów
            Assert.Equal(96.0, PasswordGen.EntropyBits(16, 64), 3);
            Assert.Equal(0.0, PasswordGen.EntropyBits(0, 64), 3);
            Assert.Equal(0.0, PasswordGen.EntropyBits(16, 1), 3);
        }
    }
}
