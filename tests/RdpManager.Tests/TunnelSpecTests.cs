using RdpManager.Core;
using Xunit;

namespace RdpManager.Tests
{
    public class TunnelSpecTests
    {
        [Theory]
        [InlineData("5433:db.internal:5432", 5433, "db.internal", 5432)]
        [InlineData(" 8080 : 10.0.0.5 : 80 ", 8080, "10.0.0.5", 80)]
        [InlineData("1:h:65535", 1, "h", 65535)]
        public void TryParse_AcceptsValidRules(string line, int lp, string host, int rp)
        {
            Assert.True(TunnelSpec.TryParse(line, out var l, out var h, out var r));
            Assert.Equal(lp, l);
            Assert.Equal(host, h);
            Assert.Equal(rp, r);
        }

        [Theory]
        [InlineData("")]
        [InlineData("5433:db")]                  // za mało części
        [InlineData("5433:db:5432:extra")]       // za dużo części
        [InlineData("0:db:5432")]                // port lokalny poza zakresem
        [InlineData("70000:db:5432")]
        [InlineData("5433::5432")]               // pusty host
        [InlineData("5433:db:abc")]              // port zdalny nie-liczba
        [InlineData("abc:db:5432")]
        public void TryParse_RejectsInvalidRules(string line)
        {
            Assert.False(TunnelSpec.TryParse(line, out _, out _, out _));
        }

        [Fact]
        public void ParseAll_SkipsCommentsAndEmpty_ReportsFirstBadLine()
        {
            var list = TunnelSpec.ParseAll("# komentarz\r\n5433:db:5432\r\n\r\n8080:web:80\r\n", out var bad);
            Assert.Null(bad);
            Assert.Equal(new[] { "5433:db:5432", "8080:web:80" }, list);

            list = TunnelSpec.ParseAll("5433:db:5432\nzepsuta-linia\n9090:x:90", out bad);
            Assert.Equal("zepsuta-linia", bad);
            Assert.Equal(2, list.Count);   // poprawne reguły przechodzą mimo błędnej linii
        }
    }

    public class UpdateCheckTests
    {
        [Theory]
        [InlineData("v1.2.0", "1.2.0")]
        [InlineData("V2.0", "2.0")]
        [InlineData("1.10.3", "1.10.3")]
        public void ParseTag_StripsPrefixAndParses(string tag, string expected)
        {
            Assert.Equal(new System.Version(expected), UpdateCheck.ParseTag(tag));
        }

        [Theory]
        [InlineData("release-x")]
        [InlineData("")]
        [InlineData(null)]
        public void ParseTag_ReturnsNullOnGarbage(string tag)
        {
            Assert.Null(UpdateCheck.ParseTag(tag));
        }

        [Fact]
        public void ParseLatest_ReadsTagName_NullOnBadJson()
        {
            Assert.Equal(new System.Version("1.2.0"),
                UpdateCheck.ParseLatest("{\"tag_name\":\"v1.2.0\",\"name\":\"Waypoint 1.2.0\"}"));
            Assert.Null(UpdateCheck.ParseLatest("nie-json"));
            Assert.Null(UpdateCheck.ParseLatest("{\"other\":1}"));
        }

        [Fact]
        public void IsNewer_ComparesVersions()
        {
            Assert.True(UpdateCheck.IsNewer(new System.Version("1.2.0"), new System.Version("1.1.0")));
            Assert.False(UpdateCheck.IsNewer(new System.Version("1.1.0"), new System.Version("1.1.0")));
            Assert.False(UpdateCheck.IsNewer(null, new System.Version("1.1.0")));
            Assert.False(UpdateCheck.IsNewer(new System.Version("1.2.0"), null));
        }
    }
}
