using System.Windows.Media;
using RdpManager.Core;
using Xunit;

namespace RdpManager.Tests
{
    /// <summary>
    /// Rachunek kontrastu i wymuszanie progu czytelności. Warstwy wizualnej nie da się sprawdzić inaczej
    /// niż okiem na Windowsie — ta jej część jest czystą matematyką, więc niech pilnuje jej CI.
    /// </summary>
    public class ColorMathTests
    {
        private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

        [Fact]
        public void Contrast_BlackOnWhite_IsMaximum()
            => Assert.Equal(21.0, ColorMath.Contrast(Colors.Black, Colors.White), 1);

        [Fact]
        public void Contrast_SameColor_IsOne()
            => Assert.Equal(1.0, ColorMath.Contrast(C("#6C6DFF"), C("#6C6DFF")), 3);

        [Fact]
        public void Contrast_IsSymmetric()
            => Assert.Equal(ColorMath.Contrast(C("#B1B4C2"), C("#2C2E37")),
                            ColorMath.Contrast(C("#2C2E37"), C("#B1B4C2")), 6);

        // Wartości z palety Waypoint — jeśli ktoś je ruszy, test pokaże, o ile.
        [Theory]
        [InlineData("#E7E8EE", "#2C2E37", 4.5)]   // TextPrim na panelu (ciemny)
        [InlineData("#B1B4C2", "#2C2E37", 4.5)]   // TextSec
        [InlineData("#9396A6", "#2C2E37", 4.5)]   // TextTer
        [InlineData("#1B1D22", "#FAFBFC", 4.5)]   // TextPrim na panelu (jasny)
        [InlineData("#565A62", "#FAFBFC", 4.5)]   // TextSec
        [InlineData("#6B6F78", "#FAFBFC", 4.5)]   // TextTer
        public void PaletteText_ClearsAaThresholdOnPanel(string fg, string bg, double min)
            => Assert.True(ColorMath.Contrast(C(fg), C(bg)) >= min,
                           $"{fg} na {bg} = {ColorMath.Contrast(C(fg), C(bg)):F2}, oczekiwano >= {min}");

        [Fact]
        public void EnsureContrast_AlreadyPassing_ReturnsColorUnchanged()
        {
            Color fg = C("#E7E8EE"), bg = C("#2C2E37");
            Assert.Equal(fg, ColorMath.EnsureContrast(fg, bg, 4.5));
        }

        // Trzeci stopień tekstu z każdego presetu wobec panelu TEGO presetu. Wszystkie wypadały
        // między 2.07 a 3.77, więc każdy z nich musi zostać dociągnięty.
        [Theory]
        [InlineData("#5A5C66", "#17181D")]   // Waypoint ciemny
        [InlineData("#5C6370", "#2F343D")]   // Atom One Dark
        [InlineData("#6E7681", "#161B22")]   // GitHub Dark
        [InlineData("#6F6E66", "#30302E")]   // Claude Dark
        [InlineData("#565F89", "#24283B")]   // Tokyo Night
        [InlineData("#7B88A1", "#3B4252")]   // Nord
        [InlineData("#888B93", "#FFFFFF")]   // Waypoint jasny
        [InlineData("#8C959F", "#F6F8FA")]   // GitHub Light
        [InlineData("#93A1A1", "#EEE8D5")]   // Solarized Light
        [InlineData("#97968B", "#FFFFFF")]   // Claude Light
        [InlineData("#9CA0B0", "#FFFFFF")]   // Catppuccin Latte
        [InlineData("#A0A1A7", "#FFFFFF")]   // One Light
        public void EnsureContrast_PresetTertiaryText_ReachesThreshold(string fg, string panel)
        {
            Color fixed_ = ColorMath.EnsureContrast(C(fg), C(panel), 4.5);
            Assert.True(ColorMath.Contrast(fixed_, C(panel)) >= 4.5,
                        $"{fg} na {panel} po dociągnięciu = {ColorMath.Contrast(fixed_, C(panel)):F2}");
        }

        [Fact]
        public void EnsureContrast_OnDarkPanel_Lightens()
        {
            Color bg = C("#17181D");
            Color fixed_ = ColorMath.EnsureContrast(C("#5A5C66"), bg, 4.5);
            Assert.True(ColorMath.RelativeLuminance(fixed_) > ColorMath.RelativeLuminance(C("#5A5C66")));
        }

        [Fact]
        public void EnsureContrast_OnLightPanel_Darkens()
        {
            Color bg = Colors.White;
            Color fixed_ = ColorMath.EnsureContrast(C("#A0A1A7"), bg, 4.5);
            Assert.True(ColorMath.RelativeLuminance(fixed_) < ColorMath.RelativeLuminance(C("#A0A1A7")));
        }

        [Fact]
        public void EnsureContrast_MakesSmallestChangeThatPasses()
        {
            // Krok wstecz o 1% musi jeszcze NIE przechodzić — inaczej dociągamy za mocno.
            Color fg = C("#5A5C66"), bg = C("#17181D");
            Color result = ColorMath.EnsureContrast(fg, bg, 4.5);
            Assert.True(ColorMath.Contrast(result, bg) >= 4.5);

            for (int i = 1; i <= 100; i++)
            {
                Color step = ColorMath.Mix(fg, Colors.White, i / 100.0);
                if (ColorMath.Contrast(step, bg) >= 4.5)
                {
                    Assert.Equal(step, result);
                    return;
                }
            }
            Assert.Fail("nie znaleziono kroku przechodzącego próg");
        }
    }
}
