using System.Collections.Generic;
using RdpManager.Core;
using RdpManager.Models;
using Xunit;

namespace RdpManager.Tests
{
    public class ProfileBackupTests
    {
        [Fact]
        public void SerializeThenParse_RoundTripsServersAndSettings()
        {
            var settings = new AppSettings { DefaultPort = 3390, UiScale = 1.1, ConnectionLogEnabled = false };
            var servers = new List<ServerInfo>
            {
                new ServerInfo { Name = "a", Host = "10.0.0.1", Port = 3390, GatewayHostname = "gw" },
                new ServerInfo { Name = "b", Host = "example.com", AuthenticationLevel = 1 },
            };

            var json = ProfileBackup.Serialize(settings, servers);
            var data = ProfileBackup.Parse(json);

            Assert.NotNull(data);
            Assert.Equal(1, data.Version);
            Assert.Equal(3390, data.Settings.DefaultPort);
            Assert.False(data.Settings.ConnectionLogEnabled);
            Assert.Equal(2, data.Servers.Count);
            Assert.Equal("a", data.Servers[0].Name);
            Assert.Equal("gw", data.Servers[0].GatewayHostname);
            Assert.Equal(1, data.Servers[1].AuthenticationLevel);
        }

        [Fact]
        public void Serialize_DoesNotIncludePasswordMaterial()
        {
            var json = ProfileBackup.Serialize(new AppSettings(),
                new[] { new ServerInfo { Name = "a", Host = "h", SavePassword = true } });
            Assert.DoesNotContain("CredTarget", json);   // klucz sejfu jest [JsonIgnore]
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("{ not valid")]
        public void Parse_ReturnsNullOnEmptyAndThrowsOnGarbage(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                Assert.Null(ProfileBackup.Parse(input));
            else
                Assert.ThrowsAny<System.Exception>(() => ProfileBackup.Parse(input));
        }
    }
}
