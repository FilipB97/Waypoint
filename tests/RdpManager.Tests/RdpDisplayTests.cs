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
        [InlineData("75", 75)]        // pomniejszenie — poniżej 100% jest dozwolone
        [InlineData("10", 50)]        // poniżej MinScale
        public void ParseScale_ReadsPercentOrAuto(string tag, int expected)
            => Assert.Equal(expected, RdpDisplay.ParseScale(tag));

        [Theory]
        [InlineData(125, 125)]
        [InlineData(200, 200)]
        [InlineData(75, 75)]
        [InlineData(600, 500)]        // przycięcie do MaxScale
        [InlineData(10, 50)]          // przycięcie do MinScale
        public void EffectiveScale_ConfiguredWins(int configured, int expected)
            => Assert.Equal(expected, RdpDisplay.EffectiveScale(configured, 1.0));

        [Theory]
        [InlineData(1.0, 100)]
        [InlineData(1.25, 125)]
        [InlineData(1.5, 150)]
        [InlineData(1.4999, 150)]     // mnożnik z GetDpi bywa niecałkowity → zaokrąglenie do 5%
        [InlineData(1.75, 175)]
        [InlineData(2.0, 200)]
        [InlineData(0.5, 100)]        // „auto" nie schodzi pod 100% — pomniejszenie wybiera się świadomie
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

        // ---------- pomniejszenie (skala < 100%) ----------

        [Theory]
        [InlineData(50, 100u)]        // DPI nie zejdzie poniżej 100% — pomniejsza mnożnik rozdzielczości
        [InlineData(75, 100u)]
        [InlineData(100, 100u)]
        [InlineData(150, 150u)]
        [InlineData(500, 500u)]
        public void DesktopScaleFactor_ClampedToProtocolRange(int scale, uint expected)
            => Assert.Equal(expected, RdpDisplay.DesktopScaleFactor(scale));

        [Theory]
        [InlineData(100, 1.0)]
        [InlineData(150, 1.0)]        // powyżej 100% pracuje DPI, rozdzielczość zostaje 1:1
        [InlineData(0, 1.0)]
        [InlineData(50, 2.0)]
        [InlineData(75, 100.0 / 75.0)]
        [InlineData(80, 1.25)]
        public void ResolutionMultiplier_OnlyGrowsBelow100(int scale, double expected)
            => Assert.Equal(expected, RdpDisplay.ResolutionMultiplier(scale), 6);

        [Theory]
        [InlineData(1920, 1080, 100, 1920, 1080)]
        [InlineData(1920, 1080, 150, 1920, 1080)]   // DPI, nie rozdzielczość
        [InlineData(1920, 1080, 75, 2560, 1440)]
        [InlineData(1920, 1080, 50, 3840, 2160)]
        [InlineData(1280, 720, 80, 1600, 900)]
        public void ScaleResolution_GrowsDesktopBelow100(int w, int h, int scale, int expW, int expH)
            => Assert.Equal((expW, expH), RdpDisplay.ScaleResolution(w, h, scale));

        [Fact]
        public void ScaleResolution_KeepsAspectWhenHittingSessionLimit()
        {
            // 6000×3000 przy 50% chciałoby 12000×6000 — limit to 8192, więc przycinamy MNOŻNIK
            // (oba wymiary o tyle samo), a nie pojedynczy wymiar, żeby nie zmienić proporcji.
            var (w, h) = RdpDisplay.ScaleResolution(6000, 3000, 50);
            Assert.Equal(RdpUtils.MaxDim, w);
            Assert.Equal(RdpUtils.MaxDim / 2, h);
        }

        [Fact]
        public void ScaleResolution_RejectsTooSmallBase()
            => Assert.Equal((0, 0), RdpDisplay.ScaleResolution(100, 100, 75));

        [Fact]
        public void DeviceScaleFactor_StaysAtAllowedValue()
        {
            // ulDeviceScaleFactor przyjmuje wyłącznie 100/140/180 — inna wartość unieważnia całe Display-Update.
            Assert.Contains(RdpDisplay.DeviceScaleFactor, new uint[] { 100u, 140u, 180u });
        }

        // ---------- kiedy w ogóle renegocjować rozdzielczość ----------
        //
        // Każde Display-Update kosztuje widoczne przemalowanie całej sesji, więc warunek „czy teraz"
        // jest tu tak samo ważny jak samo przeliczenie wymiarów.

        [Fact]
        public void ZminimalizowaneOknoNieRenegocjuje()
        {
            // Zgłoszone z użycia: „po zminimalizowaniu i zmaksymalizowaniu za każdym razem jest
            // przerenderowanie połączenia". Przy zminimalizowanym oknie układ przelicza się na wersję
            // bez trybu skupienia (IsImmersive wymaga Maximized), więc panel sesji się zwęża i leci
            // renegocjacja — a po przywróceniu druga, z powrotem. Nikt tego wtedy nie ogląda.
            Assert.False(RdpDisplay.ShouldApplyResize(minimized: true, 1920, 1080, 1280, 720));
        }

        [Fact]
        public void TenSamRozmiarNieRenegocjuje()
        {
            // Serwer już zna te wymiary — wysyłka byłaby samym mignięciem, bez żadnej zmiany.
            Assert.False(RdpDisplay.ShouldApplyResize(minimized: false, 1920, 1080, 1920, 1080));
        }

        [Fact]
        public void ZmianaJednegoWymiaruWystarczyDoRenegocjacji()
        {
            Assert.True(RdpDisplay.ShouldApplyResize(minimized: false, 1920, 1200, 1920, 1080));
            Assert.True(RdpDisplay.ShouldApplyResize(minimized: false, 1680, 1080, 1920, 1080));
        }

        [Fact]
        public void WymiaryPonizejProgiNieRenegocjuja()
        {
            // Chwilowy pomiar w trakcie układania okna (albo panel podziału zwinięty do zera) nie jest
            // rozdzielczością, o którą warto pytać serwer.
            Assert.False(RdpDisplay.ShouldApplyResize(minimized: false, RdpUtils.MinDim - 1, 1080, -1, -1));
            Assert.False(RdpDisplay.ShouldApplyResize(minimized: false, 1920, RdpUtils.MinDim - 1, -1, -1));
            Assert.True(RdpDisplay.ShouldApplyResize(minimized: false, RdpUtils.MinDim, RdpUtils.MinDim, -1, -1));
        }

        [Fact]
        public void PierwszaNegocjacjaPrzechodzi()
        {
            // -1 = nic jeszcze nie wysłano; pierwszy poprawny pomiar musi dojść do serwera.
            Assert.True(RdpDisplay.ShouldApplyResize(minimized: false, 1920, 1080, -1, -1));
        }
    }
}
