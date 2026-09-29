#!/usr/bin/env python3
"""
Buduje src/RdpManager/Assets/monaco/monaco-<wersja>.zip z oficjalnej paczki npm monaco-editor.

Użycie:  python3 scripts/monaco/build-bundle.py [wersja]      (domyślnie 0.52.2)
Wymaga:  npm (tylko do `npm pack`), Python 3.8+.

Co i dlaczego (szczegóły: src/RdpManager/Assets/monaco/README.md):
  * bierzemy klasyczne min/vs (AMD + loader.js) — od 0.53 paczka ma inny układ, stąd przypięta wersja;
  * zostają: loader.js, editor/, base/, basic-languages/ (podświetlanie), language/json (walidacja);
  * wycięte: serwisy języków TypeScript/CSS/HTML (~6 MB) i tłumaczenia nls.messages.* (~1,8 MB);
    w miejsce serwisów trafia pusty moduł (language-stub.js), bo editor.main i tak po nie sięga;
  * LICENSE i ThirdPartyNotices.txt trafiają do repo i do zipa;
  * zip jest deterministyczny (stała data, posortowane wpisy) — ten sam wejściowy tgz daje ten sam plik.
"""
import io, os, subprocess, sys, tarfile, tempfile, zipfile

VERSION = sys.argv[1] if len(sys.argv) > 1 else "0.52.2"
HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
OUT_DIR = os.path.join(REPO, "src", "RdpManager", "Assets", "monaco")
STUB = open(os.path.join(HERE, "language-stub.js"), "rb").read()
STUBS = ["vs/language/css/cssMode.js", "vs/language/html/htmlMode.js", "vs/language/typescript/tsMode.js"]
KEEP = ("vs/loader.js", "vs/editor/", "vs/base/", "vs/basic-languages/", "vs/language/json/")
FIXED_TIME = (2024, 1, 1, 0, 0, 0)

def keep(rel):
    if not rel.startswith(KEEP): return False
    if "/nls.messages." in rel or rel.split("/")[-1].startswith("nls.messages."): return False
    return not rel.endswith(".map")

def main():
    with tempfile.TemporaryDirectory() as tmp:
        subprocess.run(["npm", "pack", f"monaco-editor@{VERSION}", "--silent"], cwd=tmp, check=True,
                       shell=(os.name == "nt"))
        tgz = next(f for f in os.listdir(tmp) if f.endswith(".tgz"))
        files = {}
        with tarfile.open(os.path.join(tmp, tgz)) as t:
            for m in t.getmembers():
                if not m.isfile(): continue
                name = m.name[len("package/"):]
                if name in ("LICENSE", "ThirdPartyNotices.txt"):
                    # Licencja jedzie i w repo (widoczna w przeglądzie), i w zipie (czyli w exe) — MIT
                    # wymaga dołączenia informacji o licencji do każdej kopii.
                    data = t.extractfile(m).read()
                    open(os.path.join(OUT_DIR, name), "wb").write(data)
                    files[name] = data
                    continue
                if not name.startswith("min/vs/"): continue
                rel = name[len("min/"):]
                if keep(rel): files[rel] = t.extractfile(m).read()
        for s in STUBS: files[s] = STUB

    out = os.path.join(OUT_DIR, f"monaco-{VERSION}.zip")
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for rel in sorted(files):
            zi = zipfile.ZipInfo(rel, FIXED_TIME)
            zi.compress_type = zipfile.ZIP_DEFLATED
            zi.external_attr = 0o644 << 16
            z.writestr(zi, files[rel])
    raw = sum(len(v) for v in files.values())
    print(f"{out}: {len(files)} plików, {raw/1048576:.1f} MB rozpakowane, {os.path.getsize(out)/1048576:.2f} MB zip")

if __name__ == "__main__":
    os.makedirs(OUT_DIR, exist_ok=True)
    main()
