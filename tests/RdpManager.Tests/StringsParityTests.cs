using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace RdpManager.Tests
{
    // Teksty interfejsu żyją w dwóch słownikach XAML (pl/en) i są wołane z trzeciego miejsca — z kodu.
    // Nic w kompilacji tego nie wiąże: brakujący klucz nie jest błędem budowania, tylko pustym napisem
    // albo dosłownym „S.snip.title" na ekranie, i to WYŁĄCZNIE w jednym języku. Ten test jest tą więzią.
    public class StringsParityTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RdpManager.sln")))
                dir = dir.Parent;
            Assert.True(dir != null, "Nie znaleziono katalogu repozytorium (RdpManager.sln) powyżej " + AppContext.BaseDirectory);
            return dir.FullName;
        }

        private static string SrcDir() => Path.Combine(RepoRoot(), "src", "RdpManager");

        private static HashSet<string> Keys(string lang)
        {
            string text = File.ReadAllText(Path.Combine(SrcDir(), "Themes", "Strings." + lang + ".xaml"));
            return new HashSet<string>(Regex.Matches(text, "x:Key=\"(S\\.[^\"]+)\"")
                                             .Cast<Match>().Select(m => m.Groups[1].Value));
        }

        private static IEnumerable<string> Sources(string pattern)
            => Directory.EnumerateFiles(SrcDir(), pattern, SearchOption.AllDirectories)
                        .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                                 && !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));

        [Fact]
        public void ObaJezykiMajaTeSameKlucze()
        {
            var pl = Keys("pl");
            var en = Keys("en");

            Assert.True(pl.SetEquals(en),
                "Tylko w pl: " + string.Join(", ", pl.Except(en).OrderBy(x => x)) +
                " | tylko w en: " + string.Join(", ", en.Except(pl).OrderBy(x => x)));
        }

        [Fact]
        public void KluczeUzyteWXamlIstniejaWSlownikach()
        {
            var known = Keys("pl");
            var missing = new List<string>();

            foreach (var file in Sources("*.xaml"))
            {
                if (Path.GetFileName(file).StartsWith("Strings.", StringComparison.Ordinal)) continue;
                string text = File.ReadAllText(file);
                foreach (Match m in Regex.Matches(text, @"DynamicResource\s+(S\.[A-Za-z0-9_.]+)"))
                    if (!known.Contains(m.Groups[1].Value)) missing.Add(Path.GetFileName(file) + ": " + m.Groups[1].Value);
            }

            Assert.True(missing.Count == 0, "Klucze użyte w XAML, których nie ma w słowniku: " + string.Join(", ", missing));
        }

        [Fact]
        public void KluczeWolaneZKoduIstniejaWSlownikach()
        {
            var known = Keys("pl");
            var missing = new List<string>();

            foreach (var file in Sources("*.cs"))
            {
                string text = File.ReadAllText(file);
                // Tylko literały. Klucze składane w locie (L("S.warn." + k)) są poza zasięgiem takiego
                // sprawdzenia — i dlatego jest ich w kodzie dokładnie jedno miejsce.
                foreach (Match m in Regex.Matches(text, @"(?:LocalizationManager\.S|\bL)\(\s*""(S\.[A-Za-z0-9_.]+)""\s*\)"))
                    if (!known.Contains(m.Groups[1].Value)) missing.Add(Path.GetFileName(file) + ": " + m.Groups[1].Value);
            }

            Assert.True(missing.Count == 0, "Klucze wołane z kodu, których nie ma w słowniku: " + string.Join(", ", missing));
        }
    }
}
