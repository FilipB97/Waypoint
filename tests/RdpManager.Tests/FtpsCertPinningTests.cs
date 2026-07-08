using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RdpManager.Core;
using Xunit;

namespace RdpManager.Tests
{
    public class FtpsCertPinningTests
    {
        // Certyfikat efemeryczny (tylko w pamięci) — Fingerprint nie potrzebuje ważnego/zaufanego łańcucha,
        // tylko samych bajtów certyfikatu.
        private static X509Certificate2 MakeTestCert(string cn)
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        }

        [Fact]
        public void Fingerprint_IsDeterministic_DiffersPerCert()
        {
            var certA = MakeTestCert("a.example.com");
            var certB = MakeTestCert("b.example.com");

            string fpA1 = FtpsCertPinning.Fingerprint(certA);
            string fpA2 = FtpsCertPinning.Fingerprint(certA);
            string fpB = FtpsCertPinning.Fingerprint(certB);

            Assert.Equal(fpA1, fpA2);      // deterministyczny dla tego samego certyfikatu
            Assert.NotEqual(fpA1, fpB);    // różne certyfikaty → różny odcisk
            Assert.False(string.IsNullOrWhiteSpace(fpA1));
        }

        [Fact]
        public void Check_DistinguishesUnknownMatchMismatch()
        {
            var store = new Dictionary<string, string>();
            Assert.Equal(FtpsCertPinning.Status.Unknown, FtpsCertPinning.Check(store, "Host", 21, "ABC123"));

            store[FtpsCertPinning.EntryKey("host", 21)] = "ABC123";
            Assert.Equal(FtpsCertPinning.Status.Match, FtpsCertPinning.Check(store, "HOST", 21, "abc123"));      // host + odcisk bez wielkości liter
            Assert.Equal(FtpsCertPinning.Status.Mismatch, FtpsCertPinning.Check(store, "host", 21, "INNY"));
            Assert.Equal(FtpsCertPinning.Status.Unknown, FtpsCertPinning.Check(store, "host", 990, "ABC123"));   // inny port = inny wpis
        }

        [Fact]
        public void SaveLoad_RoundTrips()
        {
            string dir = Path.Combine(Path.GetTempPath(), "waypoint-tests-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new Dictionary<string, string> { [FtpsCertPinning.EntryKey("h", 21)] = "ABC" };
                FtpsCertPinning.Save(dir, store);
                var loaded = FtpsCertPinning.Load(dir);
                Assert.Equal("ABC", loaded[FtpsCertPinning.EntryKey("h", 21)]);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void Load_MissingOrCorrupt_ReturnsEmpty()
        {
            string dir = Path.Combine(Path.GetTempPath(), "waypoint-tests-" + Guid.NewGuid().ToString("N"));
            try
            {
                Assert.Empty(FtpsCertPinning.Load(dir));   // brak pliku
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "ftps_certs.json"), "{nie-json");
                Assert.Empty(FtpsCertPinning.Load(dir));   // uszkodzony plik
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void Load_Corrupt_QuarantinesAndNotifies()
        {
            string dir = Path.Combine(Path.GetTempPath(), "waypoint-tests-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "ftps_certs.json");
                File.WriteAllText(path, "{uszkodzony");
                HealthNotices.Drain();

                var result = FtpsCertPinning.Load(dir);

                Assert.Empty(result);
                Assert.True(File.Exists(path + ".corrupt"));
                Assert.Contains(HealthNotices.Drain(),
                    n => n.Kind == HealthNoticeKind.FileQuarantined && n.Detail == "ftps_certs.json");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
