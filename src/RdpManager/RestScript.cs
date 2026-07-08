using System;
using System.Collections.Generic;
using Jint;
using RdpManager.Models;

namespace RdpManager
{
    /// <summary>Wynik uruchomienia skryptu: logi (console.log), testy (pm.test) i ewentualny błąd wykonania.</summary>
    public sealed class ScriptOutcome
    {
        public bool Ok = true;
        public string Error = "";
        public List<string> Logs = new List<string>();
        public List<ScriptTest> Tests = new List<ScriptTest>();
        public bool IsEmpty => Ok && Logs.Count == 0 && Tests.Count == 0;
    }

    public sealed class ScriptTest
    {
        public string Name;
        public bool Passed;
        public string Error;
    }

    /// <summary>
    /// Uruchamia skrypty JS (silnik Jint, bez dostępu do CLR/systemu) z API w stylu Postmana: pm.environment,
    /// pm.variables, pm.request (pre), pm.response (po odpowiedzi), pm.test/pm.expect, console.log.
    /// Zmienne środowiska czyta/zapisuje przez wstrzyknięte delegaty (host decyduje, gdzie trafiają).
    /// </summary>
    public static class RestScript
    {
        public static ScriptOutcome Run(string script, RestRequest req, RestResponse resp,
            Func<string, string> getVar, Action<string, string> setVar, Action<string> unsetVar)
        {
            var o = new ScriptOutcome();
            if (string.IsNullOrWhiteSpace(script)) return o;
            try
            {
                var engine = new Engine(cfg => { cfg.LimitRecursion(64); cfg.TimeoutInterval(TimeSpan.FromSeconds(5)); });

                engine.SetValue("__get", (Func<string, string>)(k => getVar(k) ?? ""));
                engine.SetValue("__set", (Action<string, string>)((k, v) => setVar(k, v ?? "")));
                engine.SetValue("__unset", (Action<string>)(k => unsetVar(k)));
                engine.SetValue("__log", (Action<string>)(s => o.Logs.Add(s ?? "")));
                engine.SetValue("__test", (Action<string, bool, string>)((n, p, er) => o.Tests.Add(new ScriptTest { Name = n, Passed = p, Error = er })));

                engine.SetValue("__url", req?.Url ?? "");
                engine.SetValue("__method", req?.Method ?? "GET");
                engine.SetValue("__setUrl", (Action<string>)(v => { if (req != null) req.Url = v ?? ""; }));
                engine.SetValue("__setBody", (Action<string>)(v => { if (req != null) req.Body = v ?? ""; }));
                engine.SetValue("__addHeader", (Action<string, string>)((k, v) => req?.Headers.Add(new RestKeyValue { Key = k ?? "", Value = v ?? "" })));

                engine.SetValue("__code", resp?.Status ?? 0);
                engine.SetValue("__time", resp?.ElapsedMs ?? 0);
                engine.SetValue("__text", (Func<string>)(() => resp?.Body ?? ""));
                engine.SetValue("__header", (Func<string, string>)(HeaderLookup(resp)));

                engine.Execute(Prelude);
                engine.Execute(script);
            }
            catch (Exception ex) { o.Ok = false; o.Error = (ex.InnerException ?? ex).Message; }
            return o;
        }

        private static Func<string, string> HeaderLookup(RestResponse resp) => name =>
        {
            if (resp?.Headers == null) return "";
            foreach (var h in resp.Headers)
                if (string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)) return h.Value;
            return "";
        };

        // API wstrzykiwane przed skryptem użytkownika. Zbudowane na host-delegatach __xxx.
        private const string Prelude = @"
var pm = {
  environment: { get: function(k){return __get(k);}, set: function(k,v){__set(k,v);}, unset: function(k){__unset(k);} },
  variables:   { get: function(k){return __get(k);}, set: function(k,v){__set(k,v);} },
  request:  { url: __url, method: __method,
              setUrl: function(v){__setUrl(v);}, setBody: function(v){__setBody(v);}, addHeader: function(k,v){__addHeader(k,v);} },
  response: { code: __code, responseTime: __time,
              text: function(){return __text();}, json: function(){return JSON.parse(__text());},
              headers: { get: function(n){return __header(n);} } },
  test: function(name, fn){ try { fn(); __test(name, true, ''); } catch(e){ __test(name, false, String(e && e.message ? e.message : e)); } },
  expect: function(a){ return { to: {
      equal:   function(v){ if(a!==v) throw new Error('expected '+JSON.stringify(a)+' to equal '+JSON.stringify(v)); },
      eql:     function(v){ if(JSON.stringify(a)!==JSON.stringify(v)) throw new Error('expected deep equal'); },
      include: function(v){ if(String(a).indexOf(v)===-1) throw new Error('expected to include '+v); },
      be: { ok:    function(){ if(!a) throw new Error('expected truthy value'); },
            above: function(v){ if(!(a>v)) throw new Error('expected '+a+' > '+v); },
            below: function(v){ if(!(a<v)) throw new Error('expected '+a+' < '+v); } }
  } }; }
};
// Starsze API skryptów Postmana (sprzed pm.*, wciąż powszechne w kopiowanych kolekcjach) — te same magazyny co pm.environment.
var postman = {
  setEnvironmentVariable:   function(k,v){ __set(k,v); },
  getEnvironmentVariable:   function(k){ return __get(k); },
  clearEnvironmentVariable: function(k){ __unset(k); },
  setGlobalVariable:        function(k,v){ __set(k,v); },
  getGlobalVariable:        function(k){ return __get(k); },
  clearGlobalVariable:      function(k){ __unset(k); }
};
var console = { log: function(){ var s=''; for(var i=0;i<arguments.length;i++){ var x=arguments[i]; s+=(i?' ':'')+(typeof x==='object'?JSON.stringify(x):x); } __log(s); } };
";
    }
}
