# TuringDesk（图灵桌面）

TuringDesk 是一个面向 Windows 的 **AI Native Desktop**。

它不替换 Windows 内核。v0.12 开始，TuringDesk 的默认产品路线也不再是重写 Explorer，而是像成熟的桌面增强软件一样：**保留 Windows 原生 Shell，在其上增加可编程桌面视觉、AI Agent、语音、Harness 与自动化能力。**

## v0.12 核心变化

默认启动模式已经切换为 **Desktop Enhancement Mode**：

```text
Windows / DWM
  |
  +-- Explorer
  |    +-- Desktop Icons
  |    +-- Start / Taskbar
  |    +-- System Tray
  |    +-- Context Menu / Jump List / Shell compatibility
  |
  +-- TuringDesk
       +-- Desktop Scene（挂在 Explorer 图标后方）
       +-- Desktop DIY / Theme
       +-- AI Floating Cards
       +-- Always-on Voice
       +-- DeepSeek Harness
       +-- Harness WebView2 Console
       +-- Windows MCP / Capability API
```

**Explorer 继续负责 Windows 已经做成熟的部分；TuringDesk 重点负责 Windows 没有的 AI Native Desktop 能力。**

Replacement Shell 没有删除，而是降级为高级模式，供专用设备、实验环境和深度 AI Shell 场景使用。

## Desktop Enhancement Mode

普通运行：

```text
TuringDesk.Desktop.exe
```

TuringDesk 会寻找 Explorer 的 `Progman / WorkerW` 桌面背景层，并把自己的 Desktop Scene HWND 挂到 Explorer 桌面图标之后。

因此正常情况下：

- Windows 桌面图标仍然是 Explorer 原生图标。
- Windows 任务栏、开始菜单、系统托盘保持原生。
- Explorer 右键菜单、Jump List、应用兼容性不需要 TuringDesk 重写。
- TuringDesk 场景窗口不抢焦点、不吃鼠标、不出现在任务栏/Alt-Tab。
- 如果 WorkerW 挂载失败，TuringDesk 会放弃视觉层，而不是覆盖 Explorer。
- Harness、语音、Agent 与快捷对话仍可以继续工作。

第一版 Scene Renderer 先使用现有壁纸 + 轻量 Ambient Layer。宿主边界已经独立，后续可以直接替换为：

```text
Explorer Desktop Host
        |
        v
TuringDesk Scene Host
  +-- WPF Ambient            <- v0.12 bootstrap
  +-- Direct3D/DirectComposition
  +-- GPU Video
  +-- Web / WebView
  +-- AI-reactive Scene
```

长期高性能动态场景目标是 **Direct3D / DirectComposition**，而不是把所有动画都堆在 WPF 中。

详细设计见 `docs/ENHANCEMENT-MODE.md`。

## 全屏性能策略

TuringDesk 已加入前台全屏应用检测。

当游戏或其他应用真正覆盖整个显示器时，TuringDesk 会暂停自己的动态 Ambient Scene，让出视觉渲染资源；以下服务不随视觉暂停：

- DeepSeek Harness
- 常驻语音
- Windows MCP / Capability API
- Agent Runtime

后续 Direct3D / 视频场景统一扩展为：

```text
keep   -> 持续渲染
pause  -> 停止动画/render tick，保留资源
stop   -> 释放重型 GPU / 视频资源，离开全屏后恢复
```

## DeepSeek Harness

DeepSeek Harness 是 TuringDesk 的 Agent 基础运行层，不是一个需要用户手动打开的网页功能。

- TuringDesk 启动后自动确保 Harness 服务运行。
- 安装包包含已验证的 DeepSeek Harness runtime family `0.1.0-rc.6`。
- 完整 Harness UI 使用官方 WebUI，通过 WebView2 包装成桌面窗口。
- Harness WebView 只是完整控制台，关闭它不会停止 Harness。
- 不打开 WebView 时，TuringDesk 自己的快捷对话、语音、对话卡、执行轨迹卡仍然工作。

执行路径保持：

```text
User
  -> TuringDesk Quick Chat / Voice
  -> Runtime / DeepSeek Harness
  -> TuringDesk Windows MCP
  -> Capability API
  -> Win32 / Windows Apps
```

## 两种桌面模式

### 默认：Enhancement Mode

```text
TuringDesk.Desktop.exe
```

保留 Explorer。适合绝大多数用户，也是主产品路线。

### 高级：Replacement Shell Mode

```text
TuringDesk.Desktop.exe --shell
```

TuringDesk 自己提供 Desktop Surface、Taskbar/AppBar、Start 与 ShellHost。

安装不会静默启用 Replacement Shell。需要时仍通过：

```text
启用 TuringDesk 桌面
```

显式切换当前 Windows 用户；恢复入口为：

```text
恢复 Windows Explorer 桌面
```

## Desktop DIY / AI UX

TuringDesk 保留自己的桌面体验，而不是把所有能力塞进 Harness WebUI。

当前包括：

- 系统壁纸 / 自定义壁纸 / 填充方式
- Accent / Taskbar appearance
- AI 对话浮卡
- Agent 执行轨迹浮卡
- 浮卡透明度、左右位置、自动收起
- 快捷模型配置
- Harness 官方控制台入口
- 常驻 Windows 语音入口

Windows 已经提供成熟图标的功能继续遵循：

```text
真实应用 / 文件 Shell Icon
        ↓
Windows Stock Icon
        ↓
TuringDesk Vector Fallback
```

Agent 等 TuringDesk 专属能力才使用产品自有图标。

## Windows 安装

正式分发使用标准 MSI，安装到 Program Files，并提供：

- Windows 标准安装 / Upgrade / Repair / Uninstall
- 开始菜单快捷方式
- 桌面 `TuringDesk` 快捷方式
- TuringDesk 多尺寸 Application Icon
- Replacement Shell 显式启用入口
- Explorer 恢复入口

目前正式目标平台为 **Windows 11 ARM64**，x64 安装包仍待补齐。

## 本地构建

Runtime / Harness：

```powershell
cd runtime
corepack enable
pnpm install --no-frozen-lockfile
pnpm typecheck
pnpm build
pnpm test:mcp
pnpm test:harness
```

Windows Desktop：

```powershell
cd ..
dotnet build src/TuringDesk.Desktop/TuringDesk.Desktop.csproj -c Release
dotnet build src/TuringDesk.ShellHost/TuringDesk.ShellHost.csproj -c Release
```

打正式 ARM64 MSI 时可以显式指定 v0.12：

```powershell
./scripts/package-installer.ps1 -Version v0.12.0 -RuntimeIdentifier win-arm64
```

## 安全原则

- Enhancement Mode 不修改 Windows Shell 注册表配置。
- Replacement Shell 仅修改当前用户 HKCU，不覆盖机器级 Winlogon Shell。
- 不删除 Explorer。
- Harness 只通过 TuringDesk 审核后的 MCP / Capability Surface 操作 Windows。
- 不默认向 Agent 暴露无限制 PowerShell / Bash / 管理员权限。
- 视觉场景故障不应影响 Explorer、Harness 或普通 Windows 应用使用。

## 当前下一步

v0.12 后续重点：

- Direct3D / DirectComposition Scene Renderer
- Video Scene + GPU decode
- Web Scene
- AI-reactive Scene / Shader parameters
- 多显示器独立场景
- 全屏 `keep / pause / stop` 用户配置
- 开机后台启动与托盘控制器
- 快速开发部署，不必每次重打 MSI
- Windows x64 正式安装包
- 代码签名 / SmartScreen 体验

## 文档

- `docs/ENHANCEMENT-MODE.md` — Wallpaper Engine 风格的默认桌面增强架构
- `docs/ARCHITECTURE.md` — 总体进程与能力边界
- `docs/HARNESS-MCP.md` — Harness、WebUI 与 Windows MCP
- `docs/SHELL-REPLACEMENT.md` — 高级 Replacement Shell 与恢复机制
- `docs/SHELL-SURFACE.md` — Replacement Shell 的 Desktop / Start / Taskbar
- `docs/DESKTOP-UX.md` — AI Native Desktop UX 原则
- `docs/ROADMAP.md` — 后续路线

## License

MIT
