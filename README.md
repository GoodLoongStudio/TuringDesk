# TuringDesk

TuringDesk（图灵桌面）是一个面向 Windows 的 AI Native 桌面项目：保留 Windows 原生应用生态与窗口管理，同时让常驻语音、AI Agent、桌面能力和 DeepSeek Harness 成为桌面的一等入口。

## 当前方向

- Windows 原生桌面壳与系统能力优先，不重复重做已有的 Windows 功能。
- 系统功能和文件/应用图标优先使用 Windows 原生 Shell Icon；只有系统没有对应图标时才使用 TuringDesk 自绘图标。
- TuringDesk Desktop 与 ShellHost 都嵌入真正的多尺寸 Windows Application Icon。
- DeepSeek Harness 直接使用官方 WebUI；TuringDesk 只通过 WebView2 提供无浏览器地址栏/标签栏的原生窗口外壳，并注入 TuringDesk Windows MCP 能力。
- 正式分发使用标准 Windows MSI 安装器，安装到 Program Files，提供开始菜单、修复/升级/卸载生命周期。
- 将 TuringDesk 设置为当前用户 Shell 是安装后的显式操作；卸载时会先恢复 Windows Explorer。

## DeepSeek Harness

TuringDesk 不维护独立的 Harness 对话前端。安装包包含官方 `@deepseek-ai/dsh`，桌面入口启动官方 `dsh --profile web`，监听本机回环地址，并用 WPF + WebView2 包装显示。

TuringDesk 负责启动/托管 Harness、本机 WebView2 外壳、Windows MCP 注入，以及安装布局和运行时生命周期；Harness 的会话、设置和 WebUI 本身沿用上游实现。

## Windows 安装

Windows ARM64 CI 生成：

```text
TuringDesk-v0.11-win-arm64.msi
```

MSI 负责应用文件、开始菜单入口、升级、修复和卸载。安装后可以显式选择“启用 TuringDesk 桌面”，也提供恢复 Windows Explorer 的入口。

## 构建

```powershell
cd runtime
corepack enable
pnpm install --no-frozen-lockfile
pnpm build
pnpm test:mcp
pnpm test:harness

cd ..
dotnet build src/TuringDesk.Desktop/TuringDesk.Desktop.csproj -c Release
dotnet build src/TuringDesk.ShellHost/TuringDesk.ShellHost.csproj -c Release
./scripts/package-installer.ps1 -Version v0.11 -RuntimeIdentifier win-arm64
```

## License

MIT
