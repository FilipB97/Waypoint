using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;
using RdpManager.Core;
using Xunit;

namespace RdpManager.Tests
{
    // Terminal (xterm.js w WebView2) to osobny dokument — nie widzi DynamicResource, więc miał całą
    // paletę wpisaną na sztywno, sterowaną jedynie flagą „jasny/ciemny". Skutki były dwa i oba widoczne:
    // wybór presetu albo własnego akcentu przemalowywał całe okno POZA terminalem, a jeden z literałów
    // (#2C2E37) był kolorem panelu sprzed zmiany palety, więc pasek szukania odstawał od reszty okna.
    //
    // TerminalTheme przyjmuje czytnik palety jako parametr, dzięki czemu te niezmienniki da się sprawdzić
    // bez uruchamiania aplikacji WPF.
    public class TerminalThemeTests
    {
        private static Color C(byte r, byte g, byte b, byte a = 255) => Color.FromArgb(a, r, g, b);

        private static Func<string, Color?> Map(Dictionary<string, Color> m)
            => k => m.TryGetValue(k, out var c) ? c : (Color?)null;

        private static readonly Dictionary<string, Color> Preset = new Dictionary<string, Color>
        {
            ["Canvas"] = C(0x1A, 0x1B, 0x26),
            ["Panel"] = C(0x24, 0x28, 0x3B),
            ["Border"] = C(0xFF, 0xFF, 0xFF, 0x22),
            ["TextPrim"] = C(0xC0, 0xCA, 0xF5),
            ["TextTer"] = C(0x56, 0x5F, 0x89),
            ["Accent"] = C(0x7A, 0xA2, 0xF7)
        };

        [Fact]
        public void MotywIdzieZPalety_ANieZWartosciAwaryjnych()
        {
            // Sedno poprawki: preset (tu barwy Tokyo Night) ma dotrzeć do terminala.
            var t = TerminalTheme.From(Map(Preset), light: false);

            Assert.Equal("#1A1B26", t.Background);
            Assert.Equal("#C0CAF5", t.Foreground);
            Assert.Equal("#7AA2F7", t.Cursor);
            Assert.Equal("#7AA2F7", t.Accent);
            Assert.Equal("#24283B", t.Panel);
            Assert.Equal("#565F89", t.TextTer);
        }

        [Fact]
        public void BrakKluczaDajeWartoscAwaryjna_ANieCzarnyEkran()
        {
            // Gdyby paleta kiedyś nie dostarczyła klucza, terminal ma zostać czytelny.
            var t = TerminalTheme.From(_ => null, light: false);

            Assert.StartsWith("#", t.Background);
            Assert.NotEqual(t.Background, t.Foreground);
            Assert.False(string.IsNullOrEmpty(t.Cursor));
            Assert.False(string.IsNullOrEmpty(t.Selection));
        }

        [Fact]
        public void MotywJasnyICiemnyMajaInneWartosciAwaryjne()
        {
            var d = TerminalTheme.From(_ => null, light: false);
            var l = TerminalTheme.From(_ => null, light: true);

            Assert.NotEqual(d.Background, l.Background);
            Assert.NotEqual(d.Foreground, l.Foreground);
        }

        [Fact]
        public void ZaznaczenieToAkcentZKryciem_ZebyTekstPodSpodemZostalCzytelny()
        {
            // Pełny akcent pod zaznaczeniem zasłania litery; dlatego zaznaczenie jest rgba, nie hex.
            var t = TerminalTheme.From(Map(Preset), light: false);

            Assert.StartsWith("rgba(122,162,247,", t.Selection);
            Assert.DoesNotContain("#", t.Selection);
        }

        [Fact]
        public void ObramowanieZachowujeKanalAlfa()
        {
            // „Border" w palecie to biel z kryciem ~13%. Odczyt gubiący alfę zrobiłby z włoskowatej
            // kreski pełną, jasną linię przez cały pasek szukania.
            var t = TerminalTheme.From(Map(Preset), light: false);

            Assert.Equal("rgba(255,255,255,0.133)", t.Border);
        }

        [Fact]
        public void ZmiennaCssTlaZgadzaSieZTlemXterm()
        {
            // Strona maluje tło dokumentu tą zmienną, a xterm swoim „background" — rozjazd dałby
            // widoczny prostokąt innego koloru wokół siatki znaków.
            var t = TerminalTheme.From(Map(Preset), light: false);

            Assert.Equal(t.Background, t.CssVars()["--wp-bg"]);
        }

        [Fact]
        public void ZmienneCssPokrywajaWszystkieUzyteWArkuszuTerminala()
        {
            // Test wiąże arkusz stylów paska szukania z jego źródłem wartości: dopisanie do CSS zmiennej,
            // której nikt nie ustawia, daje kolor „przezroczysty" i element znika.
            string src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "RdpManager", "XtermControl.cs"));
            var used = new HashSet<string>();
            foreach (Match m in Regex.Matches(src, @"var\((--wp-[a-z0-9]+)\)")) used.Add(m.Groups[1].Value);

            Assert.NotEmpty(used);
            var defined = TerminalTheme.From(Map(Preset), light: false).CssVars();
            foreach (var v in used)
                Assert.True(defined.ContainsKey(v), "Arkusz terminala używa " + v + ", a TerminalTheme tego nie ustawia");
        }

        [Fact]
        public void PlikTerminalaNieZawieraZaszytychKolorow()
        {
            // Regresja. Przed poprawką XtermControl.cs miał 14 literałów kolorów i ZERO odczytów palety;
            // to one sprawiały, że presety go omijały, a panel paska szukania został na barwie sprzed
            // zmiany palety. Zasoby xterm.js są plikami zewnętrznymi, więc plik jest wolny od kolorów
            // i wolny ma zostać.
            //
            // Wyjątek świadomy: cień paska szukania to rgba(0,0,0,.35) — czerń z kryciem jest cieniem,
            // a nie barwą palety, i jest poprawna w obu motywach.
            string src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "RdpManager", "XtermControl.cs"));

            var hex = Regex.Matches(src, @"#(?:[0-9A-Fa-f]{3}|[0-9A-Fa-f]{4}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})\b");
            Assert.True(hex.Count == 0, "Zaszyte kolory w XtermControl.cs: " +
                string.Join(", ", System.Linq.Enumerable.Select(System.Linq.Enumerable.Cast<Match>(hex), m => m.Value)));

            Assert.DoesNotContain("FromRgb(0x", src);
            Assert.DoesNotContain("FromArgb(0x", src);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RdpManager.sln")))
                dir = dir.Parent;
            Assert.True(dir != null, "Nie znaleziono katalogu repozytorium (RdpManager.sln) powyżej " + AppContext.BaseDirectory);
            return dir.FullName;
        }
    }
}
