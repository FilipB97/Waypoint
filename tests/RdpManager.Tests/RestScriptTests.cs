using System.Collections.Generic;
using System.Linq;
using RdpManager;
using RdpManager.Models;
using Xunit;

namespace RdpManager.Tests
{
    public class RestScriptTests
    {
        private static (System.Func<string, string> get, System.Action<string, string> set, System.Action<string> unset, Dictionary<string, string> store) Store(
            Dictionary<string, string> seed = null)
        {
            var s = seed ?? new Dictionary<string, string>();
            return (k => s.TryGetValue(k, out var v) ? v : "", (k, v) => s[k] = v, k => s.Remove(k), s);
        }

        [Fact]
        public void PostScript_SetsEnvVarFromResponseJson()
        {
            var (get, set, unset, store) = Store();
            var resp = new RestResponse { Ok = true, Status = 200, Body = "{\"access_token\":\"XYZ\"}" };
            var o = RestScript.Run("pm.environment.set('token', pm.response.json().access_token);", null, resp, get, set, unset);
            Assert.True(o.Ok, o.Error);
            Assert.Equal("XYZ", store["token"]);
        }

        [Fact]
        public void Test_RecordsPassAndFail()
        {
            var (get, set, unset, _) = Store();
            var o = RestScript.Run(
                "pm.test('ok', function(){ pm.expect(1).to.equal(1); });" +
                "pm.test('bad', function(){ pm.expect(1).to.equal(2); });",
                null, new RestResponse { Ok = true, Status = 200 }, get, set, unset);
            Assert.True(o.Ok, o.Error);
            Assert.Equal(2, o.Tests.Count);
            Assert.True(o.Tests[0].Passed);
            Assert.False(o.Tests[1].Passed);
        }

        [Fact]
        public void PreScript_MutatesRequestAndReadsVar()
        {
            var (get, set, unset, _) = Store(new Dictionary<string, string> { ["base"] = "https://api" });
            var req = new RestRequest { Url = "https://x", Method = "GET" };
            var o = RestScript.Run(
                "pm.request.setUrl(pm.environment.get('base') + '/users'); pm.request.addHeader('X-Test','1');",
                req, null, get, set, unset);
            Assert.True(o.Ok, o.Error);
            Assert.Equal("https://api/users", req.Url);
            Assert.Contains(req.Headers, h => h.Key == "X-Test" && h.Value == "1");
        }

        [Fact]
        public void ConsoleLog_Collected()
        {
            var (get, set, unset, _) = Store();
            var o = RestScript.Run("console.log('hello', 42);", null, null, get, set, unset);
            Assert.True(o.Ok, o.Error);
            Assert.Contains("hello 42", o.Logs);
        }

        [Fact]
        public void ScriptError_ReportsNotOk()
        {
            var (get, set, unset, _) = Store();
            var o = RestScript.Run("throw new Error('boom');", null, null, get, set, unset);
            Assert.False(o.Ok);
            Assert.Contains("boom", o.Error);
        }

        [Fact]
        public void LegacyPostmanApi_SetGetClearEnvironmentVariable_MatchesPmEnvironment()
        {
            var (get, set, unset, store) = Store();
            var resp = new RestResponse { Ok = true, Status = 200, Body = "{\"access_token\":\"XYZ\"}" };
            var o = RestScript.Run(
                "var d = pm.response.json(); postman.setEnvironmentVariable('token', d.access_token);" +
                "pm.test('readback', function(){ pm.expect(postman.getEnvironmentVariable('token')).to.equal('XYZ'); });" +
                "postman.clearEnvironmentVariable('token');",
                null, resp, get, set, unset);
            Assert.True(o.Ok, o.Error);
            Assert.True(o.Tests.Single().Passed, o.Tests.Single().Error);
            Assert.False(store.ContainsKey("token"));   // clearEnvironmentVariable usunął
        }

        [Fact]
        public void LegacyPostmanApi_GlobalVariable_SharesStoreWithEnvironment()
        {
            var (get, set, unset, store) = Store();
            var o = RestScript.Run("postman.setGlobalVariable('g','1');", null, null, get, set, unset);
            Assert.True(o.Ok, o.Error);
            Assert.Equal("1", store["g"]);
        }

        [Fact]
        public void EmptyScript_IsNoop()
        {
            var (get, set, unset, _) = Store();
            var o = RestScript.Run("   ", null, null, get, set, unset);
            Assert.True(o.Ok);
            Assert.True(o.IsEmpty);
        }
    }
}
