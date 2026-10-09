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

ARCHS=(--arch arm64 --arch x86_64)
swift build --package-path "$MAC_DIR/App" -c release "${ARCHS[@]}"
BIN_DIR="$(swift build --package-path "$MAC_DIR/App" -c release "${ARCHS[@]}" --show-bin-path)"

rm -rf "$DIST"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN_DIR/Waypoint" "$APP/Contents/MacOS/Waypoint"

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
</dict>
</plist>
PLIST

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
