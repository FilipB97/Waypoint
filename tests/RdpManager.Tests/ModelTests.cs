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
    }
}
