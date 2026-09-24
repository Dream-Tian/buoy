# Buoy

用量浮窗。用 Avalonia 同时编译 Windows 和 macOS，统计和 NewAPI 请求放在不依赖系统的核心库里。

关闭主窗口后，macOS 留在菜单栏，Windows 留在托盘。NewAPI 用量和本机 Antigravity 配额都会读取。macOS 上的 Token 优先写入登录钥匙串。

原来的 Windows 版 TokenFloat 配置会在第一次启动时导入。Antigravity 会话里的逐条 Token 明细仍以那一版为准。

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
