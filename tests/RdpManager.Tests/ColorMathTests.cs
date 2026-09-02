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

        // ----- wybór inkaustu na kolorowym tle (awatary, przyciski w kolorze akcentu) -----

        // Akcenty, na których BIEL ginie — reguła musi przełączyć na ciemny inkaust.
        [Theory]
        [InlineData("#88C0D0")]   // Nord: biel 2.00
        [InlineData("#61AFEF")]   // Atom One Dark: 2.36
        [InlineData("#58A6FF")]   // GitHub Dark: 2.53
        [InlineData("#D97757")]   // Claude Dark: 3.12
        [InlineData("#E0872E")]   // próbnik „bursztyn": 2.74
        [InlineData("#22B07D")]   // próbnik „zieleń": 2.77
        [InlineData("#FFB454")]   // kolor grupy „staging": 1.76
        public void PrefersDarkInk_OnLightBackgrounds_ChoosesDark(string hex)
            => Assert.True(ColorMath.PrefersDarkInk(C(hex)));

        // Akcenty, na których biel jest wystarczająca — próg ma jej NIE ruszać, żeby domyślny motyw
        // nie zmienił wyglądu przy różnicy nie do zauważenia (indygo: ciemny 4.52 vs biel 3.99).
        [Theory]
        [InlineData("#6C6DFF")]   // akcent Waypointa, ciemny
        [InlineData("#5B4BD6")]   // akcent Waypointa, jasny
        [InlineData("#2F6BE0")]   // próbnik „kobalt"
        [InlineData("#1E66F5")]   // Catppuccin Latte
        public void PrefersDarkInk_WhenWhiteIsAdequate_KeepsWhite(string hex)
            => Assert.False(ColorMath.PrefersDarkInk(C(hex)));

        [Fact]
        public void PrefersDarkInk_ChosenInk_AlwaysBeatsWhiteOnLightAccents()
        {
            // Sedno reguły: po wyborze inkaustu napis ma być czytelniejszy, niż byłby na samej bieli.
            foreach (string hex in new[] { "#88C0D0", "#61AFEF", "#D97757", "#E0872E", "#FFB454" })
            {
                Color bg = C(hex);
                Color ink = ColorMath.PrefersDarkInk(bg) ? ColorMath.InkDark : Colors.White;
                Assert.True(ColorMath.Contrast(bg, ink) > ColorMath.Contrast(bg, Colors.White),
                            $"{hex}: wybrany inkaust {ColorMath.Contrast(bg, ink):F2} nie bije bieli {ColorMath.Contrast(bg, Colors.White):F2}");
            }
        }

        [Fact]
        public void EnsureContrast_AlreadyPassing_ReturnsColorUnchanged()
        {
            Color fg = C("#E7E8EE"), bg = C("#2C2E37");
            Assert.Equal(fg, ColorMath.EnsureContrast(fg, bg, 4.5));
        }

        // Trzeci stopień tekstu z każdego presetu wobec panelu TEGO presetu. Wszystkie wypadały
        // między 2.07 a 3.77, więc każdy z nich musi zostać dociągnięty.
        [Theory]
        [InlineData("#5A5C66", "#282A36")]   // Waypoint ciemny
        [InlineData("#5C6370", "#2F343D")]   // Atom One Dark
        [InlineData("#6E7681", "#161B22")]   // GitHub Dark
        [InlineData("#6F6E66", "#30302E")]   // Claude Dark
        [InlineData("#565F89", "#24283B")]   // Tokyo Night
        [InlineData("#7B88A1", "#3B4252")]   // Nord
        [InlineData("#888B93", "#FAFBFC")]   // Waypoint jasny
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
