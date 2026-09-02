using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media;
using RdpManager.Core;
using Xunit;

namespace RdpManager.Tests
{
    // Kolory grup (kart i serwerów) czytane WPROST z plików palety, nie z kopii wartości w teście.
    //
    // Powód istnienia: paleta grup kart była tablicą literałów w kontrolerze, a jej pierwszy wpis to
    // był #7C6CFB — ten sam odcień, który usunięto z próbnika akcentów i ze slotu 0 kolorów grup
    // serwerów, bo dzieli go od akcentu #6C6DFF odległość barwna ΔE 4,1. Poprawka objęła wtedy dwa
    // miejsca z trzech i pierwsza tworzona grupa kart dalej dostawała kolor nieodróżnialny od akcentu.
    //
    // Kontrast (WCAG) nie wystarcza jako miara: mówi o jasności, czyli o tym, czy da się COŚ odczytać.
    // Tu pytanie brzmi, czy da się dwa kolory ROZRÓŻNIĆ — a to jest ΔE.
    public class GroupPaletteTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RdpManager.sln")))
                dir = dir.Parent;
            Assert.True(dir != null, "Nie znaleziono katalogu repozytorium powyżej " + AppContext.BaseDirectory);
            return dir.FullName;
        }

        private static string PaletteText(bool light)
            => File.ReadAllText(Path.Combine(RepoRoot(), "src", "RdpManager", "Themes",
                                             light ? "Palette.Light.xaml" : "Palette.Dark.xaml"));

        private static Color Key(string xaml, string key)
        {
            var m = Regex.Match(xaml, "x:Key=\"" + Regex.Escape(key) + "\"\\s+Color=\"(#[0-9A-Fa-f]+)\"");
            Assert.True(m.Success, "Paleta nie definiuje klucza " + key);
            return (Color)ColorConverter.ConvertFromString(m.Groups[1].Value);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void KazdyKolorGrupyIstniejeWPalecie(bool light)
        {
            string xaml = PaletteText(light);
            // Brak klucza nie wysypuje aplikacji — kontroler ma awaryjny kolor — więc bez tego testu
            // literówka w nazwie klucza objawiłaby się jako sześć grup w jednym kolorze.
            foreach (string key in GroupPalette.Keys) Key(xaml, key);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void KolorGrupyJestODROZNIALNYOdAkcentu(bool light)
        {
            string xaml = PaletteText(light);
            Color accent = Key(xaml, "Accent");

            foreach (string key in GroupPalette.Keys)
            {
                double d = ColorMath.DeltaE(Key(xaml, key), accent);
                Assert.True(d > GroupPalette.MinDeltaE,
                    $"{key} wobec akcentu w motywie {(light ? "jasnym" : "ciemnym")}: ΔE {d:F1}, próg {GroupPalette.MinDeltaE}");
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void KoloryGrupSaOdroznialneMiedzySoba(bool light)
        {
            // Sześć grup obok siebie na pasku kart — dwie w tym samym odcieniu znoszą sens kolorowania.
            string xaml = PaletteText(light);
            var colors = GroupPalette.Keys.Select(k => new { Key = k, Color = Key(xaml, k) }).ToList();

            for (int i = 0; i < colors.Count; i++)
                for (int j = i + 1; j < colors.Count; j++)
                {
                    double d = ColorMath.DeltaE(colors[i].Color, colors[j].Color);
                    Assert.True(d > GroupPalette.MinDeltaE,
                        $"{colors[i].Key} ↔ {colors[j].Key} w motywie {(light ? "jasnym" : "ciemnym")}: ΔE {d:F1}");
                }
        }

        [Fact]
        public void StaryFioletBylbyOdrzuconyPrzezTenTest()
        {
            // Regresja wprost: gdyby #7C6CFB wrócił do palety grup, powyższe testy muszą go złapać.
            // Bez tego przypadku próg 15 byłby liczbą bez dowodu, że w ogóle coś odsiewa.
            Color violet = (Color)ColorConverter.ConvertFromString("#7C6CFB");
            double dark = ColorMath.DeltaE(violet, Key(PaletteText(false), "Accent"));
            double light = ColorMath.DeltaE(violet, Key(PaletteText(true), "Accent"));

            Assert.True(dark < GroupPalette.MinDeltaE, $"ciemny ΔE {dark:F1}");
            Assert.True(light < GroupPalette.MinDeltaE, $"jasny ΔE {light:F1}");
        }

        [Fact]
        public void DeltaE_JestZerowaDlaTegoSamegoKoloru()
            => Assert.Equal(0.0, ColorMath.DeltaE(Colors.Teal, Colors.Teal), 6);

        [Fact]
        public void DeltaE_JestSymetryczna()
        {
            Color a = (Color)ColorConverter.ConvertFromString("#6C6DFF");
            Color b = (Color)ColorConverter.ConvertFromString("#D06BD8");
            Assert.Equal(ColorMath.DeltaE(a, b), ColorMath.DeltaE(b, a), 6);
        }

        [Fact]
        public void DeltaE_RozniSieOdKontrastu()
        {
            // Dwa kolory o niemal identycznej JASNOŚCI (kontrast ~1) mogą być doskonale rozróżnialne.
            // To jest właśnie powód, dla którego progi kontrastu nie wystarczają dla kolorów-etykiet.
            Color a = (Color)ColorConverter.ConvertFromString("#7BA6FF");   // błękit
            Color b = (Color)ColorConverter.ConvertFromString("#F0B45F");   // bursztyn

            Assert.True(ColorMath.Contrast(a, b) < 1.5, "kontrast bliski 1 — podobna jasność");
            Assert.True(ColorMath.DeltaE(a, b) > 40, "a jednak wyraźnie różne barwy");
        }
    }
}
