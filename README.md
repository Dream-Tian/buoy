# Buoy

用量浮窗。用 Avalonia 同时编译 Windows 和 macOS，统计和 NewAPI 请求放在不依赖系统的核心库里。

关闭主窗口后，macOS 留在菜单栏，Windows 留在托盘。NewAPI 用量和本机 Antigravity 配额都会读取。macOS 上的 Token 优先写入登录钥匙串。

原来的 Windows 版 TokenFloat 配置会在第一次启动时导入。Antigravity 会话里的逐条 Token 明细仍以那一版为准。

## macOS 桌面小组件

打包 macOS 应用时会编译 `macos-widget/` 下的 WidgetKit 扩展，显示今日 Token、请求数、RPM/TPM、TOP 1–3 模型及各自占比与消耗。安装后在桌面进入编辑模式，把 `Buoy 用量` 添加到桌面。它需要 macOS 14 或更高版本；系统会调度小组件刷新，更新间隔不保证固定。

扩展是沙盒进程，读不到主程序的文件，所以数据走本机回环接口：主程序启动后在 `127.0.0.1:47821/usage.json` 提供最新快照，扩展在时间线请求时拉取；主程序每次刷新还会通过 WidgetKit 让时间线尽快重载。接口只监听本机，不对外网开放。

打包脚本会自动使用钥匙串里的 `Apple Development` 证书签名。WidgetKit 扩展不接受 ad-hoc 签名（会被 AMFI 拒绝并崩溃），所以必须先在 Xcode 里登录 Apple ID 生成开发证书；脚本也支持显式指定身份：

```sh
CODESIGN_IDENTITY="Apple Development: 你的邮箱 (TEAMID)" scripts/package-mac.sh
```

分发给其他机器时需要 Developer ID 签名和公证；本机自用开发证书即可。

扩展链接时使用 `_NSExtensionMain` 作为入口（脚本里的 `-Xlinker -e -Xlinker _NSExtensionMain`）。用 Swift 默认的 `@main` 入口时，macOS 26/27 上 `ExtensionFoundation` 会在启动阶段触发 `MainActor.assumeIsolated` 断言而崩溃。

主窗口里的 `桌面小组件` 模式是可拖动的应用内浮窗，仍可用于 macOS 12 及以上；系统 WidgetKit 小组件只随 macOS `.app` 打包。

## 运行

需要 .NET 8 SDK。

```powershell
dotnet run --project src\TokenFloatCross.App\TokenFloatCross.App.csproj
```

## 发布

```powershell
dotnet publish src\TokenFloatCross.App\TokenFloatCross.App.csproj -c Release -r win-x64 --self-contained true
dotnet publish src\TokenFloatCross.App\TokenFloatCross.App.csproj -c Release -r osx-arm64 --self-contained true
dotnet publish src\TokenFloatCross.App\TokenFloatCross.App.csproj -c Release -r osx-x64 --self-contained true
```

Mac 上可以在仓库里执行 `scripts/package-mac.sh`，生成 `artifacts/Buoy.dmg`。脚本需要在 macOS 上运行。
