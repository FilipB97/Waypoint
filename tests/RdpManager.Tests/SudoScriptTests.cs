using RdpManager.Core;
using Xunit;

namespace RdpManager.Tests
{
    // Zapis przez sudo — te same przypadki co SudoWriteUnitTests w wersji na macOS (ten sam skrypt).
    // Pełny zapis sprawdzony na prawdziwym sudo (plik root:adm 0640 z apostrofem w nazwie) — opis w PR.
    public class SudoScriptTests
    {
        [Fact]
        public void CytowaniePowloki()
        {
            Assert.Equal("'a b'", SudoScript.ShellQuote("a b"));
            Assert.Equal("'it'\\''s'", SudoScript.ShellQuote("it's"));
            Assert.Equal("'$(rm -rf /)'", SudoScript.ShellQuote("$(rm -rf /)"));
        }

        [Fact]
        public void PolecenieZdalne()
        {
            string c = SudoScript.RemoteCommand("/tmp/.w", "/etc/it's here.conf");
            Assert.StartsWith("sudo -S -p '' -- sh -c '", c);
            Assert.EndsWith("waypoint-sudo '/tmp/.w' '/etc/it'\\''s here.conf'", c);
        }

        [Fact]
        public void Wynik()
        {
            Assert.Equal(SudoOutcome.AtomicReplace, SudoScript.Interpret(0, "ATOMIC\n", ""));
            Assert.Equal(SudoOutcome.InPlace, SudoScript.Interpret(0, "INPLACE\n", ""));
            Assert.True(Assert.Throws<SudoException>(() => SudoScript.Interpret(1, "", "Sorry, try again.\nsudo: 3 incorrect password attempts")).Denied);
            Assert.True(Assert.Throws<SudoException>(() => SudoScript.Interpret(1, "", "wpt is not in the sudoers file.")).Denied);
            Assert.False(Assert.Throws<SudoException>(() => SudoScript.Interpret(2, "", "brak pliku: /x")).Denied);
        }

        [Fact]
        public void TenSamSkryptCoNaMacu()
        {
            // Rozjazd skryptów między wersjami oznaczałby różne zachowanie tego samego zapisu.
            var swift = System.IO.File.ReadAllText(System.IO.Path.Combine(
                RepoRoot(), "mac", "WaypointCore", "Sources", "WaypointCore", "SudoWrite.swift"));
            foreach (var line in SudoScript.Script.Split('\n'))
                Assert.Contains(line.Trim().Replace("\\\\", "\\"), swift.Replace("\\\\", "\\"));
        }

        private static string RepoRoot()
        {
            var d = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "RdpManager.sln"))) d = d.Parent;
            return d?.FullName ?? ".";
        }
    }
}
