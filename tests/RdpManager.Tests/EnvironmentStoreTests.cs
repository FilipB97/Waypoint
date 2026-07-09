using System;
using System.Collections.Generic;
using System.IO;
using RdpManager;
using RdpManager.Models;
using Xunit;

namespace RdpManager.Tests
{
    // Globalny store środowisk (environments.json) + migracja z per-kolekcja (rest.json).
    public class EnvironmentStoreTests : IDisposable
    {
        private readonly string _dir;

        public EnvironmentStoreTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "waypoint-env-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static RestEnvironment Env(string name, params (string k, string v)[] vars)
        {
            var e = new RestEnvironment { Name = name };
            foreach (var (k, v) in vars) e.Variables.Add(new RestVariable { Key = k, Value = v });
            return e;
        }

        [Fact]
        public void SaveLoad_RoundTrips()
        {
            EnvironmentStore.Save(new List<RestEnvironment> { Env("dev", ("token", "abc")) }, _dir);
            var loaded = EnvironmentStore.Load(_dir);
            Assert.Single(loaded);
            Assert.Equal("dev", loaded[0].Name);
            Assert.Equal("abc", loaded[0].Variables[0].Value);
        }

        [Fact]
        public void Load_MissingFile_MigratesEnvironmentsFromCollections()
        {
            var coll = new RestCollection();
            coll.Environments.Add(Env("dev", ("base", "https://dev")));
            RestStore.Save(new Dictionary<string, RestCollection> { ["s1"] = coll }, _dir);

            var envs = EnvironmentStore.Load(_dir);   // brak environments.json → migracja

            Assert.Single(envs);
            Assert.Equal("dev", envs[0].Name);
            Assert.True(File.Exists(Path.Combine(_dir, "environments.json")), "migracja powinna utrwalić plik");
        }

        [Fact]
        public void Migration_DedupsById_NotByName()
        {
            var shared = Env("shared");
            var c1 = new RestCollection();
            c1.Environments.Add(shared);
            var c2 = new RestCollection();
            c2.Environments.Add(new RestEnvironment { Id = shared.Id, Name = "shared" });   // ten sam Id (jak po DeepCopy)
            c2.Environments.Add(Env("dev"));                                                 // inny Id → zachowany
            RestStore.Save(new Dictionary<string, RestCollection> { ["a"] = c1, ["b"] = c2 }, _dir);

            var envs = EnvironmentStore.Load(_dir);

            Assert.Equal(2, envs.Count);                       // shared (raz) + dev
            Assert.Single(envs, e => e.Id == shared.Id);       // dedup po Id
        }

        [Fact]
        public void Migration_SameNameDifferentId_BothKept_NoDataLoss()
        {
            var c1 = new RestCollection(); c1.Environments.Add(Env("dev", ("k", "1")));
            var c2 = new RestCollection(); c2.Environments.Add(Env("dev", ("k", "2")));   // ta sama nazwa, inny Id
            RestStore.Save(new Dictionary<string, RestCollection> { ["a"] = c1, ["b"] = c2 }, _dir);

            var envs = EnvironmentStore.Load(_dir);

            Assert.Equal(2, envs.Count);   // dedup po Id (nie po nazwie) — nie gubimy odrębnych środowisk
        }

        [Fact]
        public void Load_MissingFileAndNoCollections_ReturnsEmptyAndCreatesFile()
        {
            var envs = EnvironmentStore.Load(_dir);
            Assert.Empty(envs);
            Assert.True(File.Exists(Path.Combine(_dir, "environments.json")), "pusty plik = znacznik migracji");
        }

        [Fact]
        public void Load_CorruptFile_PreservesCorruptAndReturnsEmpty()
        {
            var path = Path.Combine(_dir, "environments.json");
            File.WriteAllText(path, "[ to nie jest json");
            var envs = EnvironmentStore.Load(_dir);
            Assert.Empty(envs);
            Assert.True(File.Exists(path + ".corrupt"), "uszkodzony plik powinien trafić do .corrupt");
        }

        [Fact]
        public void Load_RecoversFromBackupWhenRevertedExternally()
        {
            var path = Path.Combine(_dir, "environments.json");
            File.WriteAllText(path + ".bak", "[{\"Id\":\"a\",\"Name\":\"one\"},{\"Id\":\"b\",\"Name\":\"two\"}]");
            File.WriteAllText(path, "[{\"Id\":\"a\",\"Name\":\"one\"}]");                 // cofnięty (uboższy)
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));                 // starszy
            File.SetLastWriteTimeUtc(path + ".bak", DateTime.UtcNow);                     // .bak nowszy i bogatszy

            var envs = EnvironmentStore.Load(_dir);
            Assert.Equal(2, envs.Count);   // przywrócono z .bak
        }
    }
}
