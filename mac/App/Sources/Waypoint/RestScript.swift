import Foundation
import JavaScriptCore
import WaypointCore

/// Wynik skryptu: logi (console.log), testy (pm.test) i ewentualny błąd.
struct ScriptOutcome {
    var ok = true
    var error = ""
    var logs: [String] = []
    var tests: [(name: String, passed: Bool, error: String)] = []
    var isEmpty: Bool { ok && logs.isEmpty && tests.isEmpty }
}

/// Skrypty pre-request / test w stylu Postmana — to samo API co w Windows (pm.environment, pm.request,
/// pm.response, pm.test/pm.expect, stare postman.* i responseBody/tests), na JavaScriptCore.
/// Kontekst bez dostępu do systemu (sam JS); limit czasu 5 s jak w Windows.
enum RestScript {
    final class Bridge {
        var vars: [String: String]
        var request: RestRequest
        let response: RestResponse?
        var outcome = ScriptOutcome()
        init(vars: [String: String], request: RestRequest, response: RestResponse?) {
            self.vars = vars; self.request = request; self.response = response
        }
    }

    /// Uruchamia skrypt; zwraca wynik, zmienione zmienne środowiska i (dla pre-request) zmienione żądanie.
    static func run(_ script: String, request: RestRequest, response: RestResponse?, vars: [String: String])
        -> (outcome: ScriptOutcome, vars: [String: String], request: RestRequest) {
        guard !script.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return (ScriptOutcome(), vars, request) }
        let b = Bridge(vars: vars, request: request, response: response)
        let done = DispatchSemaphore(value: 0)
        // Osobny wątek: nieskończona pętla w skrypcie nie zamrozi okna — po 5 s zgłaszamy przekroczenie czasu.
        let thread = Thread {
            execute(script, b)
            done.signal()
        }
        thread.stackSize = 4 << 20
        thread.start()
        if done.wait(timeout: .now() + 5) == .timedOut {
            var o = ScriptOutcome()
            o.ok = false
            o.error = "Timeout (5 s)"
            return (o, vars, request)
        }
        return (b.outcome, b.vars, b.request)
    }

    private static func execute(_ script: String, _ b: Bridge) {
        guard let ctx = JSContext() else { b.outcome.ok = false; b.outcome.error = "JavaScriptCore"; return }
        ctx.exceptionHandler = { _, ex in
            b.outcome.ok = false
            b.outcome.error = ex?.toString() ?? "error"
        }
        func set(_ name: String, _ v: Any) { ctx.setObject(v, forKeyedSubscript: name as NSString) }
        let get: @convention(block) (String) -> String = { b.vars[$0] ?? "" }
        let put: @convention(block) (String, String) -> Void = { b.vars[$0] = $1 }
        let unset: @convention(block) (String) -> Void = { b.vars.removeValue(forKey: $0) }
        let log: @convention(block) (String) -> Void = { b.outcome.logs.append($0) }
        let test: @convention(block) (String, Bool, String) -> Void = { b.outcome.tests.append(($0, $1, $2)) }
        let setUrl: @convention(block) (String) -> Void = { b.request.url = $0 }
        let setBody: @convention(block) (String) -> Void = { b.request.body = $0 }
        let addHeader: @convention(block) (String, String) -> Void = { b.request.headers.append(RestKeyValue(key: $0, value: $1)) }
        let text: @convention(block) () -> String = { b.response?.body ?? "" }
        let header: @convention(block) (String) -> String = { b.response?.header($0) ?? "" }
        set("__get", get); set("__set", put); set("__unset", unset); set("__log", log); set("__test", test)
        set("__setUrl", setUrl); set("__setBody", setBody); set("__addHeader", addHeader)
        set("__text", text); set("__header", header)
        set("__url", b.request.url); set("__method", b.request.method)
        set("__code", b.response?.status ?? 0); set("__time", b.response?.elapsedMs ?? 0)
        ctx.evaluateScript(prelude)
        guard b.outcome.ok else { return }
        ctx.evaluateScript(script)
        guard b.outcome.ok else { return }
        ctx.evaluateScript(epilogue)
    }

    static let prelude = #"""
    var pm = {
      environment: { get: function(k){return __get(k);}, set: function(k,v){__set(k,String(v));}, unset: function(k){__unset(k);} },
      variables:   { get: function(k){return __get(k);}, set: function(k,v){__set(k,String(v));} },
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
    var postman = {
      setEnvironmentVariable:   function(k,v){ __set(k,String(v)); },
      getEnvironmentVariable:   function(k){ return __get(k); },
      clearEnvironmentVariable: function(k){ __unset(k); },
      setGlobalVariable:        function(k,v){ __set(k,String(v)); },
      getGlobalVariable:        function(k){ return __get(k); },
      clearGlobalVariable:      function(k){ __unset(k); },
      getResponseHeader:        function(n){ return __header(n); }
    };
    var responseBody = __text();
    var responseCode = { code: __code, name: '', detail: '' };
    var tests = {};
    var console = { log: function(){ var s=''; for(var i=0;i<arguments.length;i++){ var x=arguments[i]; s+=(i?' ':'')+(typeof x==='object'?JSON.stringify(x):x); } __log(s); } };
    """#

    static let epilogue = #"""
    for (var __k in tests) { if (Object.prototype.hasOwnProperty.call(tests, __k)) __test(__k, !!tests[__k], ''); }
    """#
}
