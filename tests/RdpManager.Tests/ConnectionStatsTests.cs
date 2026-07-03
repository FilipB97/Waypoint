using System;
using RdpManager.Core;
using Xunit;

namespace RdpManager.Tests
{
    public class ConnectionStatsTests
    {
        // Dokładnie 2 spacje po 19-znakowym znaczniku czasu — jak w RdpUtils.FormatConnectionLog.
        private static readonly string[] Lines =
        {
            "2026-07-03 09:00:00  CONNECTED    web1 (10.0.0.1:3389) user=a",
            "2026-07-03 10:00:00  CONNECTED    web1 (10.0.0.1:3389) user=a",
            "2026-07-02 08:00:00  CONNECTED    db1 (10.0.0.2:22) user=b",
            "2026-07-03 11:00:00  DISCONNECTED web1 (10.0.0.1:3389) user=a",
            "2026-07-03 11:30:00  FAILED       db1 (10.0.0.2:22) user=b",
            "kompletny smiec bez sensu",
            "",
        };

        [Fact]
        public void Compute_CountsConnectedPerDayAndTopServers()
        {
            var now = new DateTime(2026, 7, 3, 12, 0, 0);
            var s = ConnectionStats.Compute(Lines, now, 7);

            Assert.Equal(3, s.TotalConnects);                       // 2x web1 + 1x db1 (DISCONNECTED/FAILED/śmieci pominięte)
            Assert.Equal(7, s.PerDay.Length);
            Assert.Equal(2, s.PerDay[6]);                           // dziś (07-03)
            Assert.Equal(1, s.PerDay[5]);                           // wczoraj (07-02)
            Assert.Equal(0, s.PerDay[0]);                           // 7 dni temu — pusto

            Assert.Equal("web1", s.TopServers[0].Key);
            Assert.Equal(2, s.TopServers[0].Value);
            Assert.Equal("db1", s.TopServers[1].Key);
        }

        [Fact]
        public void Compute_EmptyOrNull_IsSafe()
        {
            var s = ConnectionStats.Compute(null, new DateTime(2026, 7, 3), 14);
            Assert.Equal(0, s.TotalConnects);
            Assert.Equal(14, s.PerDay.Length);
            Assert.Empty(s.TopServers);
        }

        [Fact]
        public void Compute_IgnoresEventsOutsideDayWindow()
        {
            var now = new DateTime(2026, 7, 3, 12, 0, 0);
            var old = new[] { "2026-06-01 08:00:00  CONNECTED    web1 (10.0.0.1:3389) user=a" };
            var s = ConnectionStats.Compute(old, now, 7);
            Assert.Equal(1, s.TotalConnects);                       // policzone do sumy i top
            Assert.All(s.PerDay, d => Assert.Equal(0, d));          // ale poza oknem 7 dni
        }
    }
}
