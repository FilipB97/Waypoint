using RdpManager.Core;
using Xunit;

namespace RdpManager.Tests
{
    public class RdpConfiguratorTests
    {
        [Theory]
        [InlineData(-5, 0u)]
        [InlineData(0, 0u)]
        [InlineData(1, 1u)]
        [InlineData(2, 2u)]
        [InlineData(9, 2u)]
        public void ClampAuthLevel_ClampsTo0To2(int input, uint expected)
            => Assert.Equal(expected, RdpConfigurator.ClampAuthLevel(input));

        [Theory]
        [InlineData(-1, 0u)]
        [InlineData(0, 0u)]
        [InlineData(2, 2u)]
        [InlineData(5, 2u)]
        public void ClampAudioMode_ClampsTo0To2(int input, uint expected)
            => Assert.Equal(expected, RdpConfigurator.ClampAudioMode(input));

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void GatewayUsageMethod_NoHost_IsZero(string host)
            => Assert.Equal(0u, RdpConfigurator.GatewayUsageMethod(host, 2));

        [Theory]
        [InlineData(0, 1u)]   // host podany, ale metoda 0 -> traktuj jak 1 (zawsze przez bramę)
        [InlineData(1, 1u)]
        [InlineData(2, 2u)]
        public void GatewayUsageMethod_WithHost_UsesConfiguredOr1(int configured, uint expected)
            => Assert.Equal(expected, RdpConfigurator.GatewayUsageMethod("gw.example.test", configured));
    }
}
