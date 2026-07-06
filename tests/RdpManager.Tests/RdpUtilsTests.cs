using System;
using RdpManager.Core;
using RdpManager.Models;
using Xunit;

namespace RdpManager.Tests
{
    public class RdpUtilsTests
    {
        [Theory]
        [InlineData("prod-web1", "PW")]
        [InlineData("Ten komputer", "TK")]
        [InlineData("db.internal", "DI")]
        [InlineData("localhost", "LO")]
        [InlineData("", "")]
        [InlineData("   ", "")]
        public void MakeInitials_Works(string name, string expected)
        {
            Assert.Equal(expected, RdpUtils.MakeInitials(name));
        }

        [Theory]
        [InlineData("192.0.2.10", "192.0.2.10", 3389)]              // sam host -> port domyślny (adres z RFC 5737, do przykładów)
        [InlineData("192.0.2.10:3390", "192.0.2.10", 3390)]         // host:port
        [InlineData("host.example.com:52000", "host.example.com", 52000)]
        [InlineData("  server1  ", "server1", 3389)]                // trymowanie
        [InlineData("host:0", "host:0", 3389)]                      // port poza zakresem -> całość jako host
        [InlineData("host:99999", "host:99999", 3389)]              // port za duży -> całość jako host
        [InlineData("host:abc", "host:abc", 3389)]                  // nie-liczba -> całość jako host
        [InlineData("fe80::1", "fe80::1", 3389)]                    // wiele ':' (IPv6) -> nie dzielimy
        [InlineData("", "", 3389)]
        public void SplitHostPort_ParsesHostAndPort(string input, string expHost, int expPort)
        {
            var (host, port) = RdpUtils.SplitHostPort(input, 3389);
            Assert.Equal(expHost, host);
            Assert.Equal(expPort, port);
        }

        [Theory]
        [InlineData("10.0.0.5", "10.0.0.5", 3389, "", "")]
        [InlineData("10.0.0.5:3390", "10.0.0.5", 3390, "", "")]
        [InlineData("adam@srv1", "srv1", 3389, "adam", "")]
        [InlineData("adam@srv1:3390", "srv1", 3390, "adam", "")]
        [InlineData("CORP\\adam@srv1", "srv1", 3389, "adam", "CORP")]
        [InlineData("  CORP\\adam@srv1:3390  ", "srv1", 3390, "adam", "CORP")]
        [InlineData("", "", 3389, "", "")]
        public void ParseQuickConnect_ParsesAllForms(string input, string expHost, int expPort, string expUser, string expDomain)
        {
            var (host, port, user, domain) = RdpUtils.ParseQuickConnect(input, 3389);
            Assert.Equal(expHost, host);
            Assert.Equal(expPort, port);
            Assert.Equal(expUser, user);
            Assert.Equal(expDomain, domain);
        }

        [Theory]
        [InlineData(100, 200)]   // poniżej minimum
        [InlineData(199, 200)]
        [InlineData(200, 200)]
        [InlineData(801, 800)]   // wyrównanie do parzystej
        [InlineData(1920, 1920)]
        [InlineData(8193, 8192)] // powyżej maksimum
        public void NormalizeDim_ClampsAndMakesEven(int input, int expected)
        {
            int result = RdpUtils.NormalizeDim(input);
            Assert.Equal(expected, result);
            Assert.True(result % 2 == 0);
            Assert.InRange(result, RdpUtils.MinDim, RdpUtils.MaxDim);
        }

        [Fact]
        public void MatchesFilter_MatchesNameOrHostCaseInsensitive()
        {
            var s = new ServerInfo { Name = "prod-web1", Host = "192.0.2.10" };

            Assert.True(RdpUtils.MatchesFilter(s, ""));      // pusty filtr = wszystko
            Assert.True(RdpUtils.MatchesFilter(s, "  "));
            Assert.True(RdpUtils.MatchesFilter(s, "web"));   // po nazwie
            Assert.True(RdpUtils.MatchesFilter(s, "WEB"));   // bez uwzględniania wielkości liter
            Assert.True(RdpUtils.MatchesFilter(s, "192"));   // po hoście
            Assert.False(RdpUtils.MatchesFilter(s, "zzz"));
            Assert.False(RdpUtils.MatchesFilter(null, "web"));
        }

        [Fact]
        public void MatchesFilter_MatchesTags()
        {
            var s = new ServerInfo { Name = "srv1", Host = "h", Tags = new System.Collections.Generic.List<string> { "prod", "klientA" } };
            Assert.True(RdpUtils.MatchesFilter(s, "prod"));    // po tagu
            Assert.True(RdpUtils.MatchesFilter(s, "PROD"));    // bez uwzględniania wielkości liter
            Assert.True(RdpUtils.MatchesFilter(s, "#prod"));   // składnia #tag
            Assert.True(RdpUtils.MatchesFilter(s, "klient"));  // fragment taga
            Assert.False(RdpUtils.MatchesFilter(s, "staging"));
        }

        [Theory]
        [InlineData("16", 16)]
        [InlineData("24", 24)]
        [InlineData("32", 32)]
        [InlineData(" 24 ", 24)]
        [InlineData("999", 32)]   // niedozwolone -> fallback
        [InlineData("abc", 32)]
        [InlineData(null, 32)]
        public void ParseColorDepth_ValidatesAllowedValues(string text, int expected)
        {
            Assert.Equal(expected, RdpUtils.ParseColorDepth(text));
        }

        [Theory]
        [InlineData("Upłynął limit czasu.", 264, 3, "Upłynął limit czasu (kod 264/3)")]
        [InlineData(null, 0, 0, "rozłączono (kod 0/0)")]
        [InlineData("   ", 1, 2, "rozłączono (kod 1/2)")]
        public void FormatDisconnectReason_Formats(string desc, int reason, long ext, string expected)
        {
            Assert.Equal(expected, RdpUtils.FormatDisconnectReason(desc, reason, ext));
        }

        [Fact]
        public void FormatConnectionLog_IncludesTimestampEventAndTargetNoPassword()
        {
            var s = new ServerInfo { Name = "prod-web1", Host = "10.0.0.1", Port = 3389, Username = "admin", Domain = "CORP" };
            var line = RdpUtils.FormatConnectionLog(new DateTime(2026, 7, 1, 14, 30, 5), "CONNECTED", s);

            Assert.StartsWith("2026-07-01 14:30:05", line);
            Assert.Contains("CONNECTED", line);
            Assert.Contains("prod-web1 (10.0.0.1:3389)", line);
            Assert.Contains("user=CORP\\admin", line);
        }

        [Fact]
        public void FormatConnectionLog_WindowsAccountAndNoUser()
        {
            var win = new ServerInfo { Name = "h", Host = "h", UseWindowsAccount = true };
            Assert.Contains("user=(konto Windows)", RdpUtils.FormatConnectionLog(new DateTime(2026, 1, 1), "CONNECTED", win));

            var anon = new ServerInfo { Name = "h", Host = "h", Username = "" };
            Assert.Contains("user=-", RdpUtils.FormatConnectionLog(new DateTime(2026, 1, 1), "FAILED", anon));
        }

        [Theory]
        [InlineData(true, 42, "host:3389 — port OTWARTY (odpowiedź w 42 ms).")]
        [InlineData(false, 0, "host:3389 — BRAK odpowiedzi (port zamknięty, zapora lub host nieosiągalny).")]
        public void FormatDiagnostics_Formats(bool reachable, long ms, string expected)
        {
            Assert.Equal(expected, RdpUtils.FormatDiagnostics("host", 3389, reachable, ms,
                "{0}:{1} — port OTWARTY (odpowiedź w {2} ms).",
                "{0}:{1} — BRAK odpowiedzi (port zamknięty, zapora lub host nieosiągalny)."));
        }

        [Fact]
        public void FormatConnectionLog_SanitizesControlCharsAgainstLogForging()
        {
            var s = new ServerInfo
            {
                Name = "srv\r\n2026-01-01 00:00:00  CONNECTED fake",   // próba wstrzyknięcia linii
                Host = "h\tost",
                Username = "u\nser"
            };
            var line = RdpUtils.FormatConnectionLog(new DateTime(2026, 1, 1), "FAILED", s);

            Assert.DoesNotContain("\n", line);
            Assert.DoesNotContain("\r", line);
            Assert.DoesNotContain("\t", line);
        }
    }
}
