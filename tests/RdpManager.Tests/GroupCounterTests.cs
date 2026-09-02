using System.Collections.Generic;
using System.Linq;
using RdpManager.Core;
using RdpManager.Models;
using Xunit;

namespace RdpManager.Tests
{
    // Licznik przy nagłówku grupy serwerów.
    //
    // Zasada, której pilnują te testy: znacznik należy się stanowi WYMAGAJĄCEMU UWAGI, nie normie.
    // Zwinięcie grupy nie może jednak ukryć informacji, że część serwerów nie odpowiada — bo jedynym
    // sposobem, żeby się o tym dowiedzieć, byłoby rozwijanie każdej grupy po kolei.
    public class GroupCounterTests
    {
        private static List<ServerInfo> Servers(params ServerStatus[] statuses)
            => statuses.Select((st, i) => new ServerInfo { Name = "s" + i, Status = st }).ToList();

        [Fact]
        public void RozwinietaGrupaPokazujeSamaLiczbe()
        {
            // Stany widać w wierszach pod spodem — powtarzanie ich w nagłówku nic nie wnosi.
            var c = GroupCounter.For(Servers(ServerStatus.Online, ServerStatus.Offline), collapsed: false);

            Assert.False(c.ShowsOffline);
            Assert.Equal(2, c.Total);
            Assert.Equal(1, c.Offline);   // policzone, choć niepokazywane
        }

        [Fact]
        public void ZwinietaGrupaBezNiedostepnychTezPokazujeSamaLiczbe()
        {
            var c = GroupCounter.For(Servers(ServerStatus.Online, ServerStatus.Online), collapsed: true);

            Assert.False(c.ShowsOffline);
            Assert.Equal(0, c.Offline);
        }

        [Fact]
        public void ZwinietaGrupaZNiedostepnymiMowiOTym()
        {
            var c = GroupCounter.For(Servers(ServerStatus.Online, ServerStatus.Offline, ServerStatus.Online),
                                     collapsed: true);

            Assert.True(c.ShowsOffline);
            Assert.Equal(1, c.Offline);
            Assert.Equal(3, c.Total);
        }

        [Fact]
        public void ZwinietaGrupaCalaNiedostepna()
        {
            var c = GroupCounter.For(Servers(ServerStatus.Offline, ServerStatus.Offline), collapsed: true);

            Assert.True(c.ShowsOffline);
            Assert.Equal(2, c.Offline);
            Assert.Equal(2, c.Total);
        }

        [Fact]
        public void StatusIdleNieLiczySieJakoNiedostepny()
        {
            // „Wolna odpowiedź" to nie „brak odpowiedzi" — wskaźnik ma alarmować o drugim.
            var c = GroupCounter.For(Servers(ServerStatus.Idle, ServerStatus.Idle), collapsed: true);

            Assert.False(c.ShowsOffline);
            Assert.Equal(0, c.Offline);
        }

        [Fact]
        public void PustaGrupaNieWysypujeSiIPokazujeZero()
        {
            var c = GroupCounter.For(new List<ServerInfo>(), collapsed: true);

            Assert.False(c.ShowsOffline);
            Assert.Equal(0, c.Total);
        }

        [Fact]
        public void BrakListyJestTraktowanyJakPusta()
        {
            var c = GroupCounter.For(null, collapsed: true);

            Assert.False(c.ShowsOffline);
            Assert.Equal(0, c.Total);
            Assert.Equal(0, c.Offline);
        }

        [Fact]
        public void LeniweWyliczenieJestPrzegladaneRAZ()
        {
            // For przyjmuje IEnumerable; gdyby przechodził je dwa razy (raz na Count, raz na filtr),
            // źródło jednorazowe dałoby zero niedostępnych przy drugim przejściu.
            int passes = 0;
            IEnumerable<ServerInfo> Once()
            {
                passes++;
                yield return new ServerInfo { Name = "a", Status = ServerStatus.Offline };
                yield return new ServerInfo { Name = "b", Status = ServerStatus.Online };
            }

            var c = GroupCounter.For(Once(), collapsed: true);

            Assert.Equal(1, passes);
            Assert.Equal(2, c.Total);
            Assert.Equal(1, c.Offline);
        }
    }
}
