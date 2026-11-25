using System;
using System.IO;
using RdpManager;
using Xunit;

namespace RdpManager.Tests
{
    // #268: szerokość panelu bocznego (listy serwerów) zmieniana przeciąganiem prawej krawędzi
    // i zapamiętywana w ustawieniach. Zakres [180; 480], domyślnie 228.
    public class SidebarWidthTests : IDisposable
    {
        private readonly string _dir;

        public SidebarWidthTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "waypoint-sidebar-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        [Fact]
        public void Default_FreshSettings_Is228()
        {
            Assert.Equal(228.0, new AppSettings().SidebarWidth);
        }

        [Fact]
        public void Bounds_AreMin180AndMax480()
        {
            Assert.Equal(180.0, AppSettings.SidebarWidthMin);
            Assert.Equal(480.0, AppSettings.SidebarWidthMax);
        }

        [Fact]
        public void Default_LiesInsideBounds()
        {
            var width = new AppSettings().SidebarWidth;
            Assert.InRange(width, AppSettings.SidebarWidthMin, AppSettings.SidebarWidthMax);
        }

        [Theory]
        [InlineData(300.0)]
        [InlineData(228.0)]
        [InlineData(180.5)]
        [InlineData(479.9)]
        public void Clamp_ValueInsideRange_ReturnedUnchanged(double width)
        {
            Assert.Equal(width, AppSettings.ClampSidebarWidth(width));
        }

        [Theory]
        [InlineData(180.0)]
        [InlineData(480.0)]
        public void Clamp_ExactBounds_ReturnedUnchanged(double width)
        {
            Assert.Equal(width, AppSettings.ClampSidebarWidth(width));
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        [InlineData(-500.0)]
        [InlineData(179.9)]
        [InlineData(50.0)]
        public void Clamp_BelowMin_ReturnsMin(double width)
        {
            Assert.Equal(180.0, AppSettings.ClampSidebarWidth(width));
        }

        [Theory]
        [InlineData(480.1)]
        [InlineData(1000.0)]
        [InlineData(10000.0)]
        [InlineData(double.MaxValue)]
        public void Clamp_AboveMax_ReturnsMax(double width)
        {
            Assert.Equal(480.0, AppSettings.ClampSidebarWidth(width));
        }

        [Fact]
        public void Clamp_NaN_ReturnsDefault228()
        {
            Assert.Equal(228.0, AppSettings.ClampSidebarWidth(double.NaN));
        }

        [Fact]
        public void Clamp_PositiveInfinity_ReturnsMax()
        {
            Assert.Equal(480.0, AppSettings.ClampSidebarWidth(double.PositiveInfinity));
        }

        [Fact]
        public void Clamp_NegativeInfinity_ReturnsMin()
        {
            Assert.Equal(180.0, AppSettings.ClampSidebarWidth(double.NegativeInfinity));
        }

        [Fact]
        public void Clamp_IsIdempotent()
        {
            foreach (var w in new[] { -10.0, 0.0, 200.0, 480.0, 9999.0, double.NaN, double.PositiveInfinity })
            {
                var once = AppSettings.ClampSidebarWidth(w);
                Assert.Equal(once, AppSettings.ClampSidebarWidth(once));
            }
        }

        [Fact]
        public void Persistence_CustomWidth_SurvivesSaveLoadRoundTrip()
        {
            SettingsStore.Save(new AppSettings { SidebarWidth = 312.5 }, _dir);
            var loaded = SettingsStore.Load(_dir);
            Assert.Equal(312.5, loaded.SidebarWidth);
        }

        [Theory]
        [InlineData(180.0)]
        [InlineData(480.0)]
        public void Persistence_BoundaryWidth_SurvivesSaveLoadRoundTrip(double width)
        {
            SettingsStore.Save(new AppSettings { SidebarWidth = width }, _dir);
            Assert.Equal(width, SettingsStore.Load(_dir).SidebarWidth);
        }

        [Fact]
        public void Persistence_WidthWrittenToSettingsFile()
        {
            SettingsStore.Save(new AppSettings { SidebarWidth = 333 }, _dir);
            var json = File.ReadAllText(Path.Combine(_dir, "settings.json"));
            Assert.Contains("SidebarWidth", json);
        }

        [Fact]
        public void Persistence_DefaultWidth_SurvivesSaveLoadRoundTrip()
        {
            SettingsStore.Save(new AppSettings(), _dir);
            Assert.Equal(228.0, SettingsStore.Load(_dir).SidebarWidth);
        }

        [Fact]
        public void Persistence_FileWithoutSidebarWidth_LoadsAs228()
        {
            // Plik ustawień ze starszej wersji, sprzed #268.
            File.WriteAllText(Path.Combine(_dir, "settings.json"), "{\"DefaultPort\":3391}");
            var loaded = SettingsStore.Load(_dir);
            Assert.Equal(3391, loaded.DefaultPort);
            Assert.Equal(228.0, loaded.SidebarWidth);
        }

        [Fact]
        public void Persistence_NoSettingsFile_LoadsAs228()
        {
            Assert.Equal(228.0, SettingsStore.Load(_dir).SidebarWidth);
        }

        [Fact]
        public void Persistence_SecondSaveOverwritesWidth()
        {
            SettingsStore.Save(new AppSettings { SidebarWidth = 250 }, _dir);
            var loaded = SettingsStore.Load(_dir);
            loaded.SidebarWidth = 400;
            SettingsStore.Save(loaded, _dir);
            Assert.Equal(400.0, SettingsStore.Load(_dir).SidebarWidth);
        }
    }
}
