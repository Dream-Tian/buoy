#!/bin/sh
set -eu
root="$(CDPATH= cd -- "$(dirname "$0")/.." && pwd)"
publish="$root/artifacts/osx-arm64"
app="$root/artifacts/Buoy.app"
dmg="$root/artifacts/Buoy.dmg"

dotnet publish "$root/src/TokenFloatCross.App/TokenFloatCross.App.csproj" \
  -c Release -r osx-arm64 --self-contained true -o "$publish"

rm -rf "$app"
mkdir -p "$app/Contents/MacOS"
cp -R "$publish"/. "$app/Contents/MacOS/"
cat > "$app/Contents/Info.plist" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Buoy</string>
  <key>CFBundleExecutable</key><string>Buoy</string>
  <key>CFBundleIdentifier</key><string>app.buoy.desktop</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
</dict>
</plist>
EOF
chmod +x "$app/Contents/MacOS/Buoy"
rm -f "$dmg"
hdiutil create -volname Buoy -srcfolder "$app" -ov -format UDZO "$dmg"
echo "已生成 $dmg"
