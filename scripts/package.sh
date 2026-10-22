#!/usr/bin/env bash
# Builds a self-contained package of Quattromani Draw for one runtime and puts it in dist/.
#
#   scripts/package.sh <rid> [version]
#
#   rid      osx-arm64, osx-x64, win-x64 or linux-x64
#   version  defaults to the one in Directory.Build.props
#
# macOS: "Quattromani Draw.app" in a zip (ad-hoc signed, not notarized).  Windows: a zip.  Linux: a tar.gz.
set -euo pipefail

RID="${1:?usage: package.sh <rid> [version]}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="${2:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props" | head -1)}"
DIST="$ROOT/dist"
PUBLISH="$DIST/publish-$RID"
NAME="Quattromani-Draw-$VERSION-$RID"

rm -rf "$PUBLISH"
mkdir -p "$DIST"

dotnet publish "$ROOT/src/Quattromani.App" -c Release -r "$RID" --self-contained \
  -p:Version="$VERSION" -p:DebugType=none -p:DebugSymbols=false -o "$PUBLISH"

case "$RID" in
  osx-*)
    APP="$DIST/Quattromani Draw.app"
    rm -rf "$APP"
    mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
    cp -R "$PUBLISH/." "$APP/Contents/MacOS/"
    cp "$ROOT/src/Quattromani.App/Assets/icon.icns" "$APP/Contents/Resources/icon.icns"
    cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Quattromani Draw</string>
  <key>CFBundleDisplayName</key><string>Quattromani Draw</string>
  <key>CFBundleIdentifier</key><string>io.github.cbuzi.quattromani-draw</string>
  <key>CFBundleExecutable</key><string>Quattromani.App</string>
  <key>CFBundleIconFile</key><string>icon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSPrincipalClass</key><string>NSApplication</string>
</dict>
</plist>
PLIST
    chmod +x "$APP/Contents/MacOS/Quattromani.App"
    # Ad-hoc signature: no Apple account needed, and Apple Silicon refuses code that is not signed at all.
    codesign --force --deep --sign - "$APP"
    ditto -c -k --keepParent "$APP" "$DIST/$NAME.zip"
    rm -rf "$APP"
    ;;
  win-*)
    if command -v 7z >/dev/null 2>&1; then
      (cd "$PUBLISH" && 7z a -tzip -bd "$DIST/$NAME.zip" . >/dev/null)
    else
      powershell -NoProfile -Command "Compress-Archive -Path '$PUBLISH/*' -DestinationPath '$DIST/$NAME.zip' -Force"
    fi
    ;;
  linux-*)
    chmod +x "$PUBLISH/Quattromani.App"
    tar -czf "$DIST/$NAME.tar.gz" -C "$PUBLISH" .
    ;;
  *)
    echo "Unknown runtime: $RID" >&2
    exit 1
    ;;
esac

rm -rf "$PUBLISH"
echo "Created: $(ls "$DIST"/"$NAME".*)"
