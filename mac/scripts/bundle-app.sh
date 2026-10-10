#!/bin/bash
# Składa Waypoint.app z pliku wykonywalnego SwiftPM: uniwersalny (arm64 + x86_64), Info.plist,
# ikona z mac/Resources/AppIcon-1024.png i podpis ad-hoc. Wynik: mac/dist/Waypoint.app i Waypoint-mac.zip.
#
# Użycie (na macOS z Xcode / Command Line Tools):   mac/scripts/bundle-app.sh
# Zmienne: VERSION (np. 0.1.0), BUILD (numer kompilacji).
#
# Podpis ad-hoc wystarcza do uruchomienia na własnym Macu. Pobrana paczka bez podpisu Developer ID
# i notaryzacji jest blokowana przez Gatekeeper — pierwsze uruchomienie: prawy klik → Otwórz
# (albo: xattr -dr com.apple.quarantine Waypoint.app).
set -euo pipefail

MAC_DIR="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="${VERSION:-0.1.0}"
BUILD="${BUILD:-1}"
DIST="$MAC_DIR/dist"
APP="$DIST/Waypoint.app"

# Każda architektura osobno, potem lipo. `--arch arm64 --arch x86_64` naraz przełącza SwiftPM na
# system budowania Xcode, który nie obsługuje wtyczki budowania z pakietu SwiftTerm.
BINS=()
for TRIPLE in arm64-apple-macosx14.0 x86_64-apple-macosx14.0; do
    swift build --package-path "$MAC_DIR/App" -c release --triple "$TRIPLE"
    BINS+=("$(swift build --package-path "$MAC_DIR/App" -c release --triple "$TRIPLE" --show-bin-path)/Waypoint")
done

rm -rf "$DIST"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
lipo -create "${BINS[@]}" -output "$APP/Contents/MacOS/Waypoint"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>Waypoint</string>
    <key>CFBundleDisplayName</key><string>Waypoint</string>
    <key>CFBundleIdentifier</key><string>io.github.filipb97.waypoint</string>
    <key>CFBundleExecutable</key><string>Waypoint</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>${VERSION}</string>
    <key>CFBundleVersion</key><string>${BUILD}</string>
    <key>CFBundleIconFile</key><string>AppIcon</string>
    <key>CFBundleDevelopmentRegion</key><string>pl</string>
    <key>CFBundleLocalizations</key><array><string>pl</string><string>en</string></array>
    <key>LSMinimumSystemVersion</key><string>14.0</string>
    <key>LSApplicationCategoryType</key><string>public.app-category.developer-tools</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>NSHumanReadableCopyright</key><string>© 2026 Filip Benklewski · MIT</string>
    <!-- Klient REST łączy się z dowolnymi adresami, także http:// (API w sieci firmowej, localhost). -->
    <key>NSAppTransportSecurity</key><dict><key>NSAllowsArbitraryLoads</key><true/></dict>
</dict>
</plist>
PLIST

# Edytor plików: Monaco (ta sama przycięta paczka co w wersji Windows) i ta sama strona edytora,
# rozpakowane do zasobów — serwuje je WKURLSchemeHandler (EditorWebView.swift).
REPO_DIR="$(cd "$MAC_DIR/.." && pwd)"
mkdir -p "$APP/Contents/Resources/monaco"
unzip -q "$REPO_DIR/src/RdpManager/Assets/monaco/monaco-0.52.2.zip" -d "$APP/Contents/Resources/monaco"
cp "$REPO_DIR/src/RdpManager/Assets/editor/index.html" "$APP/Contents/Resources/monaco/index.html"

# Ikona: zestaw rozmiarów z jednego PNG 1024 px (sips + iconutil są w każdym macOS).
ICON_SRC="$MAC_DIR/Resources/AppIcon-1024.png"
ICONSET="$DIST/AppIcon.iconset"
mkdir -p "$ICONSET"
for s in 16 32 128 256 512; do
    sips -z $s $s "$ICON_SRC" --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
    sips -z $((s*2)) $((s*2)) "$ICON_SRC" --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"
rm -rf "$ICONSET"

codesign --force --sign - --timestamp=none "$APP"
codesign --verify --strict "$APP"

(cd "$DIST" && ditto -c -k --keepParent Waypoint.app Waypoint-mac.zip)
echo "Gotowe: $APP"
lipo -info "$APP/Contents/MacOS/Waypoint"
