using RdpManager.Core;
using RdpManager.Models;
using Xunit;

namespace RdpManager.Tests
{
    public class WakeOnLanTests
    {
        [Theory]
        [InlineData("AA:BB:CC:DD:EE:FF")]
        [InlineData("aa-bb-cc-dd-ee-ff")]
        [InlineData("AABB.CCDD.EEFF")]
        [InlineData("aabbccddeeff")]
        [InlineData("  AA BB CC DD EE FF  ")]
        public void TryParseMac_AcceptsCommonFormats(string text)
        {
            Assert.True(WakeOnLan.TryParseMac(text, out var mac));
            Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF }, mac);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("AA:BB:CC:DD:EE")]        // za krótki
        [InlineData("AA:BB:CC:DD:EE:FF:00")]  // za długi
        [InlineData("ZZ:BB:CC:DD:EE:FF")]     // nie-hex
        public void TryParseMac_RejectsGarbage(string text)
        {
            Assert.False(WakeOnLan.TryParseMac(text, out _));
        }

        [Fact]
        public void BuildMagicPacket_HasHeaderAnd16MacRepeats()
        {
            var mac = new byte[] { 1, 2, 3, 4, 5, 6 };
            var p = WakeOnLan.BuildMagicPacket(mac);

            Assert.Equal(102, p.Length);                       // 6 + 16*6
            for (int i = 0; i < 6; i++) Assert.Equal(0xFF, p[i]);
            for (int r = 0; r < 16; r++)
                for (int i = 0; i < 6; i++)
                    Assert.Equal(mac[i], p[6 + r * 6 + i]);
        }
    }

    public class RdpFileAdminSessionTests
    {
        [Fact]
        public void AdminSession_RoundTripsThroughRdpFile()
        {
            var s = new ServerInfo { Host = "h", AdminSession = true };
            var text = RdpFile.Serialize(s);
            Assert.Contains("administrative session:i:1", text);

            var parsed = RdpFile.Parse(text);
            Assert.True(parsed.AdminSession);

            Assert.False(RdpFile.Parse(RdpFile.Serialize(new ServerInfo { Host = "h" })).AdminSession);
        }
    }
}
