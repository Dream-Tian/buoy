# Buoy

用量浮窗。用 Avalonia 同时编译 Windows 和 macOS，统计和 NewAPI 请求放在不依赖系统的核心库里。

关闭主窗口后，macOS 留在菜单栏，Windows 留在托盘。macOS 版只做菜单栏常驻，不显示 Dock 图标，也不会出现在 Cmd+Tab 列表里。NewAPI 用量和本机 Antigravity 配额都会读取。macOS 上的 Token 优先写入登录钥匙串。

原来的 Windows 版 TokenFloat 配置会在第一次启动时导入。Antigravity 会话里的逐条 Token 明细仍以那一版为准。

## 功能

- **NewAPI 用量**：今日 / 本周 / 本月切换，总 Token、请求数、RPM、TPM 一起汇总。
- **消费趋势**：按小时画图，可切 Token / 次数 / 消耗三种口径，柱状或面积两种画法。
- **模型用量**：模型排行，含 Token、请求、消耗以及输入/输出占比。
- **模型筛选**：设置「筛选」页只统计指定模型，可单选可多选。列表来自主窗口当前时段用过的模型，不勾选表示统计全部；总览、趋势、模型页、菜单栏提示和桌面小组件一起生效。
- **Antigravity 配额**：读取本机 IDE 登录态里的套餐名、Prompt 额度和各模型剩余比例；IDE 没在运行或没登录时读不到。
- **桌面小组件**：macOS 上有两种 —— 应用内可拖动的桌面卡片（macOS 12+），以及随 `.app` 打包的 WidgetKit 系统小组件（macOS 14+）。
- **常驻菜单栏 / 托盘**：macOS 只占菜单栏，不占 Dock；关窗口不退出，点图标回到主窗口，菜单里有显示、设置、退出。菜单的「显示」把主窗口贴回桌面层，不抢当前应用的焦点、也不压住其他窗口；只有勾了「置于顶层」才会浮到最前。
- **外观**：浅色 / 深色、系统默认 / 圆体 / 宋体 / 楷体 / 等宽五套字体、窗口置顶。
- **其他**：开机自启、刷新间隔 10 / 30 / 60 / 300 秒、仅窗口可见时刷新、手动检查更新清单。

## 系统要求

| | 要求 |
| --- | --- |
| macOS | 12 或更高；系统小组件需要 14 或更高 |
| Windows | 10 / 11，x64 |
| 从源码构建 | .NET 8 SDK |

## 安装

从 [Releases](https://github.com/Dream-Tian/buoy/releases) 下载 `Buoy.dmg`，把 `Buoy` 拖进「应用程序」。

安装包目前用 `Apple Development` 证书签名，没有做公证。首次打开如果提示「无法验证开发者」，在「系统设置 → 隐私与安全性」里点「仍要打开」，或者执行：

```sh
xattr -dr com.apple.quarantine /Applications/Buoy.app
```

启动后点工具栏右侧的 ⚙ 打开设置，在「来源」页填 NewAPI 服务地址、访问令牌和用户 ID，保存后回主窗口刷新。

## 使用

- 主窗口顶部四个页签：总览、趋势、模型、配额；每页都能切今日 / 本周 / 本月。
- 标题栏右侧两个按钮：切成桌面卡片、关闭窗口（关闭后仍留在菜单栏）。
- 页签右边一行是刷新按钮和 ⚙ 设置。设置是独立窗口，分来源、窗口、筛选、数据、更新五页；「窗口」页管置顶、桌面小组件模式、开机自启、外观和字体；「筛选」页决定统计哪些模型，改完立刻生效，不用等下次刷新。
- 「桌面小组件」把主窗口缩成一张卡片贴到桌面层，不挡其他窗口，拖动后的位置会记住。
- 「系统小组件范围」决定 WidgetKit 小组件看的是今日、本周、本月，还是最近 24 小时 / 7 天 / 30 天。

## macOS 桌面小组件

打包 macOS 应用时会编译 `macos-widget/` 下的 WidgetKit 扩展，显示所选时间段的 Token、请求数、RPM/TPM、TOP 1–3 模型及各自占比与消耗。安装后在桌面进入编辑模式，把 `Buoy 用量` 添加到桌面。系统会调度小组件刷新，更新间隔不保证固定。

扩展是沙盒进程，默认读不到主程序的文件，所以数据主要走本机回环接口：主程序启动后在 `127.0.0.1:47821/usage.json` 提供最新快照，扩展在时间线请求时拉取；主程序每次刷新还会通过 WidgetKit 让时间线尽快重载。接口只监听本机，不对外网开放。主程序没开时，扩展读 `~/Library/Application Support/Buoy/widget-snapshot.json`（靠扩展里那条 `temporary-exception` 文件读取权限），显示最后一次的数据。

打包脚本会自动使用钥匙串里的 `Apple Development` 证书签名。WidgetKit 扩展不接受 ad-hoc 签名（会被 AMFI 拒绝并崩溃），所以必须先在 Xcode 里登录 Apple ID 生成开发证书；脚本也支持显式指定身份：

```sh
CODESIGN_IDENTITY="Apple Development: 你的邮箱 (TEAMID)" scripts/package-mac.sh
```

分发给其他机器时需要 Developer ID 签名和公证；本机自用开发证书即可。正式分发前建议去掉上面那条文件回退和对应的 `temporary-exception` 权限，只留回环接口 —— 沙盒加网络权限不需要额外授权，公证也省事。

扩展链接时使用 `_NSExtensionMain` 作为入口（脚本里的 `-Xlinker -e -Xlinker _NSExtensionMain`）。用 Swift 默认的 `@main` 入口时，macOS 26/27 上 `ExtensionFoundation` 会在启动阶段触发 `MainActor.assumeIsolated` 断言而崩溃。

主窗口里的 `桌面小组件` 模式是可拖动的应用内浮窗，仍可用于 macOS 12 及以上；系统 WidgetKit 小组件只随 macOS `.app` 打包。

## 数据与隐私

- 设置和快照放在 `~/Library/Application Support/Buoy/`（Windows 是 `%LOCALAPPDATA%\Buoy\`），`app-settings.json` 只存配置，不含用量明细。
- macOS 上 NewAPI 访问令牌写进登录钥匙串（service `Buoy`，账号是当前用户名），配置文件里只留 `keychain:v1` 占位；Windows 用 DPAPI 按当前用户加密；两者都不可用时退回本地明文编码。
- 除你配置的 NewAPI 地址外，只访问本机回环接口 `127.0.0.1:47821`（只给系统小组件供数）。
- 更新检查只拉清单里的版本号并和当前版本比对，不上传数据，也不会自动安装。

## 从源码运行

需要 .NET 8 SDK。

```sh
dotnet run --project src/TokenFloatCross.Desktop/TokenFloatCross.Desktop.csproj
```

`dotnet run` 起的是普通窗口：菜单栏常驻（不占 Dock）、桌面卡片模式都可用，但不会有 WidgetKit 扩展和对应的代码签名 —— 系统小组件只随 macOS `.app` 出现。

Dock 隐藏由 `Program.cs` 里的 `MacOSPlatformOptions.ShowInDock = false` 完成，所以 `.app` 和 `dotnet run` 表现一致。**不要**在 `Info.plist` 里加 `LSUIElement`：声明成 UIElement 后 ExtensionKit 会把小组件扩展当成 “restricted or unknown” 忽略掉，小组件库和桌面小部件会整块消失。代价是打包版在启动瞬间可能闪过一下 Dock 图标，窗口起来后不占 Dock、也不进 Cmd+Tab。

## 打包

Windows：

```powershell
dotnet publish src\TokenFloatCross.Desktop\TokenFloatCross.Desktop.csproj -c Release -r win-x64 --self-contained true
```

macOS（在 macOS 上执行）：

```sh
scripts/package-mac.sh
```

脚本会发布 `osx-arm64` 自包含版本、用 actool 生成图标、编译并嵌入 WidgetKit 扩展、签名，最后产出 `artifacts/Buoy.app`、`artifacts/Buoy.dmg`。需要 `xcrun`、`swiftc`、`codesign`，以及钥匙串里至少一张 `Apple Development` 证书；缺 `swiftc` 时跳过扩展，缺证书时退回 ad-hoc 签名（小组件不可用）。

## 发版

1. 先确认版本号一致：`scripts/package-mac.sh` 里 `CFBundleShortVersionString` 是应用版本，建议同时在 `TokenFloatCross.Desktop.csproj` 里补 `<Version>`，否则应用内「检查更新」比对的是默认的 1.0.0.0。
2. 打标签并推送：`git tag v1.1.0 && git push origin v1.1.0`。
3. 在 GitHub 的 Releases 里基于该标签新建发布，附上 `artifacts/Buoy.dmg`，说明本次改动用 `git log <上个标签>..HEAD --oneline` 整理。

签名和公证没有搞定之前，发布说明里最好注明「开发证书签名，首次打开需手动放行」，避免安装的人以为是包坏了。