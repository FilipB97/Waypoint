using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;
using RdpManager.Core;
using Xunit;

namespace RdpManager.Tests
{
    // Niezmienniki palety czytane WPROST z plików Themes/Palette.*.xaml, a nie z kopii wartości w teście —
    // kopia rozjeżdża się z paletą po pierwszej zmianie i przestaje cokolwiek pilnować.
    //
    // Powód istnienia: pędzle POWIERZCHNI (Panel) były półprzezroczystą bielą, więc dwa razy nałożone
    // sumowały się. Panel plików malował Panel dwukrotnie i wychodził o całą warstwę jaśniejszy niż
    // reszta okna (#424247 zamiast #2C2D32), a każda warstwa bieli dodatkowo wypłukiwała odcień
    // (nasycenie 14,9% → 6,4% → 3,6%). Nieprzezroczysta powierzchnia czyni nakładanie bezczynnym.
    public class PaletteSurfaceTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RdpManager.sln")))
                dir = dir.Parent;
            Assert.True(dir != null, "Nie znaleziono katalogu repozytorium (RdpManager.sln) powyżej " + AppContext.BaseDirectory);
            return dir.FullName;
        }

        private static string PaletteText(bool light)
            => File.ReadAllText(Path.Combine(RepoRoot(), "src", "RdpManager", "Themes",
                                             light ? "Palette.Light.xaml" : "Palette.Dark.xaml"));

        private static string RawColor(string xaml, string key)
        {
            var m = Regex.Match(xaml, "x:Key=\"" + Regex.Escape(key) + "\"\\s+Color=\"(#[0-9A-Fa-f]+)\"");
            Assert.True(m.Success, "Brak klucza " + key + " w palecie");
            return m.Groups[1].Value;
        }

        private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Panel_JestNieprzezroczysty(bool light)
        {
            // 7 znaków = "#RRGGBB". Zapis ośmioznakowy ("#AARRGGBB") oznaczałby powrót do nakładki,
            // która sumuje się przy zagnieżdżeniu i odbarwia powierzchnię.
            string raw = RawColor(PaletteText(light), "Panel");
            Assert.Equal(7, raw.Length);
            Assert.Equal(255, Parse(raw).A);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Elevated_ZostajePrzezroczysty(bool light)
        {
            // Uniesienie (hover, powierzchnie wewnętrzne) MA być względne wobec podłoża — karty, szyny
            // i paska kart. Kolor nieprzezroczysty wyglądałby poprawnie tylko nad jednym z nich.
            Assert.Equal(9, RawColor(PaletteText(light), "Elevated").Length);
        }

        [Theory]
        [InlineData(false, 4.5)]
        [InlineData(true, 4.5)]
        public void TrzyStopnieTekstuNaPanelu_PrzechodzaProg(bool light, double threshold)
        {
            // Panel jest gorszym przypadkiem niż kanwa (leży bliżej koloru tekstu), a większość tekstu
            // w aplikacji leży właśnie na nim. Ten test pilnuje progu przy KAŻDEJ zmianie palety.
            string xaml = PaletteText(light);
            Color panel = Parse(RawColor(xaml, "Panel"));

            foreach (string key in new[] { "TextPrim", "TextSec", "TextTer" })
            {
                double c = ColorMath.Contrast(Parse(RawColor(xaml, key)), panel);
                Assert.True(c >= threshold,
                    $"{key} na panelu {(light ? "jasnym" : "ciemnym")} = {c:F2}, próg {threshold}");
            }
        }
    }
}
