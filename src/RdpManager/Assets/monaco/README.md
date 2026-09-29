# Monaco (edytor plików)

`monaco-0.52.2.zip` to przycięta paczka [monaco-editor](https://github.com/microsoft/monaco-editor)
0.52.2 (MIT, © Microsoft — `LICENSE`, `ThirdPartyNotices.txt`; obie są też w zipie, więc jadą w exe).
Używa jej `FileEditorWindow`: zip jest zasobem WPF, przy pierwszym otwarciu edytora rozpakowuje się
**do pamięci**, a WebView2 dostaje pliki przez `WebResourceRequested` z wirtualnego źródła
`https://editor.waypoint.example/`. Nic nie idzie do sieci i nic nie jest zapisywane na dysk.

## Regeneracja

```
python3 scripts/monaco/build-bundle.py          # domyślnie 0.52.2
```

Skrypt pobiera paczkę przez `npm pack` i buduje zip **deterministycznie** (stała data, posortowane
wpisy) — ten sam tgz daje bajt w bajt ten sam plik, więc zmiana w repo oznacza zmianę treści.

## Dlaczego 0.52.2

To ostatnia wersja z klasycznym `min/vs` (AMD + `loader.js`). Od 0.53 paczka ma inny układ
(moduły ESM z haszowanymi nazwami), który wymagałby bundlera. Podniesienie wersji = osobna zmiana.

## Co wycięto (`min/vs`: 13,3 MB → 4,9 MB, zip 1,34 MB)

| Wycięte | Rozmiar | Dlaczego |
|---|---|---|
| `language/typescript` | 5,5 MB | serwis TS/JS (podpowiedzi, typy) — do edycji configów zbędny |
| `language/css`, `language/html` | 1,2 MB | jw. |
| `nls.messages.*` | 1,7 MB | tłumaczenia UI Monaco — zostaje angielski |

**Podświetlanie** wszystkich tych języków zostaje — daje je `basic-languages/`. Zostaje też
`language/json` (walidacja JSON w workerze — błąd składni jest podkreślany).

Kontrybucje w `editor.main` i tak sięgają po wycięte serwisy, więc w ich miejscu leży pusty moduł
AMD (`scripts/monaco/language-stub.js` jako `cssMode.js`, `htmlMode.js`, `tsMode.js`). Bez niego
otwarcie pliku `.css`/`.html`/`.js` kończyłoby się 404 i błędem strony.

## Sprawdzone (Chromium, ten sam układ co w WebView2)

Strona `Assets/editor/index.html` + zip serwowane z jednego źródła: 14 języków podświetla
(yaml, json, ini, shell, dockerfile, xml, html, css, js, ts, python, sql, markdown, powershell),
worker JSON zgłasza błędy, zero brakujących plików i zero błędów konsoli.
