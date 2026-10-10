import AppKit
import SwiftUI
import UniformTypeIdentifiers
import WebKit

/// Serwuje Monaco i stronę edytora z zasobów aplikacji pod `wpeditor://app/…` — bez sieci.
final class EditorSchemeHandler: NSObject, WKURLSchemeHandler {
    static let scheme = "wpeditor"
    private let root: URL

    init(root: URL) { self.root = root }

    func webView(_ webView: WKWebView, start task: WKURLSchemeTask) {
        guard let url = task.request.url else { return }
        var rel = url.path.isEmpty || url.path == "/" ? "/index.html" : url.path
        rel = rel.removingPercentEncoding ?? rel
        let file = root.appendingPathComponent(String(rel.dropFirst())).standardizedFileURL
        // Tylko pliki wewnątrz katalogu Monaco (żadnego „../" poza zasoby).
        guard file.path.hasPrefix(root.standardizedFileURL.path), let data = try? Data(contentsOf: file) else {
            task.didReceive(HTTPURLResponse(url: url, statusCode: 404, httpVersion: "HTTP/1.1", headerFields: [:])!)
            task.didFinish()
            return
        }
        let type = UTType(filenameExtension: file.pathExtension)?.preferredMIMEType ?? "application/octet-stream"
        let mime = file.pathExtension == "js" ? "application/javascript" : type
        task.didReceive(HTTPURLResponse(url: url, statusCode: 200, httpVersion: "HTTP/1.1",
                                        headerFields: ["Content-Type": mime, "Content-Length": String(data.count),
                                                       "Cache-Control": "no-store"])!)
        task.didReceive(data)
        task.didFinish()
    }

    func webView(_ webView: WKWebView, stop task: WKURLSchemeTask) {}
}

/// Most do strony edytora. Strona jest ta sama co w wersji Windows i rozmawia przez
/// `window.chrome.webview` (WebView2) — tu ten obiekt podstawia skrypt startowy, a wiadomości idą
/// przez WKScriptMessageHandler i evaluateJavaScript.
@MainActor
final class EditorBridge: NSObject, WKScriptMessageHandler, WKNavigationDelegate {
    let webView: WKWebView
    var onMessage: (([String: Any]) -> Void)?

    override init() {
        let config = WKWebViewConfiguration()
        let root = Bundle.main.resourceURL!.appendingPathComponent("monaco", isDirectory: true)
        config.setURLSchemeHandler(EditorSchemeHandler(root: root), forURLScheme: EditorSchemeHandler.scheme)
        let shim = """
        (function () {
          var listeners = [];
          window.chrome = window.chrome || {};
          window.chrome.webview = {
            postMessage: function (m) { window.webkit.messageHandlers.wp.postMessage(m); },
            addEventListener: function (t, f) { listeners.push(f); }
          };
          window.__wpReceive = function (m) { listeners.forEach(function (f) { f({ data: m }); }); };
        })();
        """
        config.userContentController.addUserScript(WKUserScript(source: shim, injectionTime: .atDocumentStart, forMainFrameOnly: true))
        webView = WKWebView(frame: .zero, configuration: config)
        super.init()
        config.userContentController.add(WeakHandler(self), name: "wp")
        webView.navigationDelegate = self
        webView.setValue(false, forKey: "drawsBackground")   // bez białego błysku przed wczytaniem Monaco
        webView.load(URLRequest(url: URL(string: "\(EditorSchemeHandler.scheme)://app/index.html")!))
    }

    func post(_ message: [String: Any]) {
        guard let data = try? JSONSerialization.data(withJSONObject: message),
              let json = String(data: data, encoding: .utf8) else { return }
        webView.evaluateJavaScript("window.__wpReceive(\(json))", completionHandler: nil)
    }

    func userContentController(_ ucc: WKUserContentController, didReceive message: WKScriptMessage) {
        guard let body = message.body as? [String: Any] else { return }
        onMessage?(body)
    }

    /// Strona nigdzie nie nawiguje; link w treści czy przeciągnięty plik nie wyprowadzą jej poza edytor.
    func webView(_ webView: WKWebView, decidePolicyFor action: WKNavigationAction,
                 decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        let ok = action.request.url?.scheme == EditorSchemeHandler.scheme
        decisionHandler(ok ? .allow : .cancel)
    }

    func teardown() {
        webView.configuration.userContentController.removeScriptMessageHandler(forName: "wp")
    }
}

/// WKUserContentController trzyma handler silnie — pośrednik ze słabą referencją zapobiega cyklowi.
private final class WeakHandler: NSObject, WKScriptMessageHandler {
    weak var target: WKScriptMessageHandler?
    init(_ t: WKScriptMessageHandler) { target = t }
    func userContentController(_ ucc: WKUserContentController, didReceive message: WKScriptMessage) {
        target?.userContentController(ucc, didReceive: message)
    }
}

struct EditorWebViewHost: NSViewRepresentable {
    let bridge: EditorBridge
    func makeNSView(context: Context) -> WKWebView { bridge.webView }
    func updateNSView(_ nsView: WKWebView, context: Context) {}
}
