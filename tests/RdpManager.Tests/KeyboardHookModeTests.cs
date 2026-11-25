using System;
using System.IO;
using System.Linq;
using RdpManager;
using RdpManager.Core;
using RdpManager.Models;
using Xunit;

namespace RdpManager.Tests
{
    // Issue #270: gdzie działają skróty Windows (Win, Win+R, Alt+Tab) w sesji RDP.
    // 0 = komputer lokalny, 1 = komputer zdalny; -1 na serwerze = użyj ustawienia globalnego.
    public class KeyboardHookModeTests : IDisposable
    {
        private readonly string _dir;

        public KeyboardHookModeTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "RdpManagerTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* najlepszy wysiłek */ }
        }

        // ---------- AppSettings.RdpKeyboardHookMode ----------

        [Fact]
        public void Settings_RdpKeyboardHookMode_DefaultsToLocal()
            => Assert.Equal(0, new AppSettings().RdpKeyboardHookMode);

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        public void Settings_RdpKeyboardHookMode_RoundTripsThroughSettingsStore(int mode)
        {
            SettingsStore.Save(new AppSettings { RdpKeyboardHookMode = mode }, _dir);
            var loaded = SettingsStore.Load(_dir);
            Assert.Equal(mode, loaded.RdpKeyboardHookMode);
        }

        [Fact]
        public void Settings_FileWithoutRdpKeyboardHookMode_LoadsAsLocal()
        {
            File.WriteAllText(Path.Combine(_dir, "settings.json"), "{\"DefaultPort\":3391}");
            var loaded = SettingsStore.Load(_dir);
            Assert.Equal(3391, loaded.DefaultPort);   // plik faktycznie wczytany, nie domyślne
            Assert.Equal(0, loaded.RdpKeyboardHookMode);
        }

        // ---------- ServerInfo.KeyboardHookMode ----------

        [Fact]
        public void Server_KeyboardHookMode_DefaultsToUseGlobal()
            => Assert.Equal(-1, new ServerInfo().KeyboardHookMode);

        [Fact]
        public void Server_JsonWithoutKeyboardHookMode_DeserializesAsUseGlobal()
        {
            var back = System.Text.Json.JsonSerializer.Deserialize<ServerInfo>("{\"Name\":\"srv\",\"Host\":\"h\"}");
            Assert.Equal("srv", back.Name);
            Assert.Equal(-1, back.KeyboardHookMode);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(0)]
        [InlineData(1)]
        public void Server_KeyboardHookMode_RoundTripsThroughJson(int mode)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new ServerInfo { Name = "srv", KeyboardHookMode = mode });
            var back = System.Text.Json.JsonSerializer.Deserialize<ServerInfo>(json);
            Assert.Equal(mode, back.KeyboardHookMode);
        }

        [Fact]
        public void ServerRepository_FileWithoutKeyboardHookMode_LoadsAsUseGlobal()
        {
            File.WriteAllText(Path.Combine(_dir, "servers.json"), "[{\"Name\":\"srv\",\"Host\":\"h\"}]");
            var loaded = ServerRepository.Load(_dir);
            Assert.Single(loaded);
            Assert.Equal(-1, loaded[0].KeyboardHookMode);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(0)]
        [InlineData(1)]
        public void ServerRepository_KeyboardHookMode_RoundTripsToDisk(int mode)
        {
            ServerRepository.Save(new System.Collections.Generic.List<ServerInfo>
            {
                new ServerInfo { Name = "srv", Host = "h", KeyboardHookMode = mode }
            }, _dir);
            var loaded = ServerRepository.Load(_dir);
            Assert.Equal(mode, loaded.Single(s => s.Name == "srv").KeyboardHookMode);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(0)]
        [InlineData(1)]
        public void Server_ShallowClone_CopiesKeyboardHookMode(int mode)
        {
            var original = new ServerInfo { Name = "srv", KeyboardHookMode = mode };
            Assert.Equal(mode, original.ShallowClone().KeyboardHookMode);
        }

        // ---------- RdpConfigurator.KeyboardHookMode(serverMode, globalMode) ----------

        [Theory]
        [InlineData(0, 0, 0)]
        [InlineData(0, 1, 0)]    // nadpisanie serwera wygrywa z globalnym
        [InlineData(1, 0, 1)]
        [InlineData(1, 1, 1)]
        [InlineData(0, -1, 0)]
        [InlineData(1, 7, 1)]
        public void Resolve_ServerOverrideWins(int serverMode, int globalMode, int expected)
            => Assert.Equal(expected, RdpConfigurator.KeyboardHookMode(serverMode, globalMode));

        [Theory]
        [InlineData(-1, 0, 0)]
        [InlineData(-1, 1, 1)]
        public void Resolve_UseGlobal_TakesGlobal(int serverMode, int globalMode, int expected)
            => Assert.Equal(expected, RdpConfigurator.KeyboardHookMode(serverMode, globalMode));

        [Theory]
        [InlineData(2, 1, 1)]    // 2 (tylko pełny ekran kontrolki) nie jest oferowane -> traktuj jak „globalne”
        [InlineData(2, 0, 0)]
        [InlineData(5, 1, 1)]
        [InlineData(-7, 1, 1)]
        [InlineData(int.MaxValue, 1, 1)]
        [InlineData(int.MinValue, 0, 0)]
        public void Resolve_OutOfRangeServerMode_FallsBackToGlobal(int serverMode, int globalMode, int expected)
            => Assert.Equal(expected, RdpConfigurator.KeyboardHookMode(serverMode, globalMode));

        [Theory]
        [InlineData(-1, 2)]
        [InlineData(-1, -3)]
        [InlineData(-1, -1)]
        [InlineData(-1, 99)]
        [InlineData(5, 2)]
        [InlineData(-7, -3)]
        public void Resolve_InvalidGlobal_FallsBackToLocal(int serverMode, int globalMode)
            => Assert.Equal(0, RdpConfigurator.KeyboardHookMode(serverMode, globalMode));

        // ---------- Import .rdp ----------

        [Theory]
        [InlineData("keyboardhook:i:0", 0)]
        [InlineData("keyboardhook:i:1", 1)]
        [InlineData("keyboardhook:i:2", -1)]   // domyślne mstsc (tylko pełny ekran) -> użyj globalnego
        [InlineData("keyboardhook:i:x", -1)]   // śmieci -> użyj globalnego
        [InlineData("keyboardhook:i:", -1)]
        [InlineData("keyboardhook:i:7", -1)]
        [InlineData("keyboardhook:i:-1", -1)]
        [InlineData("", -1)]                   // brak linii -> użyj globalnego
        public void Import_MapsKeyboardHook(string line, int expected)
        {
            var s = RdpFile.Parse("full address:s:h\r\n" + line + "\r\n");
            Assert.Equal("h", s.Host);
            Assert.Equal(expected, s.KeyboardHookMode);
        }

        [Fact]
        public void Import_KeyboardHookAmongOtherSettings()
        {
            var s = RdpFile.Parse(
                "full address:s:srv.example.com\r\n" +
                "audiomode:i:2\r\n" +
                "keyboardhook:i:1\r\n" +
                "redirectclipboard:i:0\r\n");
            Assert.Equal(1, s.KeyboardHookMode);
            Assert.Equal(2, s.AudioMode);
            Assert.False(s.RedirectClipboard);
        }

        [Fact]
        public void Import_KeyboardHookKeyIsCaseInsensitive()
        {
            // ParseRaw jest case-insensitive dla kluczy (patrz RdpFileTests) - keyboardhook też.
            var s = RdpFile.Parse("full address:s:h\r\nKeyboardHook:i:1\r\n");
            Assert.Equal(1, s.KeyboardHookMode);
        }

        // ---------- Eksport .rdp ----------

        [Theory]
        [InlineData(0, "keyboardhook:i:0")]
        [InlineData(1, "keyboardhook:i:1")]
        public void Export_WritesKeyboardHookLine(int mode, string expectedLine)
        {
            var text = RdpFile.Serialize(new ServerInfo { Host = "h", KeyboardHookMode = mode });
            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            Assert.Contains(expectedLine, lines);
            Assert.Single(lines, l => l.StartsWith("keyboardhook:", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Export_UseGlobal_OmitsKeyboardHookLine()
        {
            var text = RdpFile.Serialize(new ServerInfo { Host = "h", KeyboardHookMode = -1 });
            Assert.DoesNotContain("keyboardhook:", text, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Export_DefaultServer_OmitsKeyboardHookLine()
        {
            var text = RdpFile.Serialize(new ServerInfo { Host = "h" });
            Assert.DoesNotContain("keyboardhook:", text, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(0)]
        [InlineData(1)]
        public void ExportThenImport_RoundTripsKeyboardHookMode(int mode)
        {
            var original = new ServerInfo { Host = "h", KeyboardHookMode = mode };
            var back = RdpFile.Parse(RdpFile.Serialize(original));
            Assert.Equal(mode, back.KeyboardHookMode);
        }
    }
}
