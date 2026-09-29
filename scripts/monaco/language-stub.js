// Waypoint: serwis języka WYCIĘTY z osadzonej paczki Monaco (patrz Assets/monaco/README.md).
// Podświetlanie składni daje basic-languages; tu zostaje tylko pusty moduł o kształcie, jakiego
// oczekuje kontrybucja w editor.main — inaczej otwarcie pliku .css/.html/.js/.ts rzucałoby 404.
define([], function () {
  var none = { dispose: function () {} };
  function notBundled() { return Promise.reject(new Error('Language service not bundled in Waypoint')); }
  return {
    setupMode: function () { return none; },
    setupTypeScript: function () { return none; },
    setupJavaScript: function () { return none; },
    getTypeScriptWorker: notBundled,
    getJavaScriptWorker: notBundled
  };
});
