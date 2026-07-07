using RdpManager.Models;
using Xunit;

namespace RdpManager.Tests
{
    public class ModelTests
    {
        [Fact]
        public void ServerInfo_HasSecureAndSensibleDefaults()
        {
            var s = new ServerInfo();

            Assert.Equal(3389, s.Port);
            Assert.Equal(2, s.AuthenticationLevel);      // domyślnie „ostrzegaj” — nie 0
            Assert.True(s.RedirectClipboard);
            Assert.False(s.RedirectDrives);
            Assert.False(s.RedirectPrinters);
            Assert.False(s.SavePassword);
            Assert.False(s.UseWindowsAccount);
            Assert.Equal(ServerStatus.Offline, s.Status);
        }

        [Fact]
        public void ServerInfo_IdIsUniqueAndCredTargetDerivesFromIt()
        {
            var a = new ServerInfo();
            var b = new ServerInfo();

            Assert.False(string.IsNullOrWhiteSpace(a.Id));
            Assert.Equal(32, a.Id.Length);               // Guid "N" = 32 znaki hex
            Assert.NotEqual(a.Id, b.Id);
            Assert.Equal("RdpManager:" + a.Id, a.CredTarget);
        }

        [Fact]
        public void ServerInfo_Notes_DefaultEmpty_AndRoundTripsThroughJson()
        {
            Assert.Equal("", new ServerInfo().Notes);

            var s = new ServerInfo { Name = "srv", Notes = "wiersz 1\nwiersz 2" };
            var json = System.Text.Json.JsonSerializer.Serialize(s);
            var back = System.Text.Json.JsonSerializer.Deserialize<ServerInfo>(json);
            Assert.Equal("wiersz 1\nwiersz 2", back.Notes);
        }
    }
}
