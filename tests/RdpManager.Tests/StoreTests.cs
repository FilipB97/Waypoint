using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RdpManager;
using RdpManager.Models;
using Xunit;

namespace RdpManager.Tests
{
    public class StoreTests : IDisposable
    {
        private readonly string _dir;

        public StoreTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "RdpManagerTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* najlepszy wysiłek */ }
        }

        [Fact]
        public void SettingsStore_RoundTripsToDisk()
        {
            var settings = new AppSettings
            {
                DefaultPort = 3390,
                UiScale = 1.25,
                ColorDepth = 24,
                AutoReconnect = false,
                RecentIds = new List<string> { "a", "b" }
            };

            SettingsStore.Save(settings, _dir);
            var loaded = SettingsStore.Load(_dir);

            Assert.Equal(3390, loaded.DefaultPort);
            Assert.Equal(1.25, loaded.UiScale);
            Assert.Equal(24, loaded.ColorDepth);
            Assert.False(loaded.AutoReconnect);
            Assert.Equal(new[] { "a", "b" }, loaded.RecentIds);
        }

        [Fact]
        public void SettingsStore_ReturnsDefaultsWhenMissing()
        {
            var loaded = SettingsStore.Load(_dir);
            Assert.Equal(3389, loaded.DefaultPort);
            Assert.Equal(1.0, loaded.UiScale);
        }

        [Fact]
        public void ServerRepository_RoundTripsWithoutPersistingPassword()
        {
            var servers = new List<ServerInfo>
            {
                new ServerInfo { Name = "srv1", Host = "10.0.0.1", Username = "admin", AuthenticationLevel = 1 },
                new ServerInfo { Name = "srv2", Host = "example.com", SavePassword = true },
            };

            ServerRepository.Save(servers, _dir);
            var loaded = ServerRepository.Load(_dir);

            Assert.Equal(2, loaded.Count);
            Assert.Equal("srv1", loaded[0].Name);
            Assert.Equal("admin", loaded[0].Username);
            Assert.Equal(1, loaded[0].AuthenticationLevel);
            Assert.True(loaded[1].SavePassword);

            // Model nie ma pola z hasłem, a klucz Credential Managera jest [JsonIgnore] —
            // do pliku trafia tylko flaga SavePassword, nigdy żaden sekret ani jego lokalizacja.
            var json = File.ReadAllText(Path.Combine(_dir, "servers.json"));
            Assert.DoesNotContain("CredTarget", json);
        }

        [Fact]
        public void ServerRepository_SeedsSafeSampleDataOnFirstRun()
        {
            var loaded = ServerRepository.Load(_dir); // pusty katalog -> seed

            Assert.NotEmpty(loaded);
            // Seed nie może zawierać realnych/prywatnych IP — tylko neutralne przykłady.
            Assert.All(loaded, s =>
                Assert.False(s.Host.StartsWith("10.") || s.Host.StartsWith("192.168."),
                    "Seed nie powinien zawierać prywatnych adresów IP: " + s.Host));
        }

        [Fact]
        public void ServerRepository_SeedPersistsSoSecondLoadMatches()
        {
            var first = ServerRepository.Load(_dir);
            var second = ServerRepository.Load(_dir);
            Assert.Equal(first.Count, second.Count);
        }

        [Fact]
        public void SettingsStore_PreservesUnknownFieldsFromNewerVersion()
        {
            // Plik zapisany przez NOWSZĄ wersję z polem, którego ta wersja nie zna (regresja autostartu).
            var path = Path.Combine(_dir, "settings.json");
            File.WriteAllText(path, "{\"DefaultPort\":3391,\"FutureFeatureX\":[\"keep-me\"]}");

            var loaded = SettingsStore.Load(_dir);   // nieznane pole -> [JsonExtensionData]
            SettingsStore.Save(loaded, _dir);        // ...i z powrotem na dysk

            Assert.Equal(3391, loaded.DefaultPort);
            var json = File.ReadAllText(path);
            Assert.Contains("FutureFeatureX", json);   // starszy build NIE zjada pola nowszego
            Assert.Contains("keep-me", json);
        }

        [Fact]
        public void SettingsStore_BacksUpPreviousFileOnSave()
        {
            SettingsStore.Save(new AppSettings { DefaultPort = 5001 }, _dir);
            SettingsStore.Save(new AppSettings { DefaultPort = 5002 }, _dir);

            var bak = Path.Combine(_dir, "settings.json.bak");
            Assert.True(File.Exists(bak), "Zapis powinien zostawić kopię .bak");
            Assert.Contains("5001", File.ReadAllText(bak));   // .bak = wersja sprzed ostatniego zapisu
        }

        [Fact]
        public void SettingsStore_PreservesCorruptFileInsteadOfLosingIt()
        {
            var path = Path.Combine(_dir, "settings.json");
            File.WriteAllText(path, "{ to nie jest poprawny json ");

            var loaded = SettingsStore.Load(_dir);   // nie wysypuje się — zwraca domyślne
            Assert.Equal(3389, loaded.DefaultPort);

            var corrupt = Path.Combine(_dir, "settings.json.corrupt");
            Assert.True(File.Exists(corrupt), "Uszkodzony plik powinien zostać zachowany jako .corrupt");
            Assert.Contains("to nie jest poprawny json", File.ReadAllText(corrupt));
        }

        [Fact]
        public void ServerRepository_PreservesCorruptFileInsteadOfOverwritingRealData()
        {
            var path = Path.Combine(_dir, "servers.json");
            File.WriteAllText(path, "[ { \"Name\": \"realny-serwer\", \"Host\": \"10.9.9.9\"  <-- uciety");

            var loaded = ServerRepository.Load(_dir);   // uszkodzony -> seed, ale oryginał zachowany
            Assert.NotEmpty(loaded);

            var corrupt = Path.Combine(_dir, "servers.json.corrupt");
            Assert.True(File.Exists(corrupt), "Uszkodzony servers.json powinien trafić do .corrupt");
            Assert.Contains("realny-serwer", File.ReadAllText(corrupt));
        }

        [Fact]
        public void ServerRepository_PreservesUnknownServerFieldsFromNewerVersion()
        {
            var path = Path.Combine(_dir, "servers.json");
            File.WriteAllText(path, "[{\"Name\":\"srv\",\"Host\":\"h\",\"FutureServerField\":\"keep\"}]");

            var loaded = ServerRepository.Load(_dir);
            ServerRepository.Save(loaded, _dir);

            var json = File.ReadAllText(path);
            Assert.Contains("FutureServerField", json);
            Assert.Contains("keep", json);
        }

        [Fact]
        public void SettingsStore_RecoversSettingsWhenFileWasRevertedExternally()
        {
            // Sygnatura rollbacku z zewnątrz (np. antywirus): .bak NOWSZY niż settings.json.
            var path = Path.Combine(_dir, "settings.json");
            File.WriteAllText(path + ".bak", "{\"DefaultPort\":4444}");            // dobra kopia
            File.WriteAllText(path, "{\"DefaultPort\":3389}");                     // cofnięty plik
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));          // starszy niż .bak
            File.SetLastWriteTimeUtc(path + ".bak", DateTime.UtcNow);

            var loaded = SettingsStore.Load(_dir);
            Assert.Equal(4444, loaded.DefaultPort);   // przywrócono z .bak, nie z cofniętego pliku
        }

        [Fact]
        public void SettingsStore_KeepsCurrentFileWhenNewerThanBackup()
        {
            // Normalna sytuacja: bieżący plik nowszy niż .bak — NIE przywracamy.
            var path = Path.Combine(_dir, "settings.json");
            File.WriteAllText(path + ".bak", "{\"DefaultPort\":4444}");
            File.WriteAllText(path, "{\"DefaultPort\":3389}");
            File.SetLastWriteTimeUtc(path + ".bak", DateTime.UtcNow.AddHours(-1));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);

            var loaded = SettingsStore.Load(_dir);
            Assert.Equal(3389, loaded.DefaultPort);   // bieżący plik wygrywa
        }

        [Fact]
        public void ServerRepository_RecoversServersWhenFileWasRevertedExternally()
        {
            var path = Path.Combine(_dir, "servers.json");
            File.WriteAllText(path + ".bak", "[{\"Name\":\"prawdziwy\",\"Host\":\"h\"}]");
            File.WriteAllText(path, "[]");                                         // cofnięty do pustej listy
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
            File.SetLastWriteTimeUtc(path + ".bak", DateTime.UtcNow);

            var loaded = ServerRepository.Load(_dir);
            Assert.Single(loaded);
            Assert.Equal("prawdziwy", loaded[0].Name);   // przywrócono z .bak, nie seed/pusta lista
        }

        [Fact]
        public void SettingsStore_DoesNotRestorePoorerBackupOverRicherFile()
        {
            // „Bujanie" AV: .bak NOWSZY, ale UBOŻSZY (domyślne) niż bogaty bieżący plik — NIE cofamy.
            var path = Path.Combine(_dir, "settings.json");
            File.WriteAllText(path,
                "{\"AutoConnectServerIds\":[\"a\",\"b\"],\"TabGroups\":[{\"Name\":\"G\"}]}");   // bogaty
            File.WriteAllText(path + ".bak", "{\"DefaultPort\":3389}");                          // ubogi
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));                        // bogaty STARSZY
            File.SetLastWriteTimeUtc(path + ".bak", DateTime.UtcNow);                            // ubogi NOWSZY

            var loaded = SettingsStore.Load(_dir);
            Assert.Equal(2, loaded.AutoConnectServerIds.Count);   // zachowano bogate dane, nie cofnięto
            Assert.Single(loaded.TabGroups);
        }

        [Fact]
        public void ServerRepository_RespectsDeletionWhenCurrentFileIsNewer()
        {
            // Świadome usunięcie serwerów: bieżący plik ma MNIEJ, ale jest NOWSZY niż .bak → NIE przywracamy.
            var path = Path.Combine(_dir, "servers.json");
            File.WriteAllText(path + ".bak", "[{\"Name\":\"a\"},{\"Name\":\"b\"},{\"Name\":\"c\"}]");   // 3 (stare)
            File.WriteAllText(path, "[{\"Name\":\"a\"}]");                                              // 1 (po usunięciu)
            File.SetLastWriteTimeUtc(path + ".bak", DateTime.UtcNow.AddHours(-1));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);

            var loaded = ServerRepository.Load(_dir);
            Assert.Single(loaded);   // usunięcie uszanowane (bak starszy → self-heal się nie odpala)
        }
    }
}
