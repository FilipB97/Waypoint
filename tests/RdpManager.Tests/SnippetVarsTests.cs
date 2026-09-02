using System.Collections.Generic;
using RdpManager.Core;
using RdpManager.Models;
using Xunit;

namespace RdpManager.Tests
{
    // Podstawianie zmiennych w snippetach idzie do POWŁOKI, gdzie klamry mają własne znaczenie. Te testy
    // pilnują granicy: co jest zmienną snippetu, a co zwykłym tekstem komendy, którego nie wolno ruszyć.
    public class SnippetVarsTests
    {
        private static ServerInfo Srv() => new ServerInfo
        {
            Name = "app-01",
            Host = "10.0.0.5",
            Port = 22,
            Username = "root",
            Group = "Produkcja",
            Domain = "corp",
            Protocol = RemoteProtocol.Ssh
        };

        [Fact]
        public void PodstawiaZmienneSerwera()
        {
            Assert.Equal("ssh root@10.0.0.5 -p 22", SnippetVars.Expand("ssh {user}@{host} -p {port}", Srv()));
            Assert.Equal("app-01 / Produkcja / ssh", SnippetVars.Expand("{name} / {group} / {protocol}", Srv()));
        }

        [Fact]
        public void WielkoscLiterNazwyNieMaZnaczenia()
            => Assert.Equal("10.0.0.5", SnippetVars.Expand("{HOST}", Srv()));

        [Fact]
        public void SkladniaAwkPrzechodziBezZmian()
        {
            // To jest właściwy powód, dla którego podstawianie jest zachowawcze: „{print $1}" wygląda
            // jak zmienna, a jest programem awk. Wycięcie go zmieniłoby komendę w cichą awarię.
            const string cmd = "ps aux | awk '{print $1}' | sort | uniq -c";
            Assert.Equal(cmd, SnippetVars.Expand(cmd, Srv()));
        }

        [Fact]
        public void ZmiennaPowlokiZostajeZmiennaPowloki()
        {
            // ${host} należy do zdalnej powłoki. Podstawienie tutaj podmieniłoby jej zmienną na naszą.
            Assert.Equal("echo ${host}", SnippetVars.Expand("echo ${host}", Srv()));
            Assert.Equal("echo ${HOME}/log", SnippetVars.Expand("echo ${HOME}/log", Srv()));
        }

        [Fact]
        public void PodwojonaKlamraDajeKlamreDoslowna()
            => Assert.Equal("{host}", SnippetVars.Expand("{{host}}", Srv()));

        [Fact]
        public void NieznanaNazwaZostajeDoslownie()
        {
            // Ciche skasowanie fragmentu komendy jest gorsze niż komenda, po której widać, że nie zadziałała.
            Assert.Equal("systemctl status {serwis}", SnippetVars.Expand("systemctl status {serwis}", Srv()));
        }

        [Fact]
        public void NiedomknietaKlamraNieGubiTekstu()
            => Assert.Equal("find / -name '{host", SnippetVars.Expand("find / -name '{host", Srv()));

        [Fact]
        public void PusteWartosciDajaPustyCiagANieNull()
        {
            var s = new ServerInfo { Host = null, Username = null, Port = 23 };
            Assert.Equal("|| 23", SnippetVars.Expand("{host}|{user}| {port}", s));
        }

        [Fact]
        public void BrakSerweraNieWysypujePodstawiania()
            => Assert.Equal("| ", SnippetVars.Expand("{host}| {user}", (ServerInfo)null));

        [Fact]
        public void SlownikWolajacegoDzialaBezWzgleduNaPorownywaczKluczy()
        {
            // Wariant testowalny przyjmuje zwykły słownik — bez OrdinalIgnoreCase też ma trafiać.
            var values = new Dictionary<string, string> { ["host"] = "h1" };
            Assert.Equal("h1", SnippetVars.Expand("{Host}", values));
        }

        [Fact]
        public void EnterToCarriageReturn_TakJakZKlawiatury()
        {
            // xterm zgłasza Enter jako CR (onData), więc snippet musi wysłać dokładnie to samo — inaczej
            // zachowywałby się inaczej niż ta sama komenda wpisana ręcznie.
            Assert.Equal("uptime\r", SnippetVars.ToKeystrokes("uptime", sendEnter: true));
            Assert.Equal("uptime", SnippetVars.ToKeystrokes("uptime", sendEnter: false));
        }

        [Fact]
        public void LamaniaWierszyZPolaTekstowegoIdaJakoCR()
        {
            // Pole tekstowe WPF daje CRLF; wysłanie tego wprost dałoby na wielu urządzeniach pusty
            // dodatkowy wiersz (a na części — komendę wykonaną dwa razy).
            Assert.Equal("cd /var/log\rls -la\r", SnippetVars.ToKeystrokes("cd /var/log\r\nls -la", sendEnter: true));
            Assert.Equal("a\rb\r", SnippetVars.ToKeystrokes("a\nb", sendEnter: true));
        }

        [Fact]
        public void EnterNieDublujeSieGdyTrescJuzGoMa()
            => Assert.Equal("uptime\r", SnippetVars.ToKeystrokes("uptime\n", sendEnter: true));

        [Fact]
        public void WszystkiePodpowiadaneNazwySaRozpoznawane()
        {
            // Lista Names trafia do podpowiedzi w oknie snippetów — nazwa, której nie da się podstawić,
            // byłaby tam obietnicą bez pokrycia.
            foreach (var n in SnippetVars.Names)
                Assert.NotEqual("{" + n + "}", SnippetVars.Expand("{" + n + "}", Srv()));
        }
    }
}
