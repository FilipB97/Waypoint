using System.Collections.Generic;
using System.Linq;
using RdpManager.Models;
using RdpManager.ViewModels;
using Xunit;

namespace RdpManager.Tests
{
    public class MainViewModelTests
    {
        private static ServerInfo Srv(string name, string host, ServerStatus status = ServerStatus.Offline)
            => new ServerInfo { Name = name, Host = host, Status = status };

        [Fact]
        public void AddRemove_UpdatesCountsAndLookup()
        {
            var vm = new MainViewModel();
            var a = Srv("a", "h1", ServerStatus.Online);
            var b = Srv("b", "h2");

            vm.Add(a);
            vm.Add(b);

            Assert.Equal(2, vm.Total);
            Assert.Equal(1, vm.OnlineCount);
            Assert.Same(a, vm.FindById(a.Id));

            vm.Remove(a);
            Assert.Equal(1, vm.Total);
            Assert.Equal(0, vm.OnlineCount);
            Assert.Null(vm.FindById(a.Id));
        }

        [Fact]
        public void Filter_MatchesNameOrHost()
        {
            var vm = new MainViewModel();
            vm.Add(Srv("prod-web", "10.0.0.1"));
            vm.Add(Srv("db", "example.com"));

            Assert.Single(vm.Filter("web"));
            Assert.Single(vm.Filter("example"));
            Assert.Equal(2, vm.Filter("").Count());
        }

        [Fact]
        public void RecordRecent_MovesToFrontDedupsAndCaps()
        {
            var vm = new MainViewModel();
            vm.RecordRecent("a");
            vm.RecordRecent("b");
            vm.RecordRecent("a");   // ponowne -> na początek, bez duplikatu

            Assert.Equal(new[] { "a", "b" }, vm.RecentIds);

            for (int i = 0; i < 20; i++) vm.RecordRecent("id" + i, max: 5);
            Assert.Equal(5, vm.RecentIds.Count);
            Assert.Equal("id19", vm.RecentIds[0]);
        }

        [Fact]
        public void RecentServers_PreservesOrderAndSkipsMissing()
        {
            var vm = new MainViewModel();
            var a = Srv("a", "h1");
            var b = Srv("b", "h2");
            vm.Add(a);
            vm.Add(b);

            vm.RecordRecent(b.Id);
            vm.RecordRecent("ghost");   // usunięty serwer
            vm.RecordRecent(a.Id);

            var recent = vm.RecentServers().ToList();
            Assert.Equal(new[] { a, b }, recent);   // a najnowsze, ghost pominięty
        }

        [Fact]
        public void UseRecentIds_SharesListReferenceWithSettings()
        {
            var settingsRecents = new List<string>();
            var vm = new MainViewModel();
            vm.UseRecentIds(settingsRecents);

            vm.RecordRecent("x");
            Assert.Contains("x", settingsRecents);   // mutacja widoczna w ustawieniach -> zapis je utrwali
        }

        [Fact]
        public void LoadServers_ReplacesCollection()
        {
            var vm = new MainViewModel();
            vm.Add(Srv("old", "h"));
            vm.LoadServers(new[] { Srv("new1", "h1"), Srv("new2", "h2") });

            Assert.Equal(2, vm.Total);
            Assert.DoesNotContain(vm.Servers, s => s.Name == "old");
        }
    }
}
