using System;
using System.Collections.Generic;
using System.IO;
using RdpManager;
using RdpManager.Models;
using Xunit;

namespace RdpManager.Tests
{
    public class RestClientUriTests
    {
        private static RestRequest Req(string url, params (string k, string v, bool en)[] ps)
        {
            var r = new RestRequest { Url = url };
            foreach (var p in ps) r.QueryParams.Add(new RestKeyValue { Key = p.k, Value = p.v, Enabled = p.en });
            return r;
        }

        [Fact]
        public void BuildUri_PrependsHttpsWhenSchemeMissing()
        {
            var u = RestClient.BuildRequestUri(new RestRequest { Url = "api.example.com/v1" });
            Assert.Equal("https://api.example.com/v1", u.AbsoluteUri);
        }

        [Fact]
        public void BuildUri_AppendsEnabledParams_SkipsDisabledAndEmptyKeys()
        {
            var u = RestClient.BuildRequestUri(Req("https://x.test/a",
                ("q", "1", true), ("skip", "no", false), ("", "x", true), ("t", "a b", true)));
            Assert.Equal("https://x.test/a?q=1&t=a%20b", u.AbsoluteUri);
        }

        [Fact]
        public void BuildUri_MergesWithExistingQuery()
        {
            var u = RestClient.BuildRequestUri(Req("https://x.test/a?z=0", ("q", "1", true)));
            Assert.Equal("https://x.test/a?z=0&q=1", u.AbsoluteUri);
        }

        [Fact]
        public void Subst_ReplacesKnown_LeavesUnknownAndNull()
        {
            var vars = new Dictionary<string, string> { ["a"] = "1", ["name"] = "bob" };
            Assert.Equal("1/bob", RestClient.Subst("{{a}}/{{name}}", vars));
            Assert.Equal("1{{b}}", RestClient.Subst("{{a}}{{b}}", vars));   // nieznana zostaje
            Assert.Equal("{{a}}", RestClient.Subst("{{a}}", null));         // brak zmiennych = bez zmian
        }

        [Fact]
        public void BuildUri_SubstitutesVariablesInUrlAndQuery()
        {
            var vars = new Dictionary<string, string> { ["base"] = "https://x.test", ["v"] = "9" };
            var u = RestClient.BuildRequestUri(Req("{{base}}/u", ("id", "{{v}}", true)), vars);
            Assert.Equal("https://x.test/u?id=9", u.AbsoluteUri);
        }
    }

    public class RestStoreTests
    {
        private static string TempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "waypoint-rest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void SaveLoad_RoundTrips()
        {
            string dir = TempDir();
            try
            {
                var data = new Dictionary<string, RestCollection>
                {
                    ["srv1"] = new RestCollection
                    {
                        BaseUrl = "https://api.test",
                        Requests = { new RestRequest { Name = "R", Method = "POST", Url = "https://api.test/x", Body = "{}" } }
                    }
                };
                RestStore.Save(data, dir);
                var loaded = RestStore.Load(dir);
                Assert.True(loaded.ContainsKey("srv1"));
                Assert.Equal("https://api.test", loaded["srv1"].BaseUrl);
                Assert.Single(loaded["srv1"].Requests);
                Assert.Equal("POST", loaded["srv1"].Requests[0].Method);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void Load_MissingFile_ReturnsEmpty()
        {
            string dir = TempDir();
            try { Assert.Empty(RestStore.Load(dir)); }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void Load_CorruptFile_ReturnsEmptyAndDoesNotThrow()
        {
            string dir = TempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "rest.json"), "{ this is not json");
                Assert.Empty(RestStore.Load(dir));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
