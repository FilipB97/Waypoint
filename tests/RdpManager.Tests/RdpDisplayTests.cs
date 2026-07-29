using RdpManager.Core;
using Xunit;

namespace RdpManager.Tests
{
    public class RdpDisplayTests
    {
        // ---------- rozdzielczość ----------

        [Theory]
        [InlineData("1920x1080", 1920, 1080)]
        [InlineData("1920X1080", 1920, 1080)]
        [InlineData("1920×1080", 1920, 1080)]   // znak „×" z etykiety w UI
        [InlineData(" 1280 x 720 ", 1280, 720)]
        public void ParseResolution_ReadsFixedSize(string tag, int w, int h)
            => Assert.Equal((w, h), RdpDisplay.ParseResolution(tag));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("Auto")]
        [InlineData("1920")]
        [InlineData("1920x")]
        [InlineData("x1080")]
        [InlineData("axb")]
        public void ParseResolution_UnknownMeansFitToWindow(string tag)
            => Assert.Equal((0, 0), RdpDisplay.ParseResolution(tag));

        [Fact]
        public void ParseResolution_NormalizesToEvenAndRange()
        {
            Assert.Equal((1280, 720), RdpDisplay.ParseResolution("1281x721"));   // nieparzyste → parzyste
            Assert.Equal((RdpUtils.MaxDim, RdpUtils.MaxDim), RdpDisplay.ParseResolution("99999x99999"));
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(-1, -1)]
        [InlineData(100, 100)]        // poniżej MinDim (200) — traktujemy jak brak wyboru
        [InlineData(1920, 100)]       // jeden wymiar za mały → też brak wyboru
        public void Normalize_BelowMinimumMeansFitToWindow(int w, int h)
        {
            Assert.Equal((0, 0), RdpDisplay.Normalize(w, h));
            Assert.False(RdpDisplay.IsFixed(w, h));
            Assert.Equal("", RdpDisplay.FormatResolution(w, h));
        }

        [Fact]
        public void FormatResolution_RoundTripsWithParse()
        {
            string tag = RdpDisplay.FormatResolution(1600, 900);
            Assert.Equal("1600x900", tag);
            Assert.Equal((1600, 900), RdpDisplay.ParseResolution(tag));
        }

        // ---------- skalowanie (DPI) ----------

        [Theory]
        [InlineData(null, 0)]
        [InlineData("", 0)]
        [InlineData("Auto", 0)]
        [InlineData("0", 0)]
        [InlineData("-50", 0)]
        [InlineData("100", 100)]
        [InlineData("150", 150)]
        [InlineData("175%", 175)]     // tolerujemy „%" z etykiety
        [InlineData("9000", 500)]     // powyżej MaxScale
        [InlineData("50", 100)]       // poniżej MinScale
        public void ParseScale_ReadsPercentOrAuto(string tag, int expected)
            => Assert.Equal(expected, RdpDisplay.ParseScale(tag));

        [Theory]
        [InlineData(125, 125)]
        [InlineData(200, 200)]
        [InlineData(600, 500)]        // przycięcie do MaxScale
        [InlineData(10, 100)]         // przycięcie do MinScale
        public void EffectiveScale_ConfiguredWins(int configured, int expected)
            => Assert.Equal(expected, RdpDisplay.EffectiveScale(configured, 1.0));

        [Theory]
        [InlineData(1.0, 100)]
        [InlineData(1.25, 125)]
        [InlineData(1.5, 150)]
        [InlineData(1.4999, 150)]     // mnożnik z GetDpi bywa niecałkowity → zaokrąglenie do 5%
        [InlineData(1.75, 175)]
        [InlineData(2.0, 200)]
        [InlineData(0.5, 100)]        // ekran „poniżej 100%" — serwer i tak nie przyjmie < 100
        [InlineData(9.0, 500)]
        public void EffectiveScale_AutoFollowsLocalDpi(double dpiScale, int expected)
            => Assert.Equal(expected, RdpDisplay.EffectiveScale(0, dpiScale));

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void EffectiveScale_AutoWithBogusDpiFallsBackTo100(double dpiScale)
            => Assert.Equal(100, RdpDisplay.EffectiveScale(0, dpiScale));

        [Fact]
        public void DeviceScaleFactor_StaysAtAllowedValue()
        {
            // ulDeviceScaleFactor przyjmuje wyłącznie 100/140/180 — inna wartość unieważnia całe Display-Update.
            Assert.Contains(RdpDisplay.DeviceScaleFactor, new uint[] { 100u, 140u, 180u });
        }
    }
}
