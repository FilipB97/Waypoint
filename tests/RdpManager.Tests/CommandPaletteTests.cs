using RdpManager.Core;
using Xunit;

namespace RdpManager.Tests
{
    public class CommandPaletteTests
    {
        [Fact]
        public void Score_QualityOrder_ExactPrefixWordBoundarySubstring()
        {
            int exact = CommandPalette.Score("prod", "prod");
            int prefix = CommandPalette.Score("prod-server", "prod");
            int boundary = CommandPalette.Score("web prod", "prod");   // start po spacji
            int substr = CommandPalette.Score("webprodx", "prod");     // podciąg w środku słowa
            Assert.True(exact > prefix, $"{exact} > {prefix}");
            Assert.True(prefix > boundary, $"{prefix} > {boundary}");
            Assert.True(boundary > substr, $"{boundary} > {substr}");
            Assert.True(substr > 0);
        }

        [Fact]
        public void Score_NoMatch_IsNegative()
            => Assert.True(CommandPalette.Score("dashboard", "xyz") < 0);

        [Fact]
        public void Score_IsCaseInsensitive()
            => Assert.Equal(CommandPalette.Score("Dashboard", "dash"), CommandPalette.Score("dashboard", "DASH"));

        [Fact]
        public void Score_EmptyQuery_IsNeutral()
        {
            Assert.Equal(0, CommandPalette.Score("anything", ""));
            Assert.Equal(0, CommandPalette.Score("anything", "   "));
        }

        [Fact]
        public void Score_ShorterHaystack_RanksHigher_ForSameTier()
            => Assert.True(CommandPalette.Score("prod", "pro") > CommandPalette.Score("prodxxxx", "pro"));

        [Fact]
        public void Score_EarlierMatch_RanksHigher()
            => Assert.True(CommandPalette.Score("xprod", "prod") > CommandPalette.Score("xxxxprod", "prod"));

        [Fact]
        public void Score_NullSafe()
        {
            Assert.True(CommandPalette.Score(null, "x") < 0);
            Assert.Equal(0, CommandPalette.Score(null, null));   // puste query
        }
    }
}
