using RdpManager.Core;
using RdpManager.Models;
using Xunit;

namespace RdpManager.Tests
{
    public class RdpFileTests
    {
        private const string Sample =
            "full address:s:server01.example.com:3390\r\n" +
            "username:s:CONTOSO\\jkowalski\r\n" +
            "redirectclipboard:i:0\r\n" +
            "redirectprinters:i:1\r\n" +
            "drivestoredirect:s:*\r\n" +
            "audiomode:i:2\r\n" +
            "authentication level:i:1\r\n" +
            "gatewayhostname:s:gw.example.com\r\n" +
            "gatewayusagemethod:i:1\r\n" +
            "screen mode id:i:2\r\n";

        [Fact]
        public void Parse_MapsCoreFields()
        {
            var s = RdpFile.Parse(Sample);

            Assert.Equal("server01.example.com", s.Host);
            Assert.Equal(3390, s.Port);
            Assert.Equal("jkowalski", s.Username);
            Assert.Equal("CONTOSO", s.Domain);
            Assert.False(s.RedirectClipboard);
            Assert.True(s.RedirectPrinters);
            Assert.True(s.RedirectDrives);
            Assert.Equal(2, s.AudioMode);
            Assert.Equal(1, s.AuthenticationLevel);
            Assert.Equal("gw.example.com", s.GatewayHostname);
            Assert.Equal(1, s.GatewayUsageMethod);
        }

        [Fact]
        public void Parse_DefaultsPortAndIsTolerantOfMissingKeys()
        {
            var s = RdpFile.Parse("full address:s:host-only\r\n");
            Assert.Equal("host-only", s.Host);
            Assert.Equal(3389, s.Port);            // domyślny port
            Assert.Equal(2, s.AuthenticationLevel); // domyślnie bezpieczny
            Assert.Equal("host-only", s.Name);
        }

        [Fact]
        public void Parse_HandlesIpv6WithAndWithoutPort()
        {
            Assert.Equal("::1", RdpFile.Parse("full address:s:[::1]:3391\r\n").Host);
            Assert.Equal(3391, RdpFile.Parse("full address:s:[::1]:3391\r\n").Port);
            Assert.Equal("::1", RdpFile.Parse("full address:s:::1\r\n").Host);   // goły IPv6 -> bez portu
            Assert.Equal(3389, RdpFile.Parse("full address:s:::1\r\n").Port);
        }

        [Fact]
        public void Parse_UserWithUpnStyle()
        {
            var s = RdpFile.Parse("full address:s:h\r\nusername:s:jan@contoso.local\r\n");
            Assert.Equal("jan", s.Username);
            Assert.Equal("contoso.local", s.Domain);
        }

        [Fact]
        public void ParseRaw_IsCaseInsensitiveAndSkipsGarbage()
        {
            var map = RdpFile.ParseRaw("FULL ADDRESS:s:h\r\n\r\ngarbage line without colons\r\naudiomode:i:1\r\n");
            Assert.Equal("h", map["full address"]);
            Assert.Equal("1", map["audiomode"]);
            Assert.False(map.ContainsKey("garbage line without colons"));
        }

        [Fact]
        public void SerializeThenParse_RoundTripsKeyFields()
        {
            var original = new ServerInfo
            {
                Host = "app.example.com",
                Port = 3390,
                Username = "admin",
                Domain = "CORP",
                RedirectClipboard = true,
                RedirectPrinters = false,
                RedirectDrives = true,
                AudioMode = 1,
                AuthenticationLevel = 1,
                GatewayHostname = "gw.example.com",
                GatewayUsageMethod = 2,
            };

            var back = RdpFile.Parse(RdpFile.Serialize(original));

            Assert.Equal(original.Host, back.Host);
            Assert.Equal(original.Port, back.Port);
            Assert.Equal(original.Username, back.Username);
            Assert.Equal(original.Domain, back.Domain);
            Assert.Equal(original.RedirectClipboard, back.RedirectClipboard);
            Assert.Equal(original.RedirectPrinters, back.RedirectPrinters);
            Assert.Equal(original.RedirectDrives, back.RedirectDrives);
            Assert.Equal(original.AudioMode, back.AudioMode);
            Assert.Equal(original.AuthenticationLevel, back.AuthenticationLevel);
            Assert.Equal(original.GatewayHostname, back.GatewayHostname);
            Assert.Equal(original.GatewayUsageMethod, back.GatewayUsageMethod);
        }

        [Fact]
        public void Serialize_OmitsDefaultPortAndEmptyGateway()
        {
            var s = new ServerInfo { Host = "h", Port = 3389 };
            var text = RdpFile.Serialize(s);
            Assert.Contains("full address:s:h\r\n", text);   // bez :3389
            Assert.DoesNotContain("gatewayhostname", text);
        }

        [Theory]
        [InlineData("full address:s:h:0\r\n")]        // port 0
        [InlineData("full address:s:h:-5\r\n")]       // ujemny
        [InlineData("full address:s:h:999999\r\n")]   // > 65535
        public void Parse_IgnoresOutOfRangePorts(string content)
        {
            var s = RdpFile.Parse(content);
            Assert.Equal("h", s.Host);
            Assert.Equal(3389, s.Port);   // zostaje bezpieczny domyślny
        }

        [Fact]
        public void Parse_ClampsGatewayUsageMethod()
        {
            var s = RdpFile.Parse("full address:s:h\r\ngatewayhostname:s:gw\r\ngatewayusagemethod:i:99\r\n");
            Assert.Equal(2, s.GatewayUsageMethod);
        }

        [Theory]
        [InlineData("use multimon:i:1", true)]
        [InlineData("use multimon:i:0", false)]
        [InlineData("use multimon:i:garbage", false)]   // niepoprawna wartość -> domyślne false
        [InlineData("", false)]                          // brak klucza -> domyślne false
        public void Parse_MapsUseMultimon(string line, bool expected)
        {
            var s = RdpFile.Parse("full address:s:h\r\n" + line + "\r\n");
            Assert.Equal(expected, s.UseAllMonitors);
        }

        [Fact]
        public void SerializeThenParse_RoundTripsUseMultimon()
        {
            var original = new ServerInfo { Host = "h", UseAllMonitors = true };
            Assert.True(RdpFile.Parse(RdpFile.Serialize(original)).UseAllMonitors);

            original.UseAllMonitors = false;
            Assert.False(RdpFile.Parse(RdpFile.Serialize(original)).UseAllMonitors);
        }
    }
}
