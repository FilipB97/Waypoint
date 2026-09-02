using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RdpManager;
using RdpManager.Models;
using Xunit;

namespace RdpManager.Tests
{
    // Trwałość snippetów. Plik jest globalny i edytowalny ręcznie, więc store musi znieść zarówno
    // uszkodzenie, jak i wpisy o niepełnych danych — a kolejność musi przetrwać zapis, bo to ona
    // przypisuje skróty Ctrl+Shift+1..9.
    public class SnippetStoreTests : IDisposable
    {
        private readonly string _dir;

        public SnippetStoreTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "waypoint-snip-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private string File_ => Path.Combine(_dir, "snippets.json");

        private static CommandSnippet S(string name, string cmd, bool enter = true)
            => new CommandSnippet { Name = name, Command = cmd, SendEnter = enter };

        [Fact]
        public void BrakPlikuDajePustaListe()
            => Assert.Empty(SnippetStore.Load(_dir));

        [Fact]
        public void ZapisIOdczytZachowujaKolejnoscIPola()
        {
            SnippetStore.Save(new List<CommandSnippet> { S("a", "uptime"), S("b", "df -h", enter: false) }, _dir);

            var back = SnippetStore.Load(_dir);

            Assert.Equal(new[] { "a", "b" }, back.Select(x => x.Name));
            Assert.Equal("df -h", back[1].Command);
            Assert.False(back[1].SendEnter);
        }

        [Fact]
        public void WpisBezTresciJestOdsiewany()
        {
            // Pusty wpis zająłby numer skrótu i „wysyłał" nic — a numer jest pozycją na liście.
            SnippetStore.Save(new List<CommandSnippet> { S("pusty", "  "), S("realny", "uptime") }, _dir);

            var back = SnippetStore.Load(_dir);

            Assert.Single(back);
            Assert.Equal("realny", back[0].Name);
        }

        [Fact]
        public void WpisBezNazwyDostajePierwszyWierszKomendy()
        {
            SnippetStore.Save(new List<CommandSnippet> { S("", "tail -f /var/log/syslog\ngrep err") }, _dir);

            Assert.Equal("tail -f /var/log/syslog", SnippetStore.Load(_dir).Single().Name);
        }

        [Fact]
        public void WpisBezIdDostajeId()
        {
            System.IO.File.WriteAllText(File_, "[{\"Name\":\"x\",\"Command\":\"uptime\"}]");

            Assert.False(string.IsNullOrWhiteSpace(SnippetStore.Load(_dir).Single().Id));
        }

        [Fact]
        public void UszkodzonyPlikNieWywracaAplikacjiIJestZachowany()
        {
            System.IO.File.WriteAllText(File_, "{to nie jest json");

            Assert.Empty(SnippetStore.Load(_dir));
            Assert.True(System.IO.File.Exists(File_ + ".corrupt"), "Uszkodzony plik ma zostać odłożony, a nie skasowany");
        }

        [Fact]
        public void KopiaZapasowaWracaGdyPlikCofnietoZZewnatrz()
        {
            SnippetStore.Save(new List<CommandSnippet> { S("a", "1"), S("b", "2"), S("c", "3") }, _dir);
            System.IO.File.Copy(File_, File_ + ".bak", overwrite: true);

            // Plik podmieniony „z zewnątrz" na uboższy, ze STARSZYM czasem zapisu niż .bak.
            System.IO.File.WriteAllText(File_, "[]");
            System.IO.File.SetLastWriteTimeUtc(File_, DateTime.UtcNow.AddMinutes(-5));

            Assert.Equal(3, SnippetStore.Load(_dir).Count);
        }

        [Fact]
        public void SwiadomeUsuniecieNieJestWskrzeszane()
        {
            // Odwrotny przypadek do powyższego: .bak jest bogatszy, ale plik główny jest NOWSZY —
            // to zwykłe usunięcie wpisu przez użytkownika i nie wolno go cofać.
            SnippetStore.Save(new List<CommandSnippet> { S("a", "1"), S("b", "2") }, _dir);
            SnippetStore.Save(new List<CommandSnippet> { S("a", "1") }, _dir);

            Assert.Single(SnippetStore.Load(_dir));
        }

        [Fact]
        public void NazwaZastepczaJestPrzycietaDoJednegoWiersza()
        {
            Assert.Equal("ls -la", SnippetStore.FirstLine("  ls -la\r\nrm -rf /tmp/x"));
            Assert.Equal(48, SnippetStore.FirstLine(new string('x', 200)).Length);
            Assert.EndsWith("…", SnippetStore.FirstLine(new string('x', 200)));
        }
    }
}
