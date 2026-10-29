#!/bin/sh
# 把 artifacts/Buoy.app 安装到 /Applications。
# 关键点：替换 bundle 后小组件扩展经常从 pluginkit 注册表里掉出去（小组件库整个变空），
# 必须手动 pluginkit -a 注册回去，再重启 Dock 让小组件库重新枚举。
set -eu
root="$(CDPATH= cd -- "$(dirname "$0")/.." && pwd)"
app="$root/artifacts/Buoy.app"
target="/Applications/Buoy.app"

if [ ! -d "$app" ]; then
  echo "artifacts/Buoy.app 不存在，先运行 scripts/package-mac.sh"
  exit 1
fi

osascript -e 'quit app "Buoy"' >/dev/null 2>&1 || true
sleep 1
rm -rf "$target"
ditto "$app" "$target"
# 替换 bundle 后 LaunchServices 可能还挂着旧记录（我们见过 launch-disabled），
# 强制重新登记一次，再让 pluginkit 记录新的扩展路径。
"/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister" -f "$target" 2>/dev/null || true
if ! codesign --verify --strict "$target" >/dev/null 2>&1; then
  echo "警告：安装后的签名校验失败，小组件可能不可用。"
fi
pluginkit -a "$target/Contents/PlugIns/BuoyWidget.appex" 2>/dev/null || true
open "$target"
sleep 2
killall chronod 2>/dev/null || true
killall Dock 2>/dev/null || true
echo "已安装到 ${target}，小组件扩展已重新注册。"
