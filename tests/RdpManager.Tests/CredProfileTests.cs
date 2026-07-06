using System;
using System.Collections.Generic;
using System.IO;
using RdpManager;
using RdpManager.Models;
using Xunit;

namespace RdpManager.Tests
{
    // Repozytorium profili poświadczeń — te same gwarancje co ServerRepository: brak sekretów w JSON,
    // kwarantanna uszkodzonego pliku, forward-compat, self-heal z .bak (z poszanowaniem świadomego usunięcia).
    public class CredProfileTests : IDisposable
    {
        private readonly string _dir;

        public CredProfileTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "RdpManagerTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* najlepszy wysiłek */ }
        }

        [Fact]
        public void RoundTripsWithoutPersistingCredTarget()
        {
            var profiles = new List<CredentialProfile>
            {
                new CredentialProfile { Name = "Domena ACME", Domain = "ACME", Username = "admin" },
                new CredentialProfile { Name = "SSH root", Username = "root" },
            };

            CredentialProfileRepository.Save(profiles, _dir);
            var loaded = CredentialProfileRepository.Load(_dir);

            Assert.Equal(2, loaded.Count);
            Assert.Equal("Domena ACME", loaded[0].Name);
            Assert.Equal("ACME", loaded[0].Domain);
            Assert.Equal("admin", loaded[0].Username);
            Assert.Equal("root", loaded[1].Username);

            // CredTarget jest [JsonIgnore] — do pliku nie trafia lokalizacja hasła w Credential Manager.
            var json = File.ReadAllText(Path.Combine(_dir, "credprofiles.json"));
            Assert.DoesNotContain("CredTarget", json);
        }

        [Fact]
        public void ReturnsEmptyWhenMissing_NoSeed()
        {
            var loaded = CredentialProfileRepository.Load(_dir);
            Assert.Empty(loaded);   // bez seedu — pierwsze uruchomienie = pusta lista
        }

        [Fact]
        public void PreservesCorruptFileInsteadOfLosingData()
        {
            var path = Path.Combine(_dir, "credprofiles.json");
            File.WriteAllText(path, "[ { \"Name\": \"prawdziwy-profil\"  <-- uciety");

            var loaded = CredentialProfileRepository.Load(_dir);
            Assert.Empty(loaded);

            var corrupt = path + ".corrupt";
            Assert.True(File.Exists(corrupt), "Uszkodzony credprofiles.json powinien trafić do .corrupt");
            Assert.Contains("prawdziwy-profil", File.ReadAllText(corrupt));
        }

        [Fact]
        public void PreservesUnknownFieldsFromNewerVersion()
        {
            var path = Path.Combine(_dir, "credprofiles.json");
            File.WriteAllText(path, "[{\"Name\":\"p\",\"Username\":\"u\",\"FutureField\":\"keep\"}]");

            var loaded = CredentialProfileRepository.Load(_dir);
            CredentialProfileRepository.Save(loaded, _dir);

            var json = File.ReadAllText(path);
            Assert.Contains("FutureField", json);
            Assert.Contains("keep", json);
        }

        [Fact]
        public void RecoversWhenFileWasRevertedExternally()
        {
            var path = Path.Combine(_dir, "credprofiles.json");
            File.WriteAllText(path + ".bak", "[{\"Name\":\"prawdziwy\",\"Username\":\"u\"}]");
            File.WriteAllText(path, "[]");                                  // cofnięty do pustej listy
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
            File.SetLastWriteTimeUtc(path + ".bak", DateTime.UtcNow);

            var loaded = CredentialProfileRepository.Load(_dir);
            Assert.Single(loaded);
            Assert.Equal("prawdziwy", loaded[0].Name);   // przywrócono z .bak, nie pustą listę
        }

        [Fact]
        public void RespectsDeletionWhenCurrentFileIsNewer()
        {
            var path = Path.Combine(_dir, "credprofiles.json");
            File.WriteAllText(path + ".bak", "[{\"Name\":\"a\"},{\"Name\":\"b\"}]");   // 2 (stare)
            File.WriteAllText(path, "[{\"Name\":\"a\"}]");                             // 1 (po usunięciu)
            File.SetLastWriteTimeUtc(path + ".bak", DateTime.UtcNow.AddHours(-1));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);

            var loaded = CredentialProfileRepository.Load(_dir);
            Assert.Single(loaded);   // usunięcie uszanowane (bak starszy → self-heal się nie odpala)
        }
    }
}
