#!/bin/sh
set -eu
root="$(CDPATH= cd -- "$(dirname "$0")/.." && pwd)"
publish="$root/artifacts/osx-arm64"
app="$root/artifacts/Buoy.app"
dmg="$root/artifacts/Buoy.dmg"
widget="$root/macos-widget"

car_out="$root/artifacts/assetcatalog"
assets_car=""
app_icon=""
icon_source=""
if command -v xcrun >/dev/null 2>&1; then
  if [ -d "$root/assets/AppIcon.icon" ]; then
    icon_source="$root/assets/AppIcon.icon"
  elif [ -d "$root/assets/Assets.xcassets" ]; then
    icon_source="$root/assets/Assets.xcassets"
  fi
fi
if [ -n "$icon_source" ]; then
  rm -rf "$car_out"; mkdir -p "$car_out"
  if xcrun actool "$icon_source" --compile "$car_out" --platform macosx --minimum-deployment-target 12.0 --app-icon AppIcon --output-partial-info-plist "$car_out/partial.plist" >/dev/null 2>&1; then
    assets_car="$car_out/Assets.car"
    app_icon="$car_out/AppIcon.icns"
    echo "已生成图标资源 Assets.car"
  else
    echo "警告：actool 失败，回退到 Buoy.icns"
  fi
fi
if [ -z "$app_icon" ]; then
  app_icon="$root/assets/Buoy.icns"
fi

dotnet publish "$root/src/TokenFloatCross.Desktop/TokenFloatCross.Desktop.csproj" \
  -c Release -r osx-arm64 --self-contained true -o "$publish"

rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/PlugIns" "$app/Contents/Resources"
cp -R "$publish"/. "$app/Contents/MacOS/"
cat > "$app/Contents/Info.plist" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Buoy</string>
  <key>CFBundleDisplayName</key><string>Buoy</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundleIconName</key><string>AppIcon</string>
  <key>CFBundleExecutable</key><string>Buoy</string>
  <key>CFBundleIdentifier</key><string>app.buoy.desktop</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>1.1.0</string>
  <key>CFBundleVersion</key><string>20</string>
  <key>CFBundleDevelopmentRegion</key><string>en</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleSupportedPlatforms</key><array><string>MacOSX</string></array>
  <key>NSHighResolutionCapable</key><true/>
  <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>CFBundleURLTypes</key>
  <array>
    <dict>
      <key>CFBundleURLName</key><string>Buoy</string>
      <key>CFBundleURLSchemes</key><array><string>buoy</string></array>
    </dict>
  </array>
</dict>
</plist>
EOF
chmod +x "$app/Contents/MacOS/Buoy"
cp "$app_icon" "$app/Contents/Resources/AppIcon.icns"
if [ -n "$assets_car" ]; then cp "$assets_car" "$app/Contents/Resources/Assets.car"; fi

if command -v swiftc >/dev/null 2>&1; then
  widget_app="$app/Contents/PlugIns/BuoyWidget.appex"
  mkdir -p "$widget_app/Contents/MacOS"
  swiftc \
    -parse-as-library \
    -application-extension \
    -target arm64-apple-macosx14.0 \
    -module-name BuoyWidget \
    -O \
    -Xlinker -e -Xlinker _NSExtensionMain \
    -o "$widget_app/Contents/MacOS/BuoyWidget" \
    "$widget/BuoyWidget.swift" \
    -framework WidgetKit \
    -framework SwiftUI \
    -framework Foundation
  sed 's/$(PRODUCT_MODULE_NAME)/BuoyWidget/g' "$widget/Widget-Info.plist" > "$widget_app/Contents/Info.plist"
  mkdir -p "$widget_app/Contents/Resources"
  cp "$app_icon" "$widget_app/Contents/Resources/AppIcon.icns"
  if [ -n "$assets_car" ]; then cp "$assets_car" "$widget_app/Contents/Resources/Assets.car"; fi
  if [ -n "${BUOY_WIDGET_PROVISIONING_PROFILE:-}" ]; then
    cp "$BUOY_WIDGET_PROVISIONING_PROFILE" "$widget_app/Contents/embedded.provisionprofile"
  fi

  swiftc \
    -emit-library \
    -target arm64-apple-macosx14.0 \
    -module-name BuoyWidgetBridge \
    -O \
    -o "$app/Contents/MacOS/libBuoyWidgetBridge.dylib" \
    "$widget/BuoyWidgetBridge.swift" \
    -framework WidgetKit \
    -framework Foundation
  if [ -n "${BUOY_APP_PROVISIONING_PROFILE:-}" ]; then
    cp "$BUOY_APP_PROVISIONING_PROFILE" "$app/Contents/embedded.provisionprofile"
  fi
else
  echo "警告：未找到 swiftc，跳过 WidgetKit 扩展。"
fi

if command -v codesign >/dev/null 2>&1; then
  identity="${CODESIGN_IDENTITY:-}"
  if [ -z "$identity" ]; then
    identity="$(security find-identity -v -p codesigning 2>/dev/null | sed -n 's/.*"\(Apple Development:.*\)"/\1/p' | head -1)"
  fi
  if [ -z "$identity" ]; then
    echo "警告：没找到 Apple Development 证书，退回 ad-hoc 签名（WidgetKit 扩展不会工作）。"
    identity="-"
  fi
  echo "使用签名身份：$identity"
  if [ -d "$app/Contents/PlugIns/BuoyWidget.appex" ]; then
    codesign --force --deep --sign "$identity" "$app"
    codesign --force --sign "$identity" --entitlements "$widget/BuoyWidget.entitlements" "$app/Contents/PlugIns/BuoyWidget.appex"
    codesign --force --sign "$identity" --entitlements "$widget/App.entitlements" "$app"
  else
    codesign --force --deep --sign "$identity" --entitlements "$widget/App.entitlements" "$app"
  fi
fi
rm -f "$dmg"
if ! hdiutil create -volname Buoy -srcfolder "$app" -ov -format UDZO "$dmg"; then
  echo "警告：hdiutil 生成 DMG 失败，Buoy.app 仍然可用。"
fi
echo "已生成 $app"
