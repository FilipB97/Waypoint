using System;
using System.Collections.Generic;
using System.IO;
using RdpManager.Core;
using Xunit;

namespace RdpManager.Tests
{
    public class KnownHostsTests
    {
        [Fact]
        public void Fingerprint_IsOpenSshSha256Format()
        {
            var fp = KnownHosts.Fingerprint(new byte[] { 1, 2, 3 });
            Assert.StartsWith("SHA256:", fp);
            Assert.DoesNotContain("=", fp);
            Assert.Equal(fp, KnownHosts.Fingerprint(new byte[] { 1, 2, 3 }));      // deterministyczny
            Assert.NotEqual(fp, KnownHosts.Fingerprint(new byte[] { 1, 2, 4 }));
        }

        [Fact]
        public void Check_DistinguishesUnknownMatchMismatch()
        {
            var store = new Dictionary<string, string>();
            Assert.Equal(KnownHosts.Status.Unknown, KnownHosts.Check(store, "Host", 22, "SHA256:abc"));

            store[KnownHosts.EntryKey("host", 22)] = "SHA256:abc";
            Assert.Equal(KnownHosts.Status.Match, KnownHosts.Check(store, "HOST", 22, "SHA256:abc"));      // host bez wielkości liter
            Assert.Equal(KnownHosts.Status.Mismatch, KnownHosts.Check(store, "host", 22, "SHA256:inny"));
            Assert.Equal(KnownHosts.Status.Unknown, KnownHosts.Check(store, "host", 2222, "SHA256:abc"));  // inny port = inny wpis
        }

        [Fact]
        public void SaveLoad_RoundTrips()
        {
            string dir = Path.Combine(Path.GetTempPath(), "waypoint-tests-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new Dictionary<string, string> { [KnownHosts.EntryKey("h", 22)] = "SHA256:x" };
                KnownHosts.Save(dir, store);
                var loaded = KnownHosts.Load(dir);
                Assert.Equal("SHA256:x", loaded[KnownHosts.EntryKey("h", 22)]);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void Load_MissingOrCorrupt_ReturnsEmpty()
        {
            string dir = Path.Combine(Path.GetTempPath(), "waypoint-tests-" + Guid.NewGuid().ToString("N"));
            try
            {
                Assert.Empty(KnownHosts.Load(dir));   // brak pliku
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "known_hosts.json"), "{nie-json");
                Assert.Empty(KnownHosts.Load(dir));   // uszkodzony plik
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
