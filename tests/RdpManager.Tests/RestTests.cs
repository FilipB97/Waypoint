using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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

        [Fact]
        public void BuildUri_PreservesPercentEncodingInPath_NoDecoding()
        {
            // Bez wyłączonej kanonikalizacji System.Uri dekodowało %7E→~ i %2F→/ — psując podpisane URL-e.
            var u = RestClient.BuildRequestUri(new RestRequest { Url = "https://x.test/a%7Eb%2Fc" });
            Assert.Contains("%7E", u.AbsoluteUri);
            Assert.Contains("%2F", u.AbsoluteUri);
            Assert.DoesNotContain("~", u.AbsoluteUri);
        }

        [Fact]
        public void BuildUri_PreservesDotSegmentsAndPlusLiterally()
        {
            // /../ nie jest zwijane, a + w query zostaje + (nie zamieniane) — dosłownie to, co wpisał użytkownik.
            var u = RestClient.BuildRequestUri(new RestRequest { Url = "https://x.test/a/../b?q=1+2" });
            Assert.Equal("https://x.test/a/../b?q=1+2", u.AbsoluteUri);
        }

        [Fact]
        public void NormalizeNewlines_ConvertsCrlfAndLoneCrToLf()
        {
            Assert.Equal("a\nb\nc", RestClient.NormalizeNewlines("a\r\nb\rc"));
            Assert.Equal("", RestClient.NormalizeNewlines(null));
            Assert.Equal("{\n  \"x\": 1\n}", RestClient.NormalizeNewlines("{\r\n  \"x\": 1\r\n}"));
        }

        [Fact]
        public void BuildFormBody_SubstitutesThenEncodesValues()
        {
            var vars = new Dictionary<string, string> { ["u"] = "bob", ["sec"] = "a+b/c=" };
            var b = RestClient.BuildFormBody("username={{u}}&client_secret={{sec}}&grant_type=password", vars);
            Assert.Equal("username=bob&client_secret=a%2Bb%2Fc%3D&grant_type=password", b);   // +,/,= zakodowane PO podstawieniu
        }

        [Fact]
        public void BuildFormBodyFromFields_SkipsDisabledAndEmptyKey_EncodesAfterSubstitute()
        {
            var vars = new Dictionary<string, string> { ["u"] = "bob", ["sec"] = "a+b/c=" };
            var fields = new List<RestKeyValue>
            {
                new RestKeyValue { Key = "username", Value = "{{u}}", Enabled = true },
                new RestKeyValue { Key = "client_secret", Value = "{{sec}}", Enabled = true },
                new RestKeyValue { Key = "skip", Value = "x", Enabled = false },
                new RestKeyValue { Key = "", Value = "y", Enabled = true },
            };
            var b = RestClient.BuildFormBodyFromFields(fields, vars);
            Assert.Equal("username=bob&client_secret=a%2Bb%2Fc%3D", b);
        }

        [Fact]
        public void BuildFormBody_EmptyAndNoVars()
        {
            Assert.Equal("", RestClient.BuildFormBody("", null));
            Assert.Equal("grant_type=password", RestClient.BuildFormBody("grant_type=password", null));
        }

        [Fact]
        public void AddDefaultHeaders_AddsAcceptAndUserAgentWhenMissing()
        {
            using var m = new HttpRequestMessage(HttpMethod.Get, "https://x/y");
            RestClient.AddDefaultHeaders(m);
            Assert.True(m.Headers.Contains("Accept"));
            Assert.True(m.Headers.Contains("User-Agent"));
            Assert.StartsWith("Waypoint/", string.Concat(m.Headers.GetValues("User-Agent")));
        }

        [Fact]
        public void AddDefaultHeaders_DoesNotOverwriteExplicit()
        {
            using var m = new HttpRequestMessage(HttpMethod.Get, "https://x/y");
            m.Headers.TryAddWithoutValidation("Accept", "application/json");
            RestClient.AddDefaultHeaders(m);
            Assert.Equal("application/json", string.Concat(m.Headers.GetValues("Accept")));
        }

        [Fact]
        public async Task ReadBoundedAsync_UnderLimit_ReturnsAllBytes()
        {
            byte[] data = Encoding.UTF8.GetBytes("{\"ok\":true}");
            using var stream = new MemoryStream(data);
            byte[] result = await RestClient.ReadBoundedAsync(stream, RestClient.MaxResponseBytes, CancellationToken.None);
            Assert.Equal(data, result);
        }

        [Fact]
        public async Task ReadBoundedAsync_OverLimit_ReturnsNull()
        {
            byte[] data = new byte[1000];
            using var stream = new MemoryStream(data);
            byte[] result = await RestClient.ReadBoundedAsync(stream, maxBytes: 100, CancellationToken.None);
            Assert.Null(result);
        }

        [Fact]
        public async Task ReadBoundedAsync_ExactlyAtLimit_ReturnsBytes()
        {
            byte[] data = new byte[100];
            using var stream = new MemoryStream(data);
            byte[] result = await RestClient.ReadBoundedAsync(stream, maxBytes: 100, CancellationToken.None);
            Assert.Equal(100, result.Length);
        }

        [Fact]
        public async Task ReadBoundedAsync_Empty_ReturnsEmptyArray()
        {
            using var stream = new MemoryStream(Array.Empty<byte>());
            byte[] result = await RestClient.ReadBoundedAsync(stream, RestClient.MaxResponseBytes, CancellationToken.None);
            Assert.Empty(result);
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
        public void SaveLoad_PreservesHistory()
        {
            string dir = TempDir();
            try
            {
                var data = new Dictionary<string, RestCollection>
                {
                    ["srv1"] = new RestCollection
                    {
                        History = { new RestHistoryEntry { Method = "GET", Url = "https://api.test/x", Status = 200, ElapsedMs = 42, WhenIso = "2026-07-08 10:00:00" } }
                    }
                };
                RestStore.Save(data, dir);
                var h = RestStore.Load(dir)["srv1"].History;
                Assert.Single(h);
                Assert.Equal(200, h[0].Status);
                Assert.Equal("GET", h[0].Method);
                Assert.Equal(42, h[0].ElapsedMs);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void SaveLoad_PreservesEnvironments()
        {
            string dir = TempDir();
            try
            {
                var env = new RestEnvironment { Name = "dev", Variables = { new RestVariable { Key = "token", Value = "abc" } } };
                var data = new Dictionary<string, RestCollection>
                {
                    ["srv1"] = new RestCollection { Environments = { env }, ActiveEnvironmentId = env.Id }
                };
                RestStore.Save(data, dir);
                var loaded = RestStore.Load(dir)["srv1"];
                Assert.Single(loaded.Environments);
                Assert.Equal("dev", loaded.Environments[0].Name);
                Assert.Equal("token", loaded.Environments[0].Variables[0].Key);
                Assert.Equal("abc", loaded.Environments[0].Variables[0].Value);
                Assert.Equal(env.Id, loaded.ActiveEnvironmentId);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // SchemaVersion (B5/C5): Save() stempluje bieżącą wersję; plik sprzed jej wprowadzenia (brak pola
        // w JSON) deserializuje się jako 0 — właśnie ten brak znacznika opisuje przegląd (AuthType=3=Inherit).
        [Fact]
        public void SaveLoad_StampsCurrentSchemaVersion()
        {
            string dir = TempDir();
            try
            {
                RestStore.Save(new Dictionary<string, RestCollection> { ["srv1"] = new RestCollection() }, dir);
                var loaded = RestStore.Load(dir)["srv1"];
                Assert.Equal(RestCollection.CurrentSchemaVersion, loaded.SchemaVersion);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void SaveLoad_FileWithoutSchemaVersion_DefaultsToZero()
        {
            string dir = TempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "rest.json"), "{\"srv1\":{\"BaseUrl\":\"https://api.test\"}}");
                var loaded = RestStore.Load(dir)["srv1"];
                Assert.Equal(0, loaded.SchemaVersion);
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

        [Fact]
        public void DeepCopy_FreshRequestIds_ClearsHistory_KeepsData()
        {
            var src = new RestCollection
            {
                BaseUrl = "https://x",
                Folders = { new RestFolder { Name = "F" } },
                Requests = { new RestRequest { Name = "R", Method = "POST", Url = "https://x/y" } },
                Environments = { new RestEnvironment { Name = "dev", Variables = { new RestVariable { Key = "k", Value = "v" } } } },
                History = { new RestHistoryEntry { Method = "GET" } }
            };
            string srcReqId = src.Requests[0].Id;

            var copy = RestStore.DeepCopy(src, out var reqMap, out _);

            Assert.Single(copy.Requests);
            Assert.NotEqual(srcReqId, copy.Requests[0].Id);        // świeże Id
            Assert.Equal(copy.Requests[0].Id, reqMap[srcReqId]);   // mapa stare→nowe
            Assert.Equal("POST", copy.Requests[0].Method);
            Assert.Single(copy.Folders);
            Assert.Equal("v", copy.Environments[0].Variables[0].Value);
            Assert.Empty(copy.History);                            // historia wyczyszczona
            // źródło nietknięte
            Assert.Equal(srcReqId, src.Requests[0].Id);
            Assert.Single(src.History);
        }

        [Fact]
        public void DeepCopy_FreshFolderIds_RewritesParentAndFolderIdReferences()
        {
            var src = new RestCollection();
            var parent = new RestFolder { Name = "Parent" };
            var child = new RestFolder { Name = "Child", ParentId = parent.Id };
            src.Folders.Add(parent);
            src.Folders.Add(child);
            src.Requests.Add(new RestRequest { Name = "R", FolderId = child.Id });
            string parentOldId = parent.Id, childOldId = child.Id;

            var copy = RestStore.DeepCopy(src, out _, out var folderMap);

            var newParent = copy.Folders.First(f => f.Name == "Parent");
            var newChild = copy.Folders.First(f => f.Name == "Child");
            Assert.NotEqual(parentOldId, newParent.Id);
            Assert.NotEqual(childOldId, newChild.Id);
            Assert.Equal(newParent.Id, folderMap[parentOldId]);
            Assert.Equal(newChild.Id, folderMap[childOldId]);
            Assert.Equal(newParent.Id, newChild.ParentId);         // referencja przepisana na nowe Id
            Assert.Equal(newChild.Id, copy.Requests[0].FolderId);  // referencja żądania też przepisana
            // źródło nietknięte
            Assert.Equal(parentOldId, parent.Id);
            Assert.Equal(parentOldId, child.ParentId);
        }
    }
}
