using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RdpManager;
using Xunit;

namespace RdpManager.Tests
{
    // Migracja zapisanych kolorów grup kart przy wczytywaniu ustawień.
    //
    // #7C6CFB był PIERWSZYM kolorem palety grup kart, więc dostawała go pierwsza utworzona grupa —
    // a od akcentu #6C6DFF dzieli go ΔE 4,1, czyli znacznik grupy zlewał się z akcentem. Inaczej niż
    // przy akcencie użytkownika, tu nie da się wyczyścić do „domyślnego": grupa musi mieć jakiś kolor.
    public class TabGroupMigrationTests : IDisposable
    {
        private readonly string _dir;

        public TabGroupMigrationTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "waypoint-grp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private AppSettings SaveAndLoad(params string[] groupColors)
        {
            var s = new AppSettings
            {
                TabGroups = groupColors.Select((c, i) => new TabGroupDef
                { Name = "Grupa " + i, Color = c, ServerIds = new List<string> { "srv" + i } }).ToList()
            };
            SettingsStore.Save(s, _dir);
            return SettingsStore.Load(_dir);
        }

        [Fact]
        public void StaryFioletJestPodmienianyNaPierwszyKolorNowejPalety()
        {
            var loaded = SaveAndLoad("#FF7C6CFB");
            Assert.Equal("#FFD06BD8", loaded.TabGroups[0].Color);
        }

        [Fact]
        public void PodmianaJestNieczulaNaWielkoscLiter()
        {
            var loaded = SaveAndLoad("#ff7c6cfb");
            Assert.Equal("#FFD06BD8", loaded.TabGroups[0].Color);
        }

        [Fact]
        public void PozostaleKoloryZostajaNietkniete()
        {
            // Migracja celuje w JEDNĄ wartość. Kolor wybrany ręcznie przez użytkownika to jego decyzja
            // i nie wolno jej nadpisać przy okazji.
            var loaded = SaveAndLoad("#FF36B8C4", "#FF7C6CFB", "#FFFFB454");

            Assert.Equal("#FF36B8C4", loaded.TabGroups[0].Color);
            Assert.Equal("#FFD06BD8", loaded.TabGroups[1].Color);
            Assert.Equal("#FFFFB454", loaded.TabGroups[2].Color);
        }

        [Fact]
        public void MigracjaZnosiSiePrzyBrakuGrup()
        {
            SettingsStore.Save(new AppSettings(), _dir);
            var loaded = SettingsStore.Load(_dir);
            Assert.Empty(loaded.TabGroups);
        }

        [Fact]
        public void SamonaprawaZKopiiTakzeMigruje()
        {
            // Kopia zapasowa jest z definicji STARSZA od pliku głównego, więc to właśnie ona najczęściej
            // niesie wartości do przeniesienia. Ścieżka samonaprawy zwracała ją z pominięciem Migrate,
            // czyli odzyskanie ustawień cofało wszystkie migracje naraz.
            string path = Path.Combine(_dir, "settings.json");
            SaveAndLoad("#FF36B8C4");                                  // plik główny: kolor bez zmian
            File.Copy(path, path + ".bak", overwrite: true);
            File.WriteAllText(path + ".bak", File.ReadAllText(path).Replace("#FF36B8C4", "#FF7C6CFB"));
            File.SetLastWriteTimeUtc(path + ".bak", DateTime.UtcNow.AddMinutes(5));   // .bak nowszy → samonaprawa

            var loaded = SettingsStore.Load(_dir);

            Assert.Equal("#FFD06BD8", loaded.TabGroups[0].Color);
        }

        [Fact]
        public void NazwaIPrzynaleznoscGrupyPrzezywajaMigracje()
        {
            var loaded = SaveAndLoad("#FF7C6CFB");
            Assert.Equal("Grupa 0", loaded.TabGroups[0].Name);
            Assert.Equal(new[] { "srv0" }, loaded.TabGroups[0].ServerIds);
        }
    }
}
