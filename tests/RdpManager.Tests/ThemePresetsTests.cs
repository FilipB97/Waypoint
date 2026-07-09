using System.Linq;
using RdpManager;
using Xunit;

namespace RdpManager.Tests
{
    // Presety motywu (Compass §4.9) — dane w kodzie; Find zwraca null dla domyślnego „Waypoint".
    public class ThemePresetsTests
    {
        [Fact]
        public void For_ReturnsOnlyMatchingMode_AndIncludesWaypoint()
        {
            Assert.All(ThemePresets.For(false), p => Assert.False(p.Light));
            Assert.All(ThemePresets.For(true), p => Assert.True(p.Light));
            Assert.Contains(ThemePresets.For(false), p => p.Id == "Waypoint");
            Assert.Contains(ThemePresets.For(true), p => p.Id == "Waypoint");
            Assert.Contains(ThemePresets.For(false), p => p.Id == "Nord");
        }

        [Theory]
        [InlineData("", false)]           // pusty = domyślny
        [InlineData("Waypoint", false)]   // Waypoint = baza, brak nakładki
        [InlineData("Waypoint", true)]
        [InlineData(null, true)]
        public void Find_DefaultOrEmpty_ReturnsNull(string id, bool light)
            => Assert.Null(ThemePresets.Find(id, light));

        [Fact]
        public void Find_MatchesByIdAndMode()
        {
            var nord = ThemePresets.Find("Nord", false);
            Assert.NotNull(nord);
            Assert.False(nord.Light);
            Assert.Null(ThemePresets.Find("Nord", true));   // Nord jest tylko ciemny
            Assert.NotNull(ThemePresets.Find("GitHubLight", true));
        }

        [Fact]
        public void AllPresets_HaveIdAndName()
        {
            Assert.All(ThemePresets.All, p =>
            {
                Assert.False(string.IsNullOrWhiteSpace(p.Id));
                Assert.False(string.IsNullOrWhiteSpace(p.Name));
            });
        }
    }
}
