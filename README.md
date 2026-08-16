# TuringDesk（图灵桌面）

TuringDesk 是一个面向 Windows 的 **AI Native Desktop** 项目。

它不是要替换 Windows 内核，而是在 Windows 现有应用、驱动和窗口体系之上，提供更深度的桌面 Shell、常驻 AI Agent、语音入口、桌面能力调用和可观察的 Agent 执行过程。

## v0.11 当前状态

当前正式构建目标：**Windows 11 ARM64**。

CI 已能够生成标准 Windows Installer：

```text
TuringDesk-v0.11-win-arm64.msi
```

v0.11 的核心方向：

- 标准 Windows MSI 安装，应用文件由 Windows Installer 管理并安装到 Program Files。
- TuringDesk Desktop 与 ShellHost 均嵌入真正的多尺寸 Windows Application Icon。
- Windows 文件、快捷方式、系统功能优先使用原生 Shell / Stock Icon；只有系统没有可用图标时才使用 TuringDesk 自绘矢量图标。
- DeepSeek Harness 作为桌面的常驻 Agent 基础服务：**TuringDesk 启动时自动后台启动 Harness**。
- Harness 官方 WebUI 原样保留，TuringDesk 使用 WebView2 提供无浏览器地址栏/标签栏的桌面窗口外壳。
- WebView 只是完整 Harness 控制台入口，**不是快捷对话的前置条件**。
- 即使用户没有打开 Harness WebView，快捷文本/语音对话仍可工作，并继续显示 TuringDesk 原生的 **对话卡** 与 **执行轨迹卡**。
- TuringDesk 的桌面深度美化、Desktop DIY、任务栏、Start、桌面文件表面等继续由 TuringDesk 自己负责，不被 Harness WebUI 替代。
- 将 TuringDesk 设置为当前用户 Windows Shell 是安装后的显式操作；卸载前会优先恢复 Explorer。

## 产品结构

```text
Windows
  │
  ├─ 原生应用 / Win32 窗口 / Shell 资源
  │
  ▼
TuringDesk Desktop / Shell
  ├─ Desktop Surface
  ├─ Start Menu
  ├─ Taskbar / AppBar
  ├─ Desktop DIY / 深度美化
  ├─ 快捷对话入口
  ├─ 对话卡
  ├─ 执行轨迹卡
  ├─ 常驻语音
  └─ Harness WebView2 Console（按需打开）
  │
  ▼
TuringDesk Runtime / Capability API
  │
  ▼
DeepSeek Harness（随 TuringDesk 自动启动）
  │
  ▼
TuringDesk Windows MCP
  │
  ▼
Win32 / Windows Apps
```

## DeepSeek Harness

TuringDesk 不再重复开发一套 Harness 专用聊天前端。

安装包包含已验证的 DeepSeek Harness runtime family `0.1.0-rc.6`。TuringDesk 启动后会自动确保 Harness 服务处于可用状态；用户需要完整控制台时，再打开官方 Harness WebUI 的 WebView2 外壳。

职责划分：

**DeepSeek Harness**

- Agent runtime / reasoning-execution kernel
- 官方会话与 WebUI
- 模型和 Harness 自身运行逻辑

**TuringDesk**

- Harness 生命周期托管
- Windows MCP / Capability 边界
- 快捷对话入口
- 对话卡与执行轨迹卡
- 语音入口
- WebView2 桌面外壳
- Windows Shell、桌面、任务栏和美化体验

因此：**关闭 Harness WebView 不等于停止 Harness**。

## 原生 Icon 优先原则

TuringDesk 的图标规则是：

```text
真实应用 / 文件 Shell Icon
        ↓ 取不到
Windows Stock Icon
        ↓ 仍取不到
TuringDesk Vector Fallback
```

TuringDesk 自己的品牌 EXE / MSI 使用独立的 TuringDesk Application Icon。

这意味着文件夹、设置、网络、桌面、搜索、用户、锁定、删除等 Windows 已提供视觉资源的功能，应尽量保持 Windows 原生视觉；Agent 等 TuringDesk 专属能力才使用自己的图标体系。

## Windows 安装

正式分发使用：

```text
TuringDesk-v0.11-win-arm64.msi
```

MSI 负责：

- Program Files 安装目录
- 开始菜单入口
- Windows“已安装的应用 / 程序和功能”生命周期
- Repair / Upgrade / Uninstall
- TuringDesk 应用图标
- 卸载前 Explorer 恢复流程

安装本身**不会静默替换 Windows Shell**。

需要真正进入 TuringDesk replacement-shell 模式时，在安装后的开始菜单中显式选择：

```text
启用 TuringDesk 桌面
```

需要恢复 Windows Explorer 时选择：

```text
恢复 Windows Explorer 桌面
```

Shell 设置只针对当前 Windows 用户，不改机器级 Winlogon Shell。

## Agent 动态浮卡

TuringDesk 保留自己的 AI 桌面交互，而不是把所有操作都塞进 Harness WebUI。

快捷对话触发 Agent 后可显示两类原生浮卡：

### 对话卡

- 当前用户请求
- Agent 状态
- 最终回复 / 错误
- Run ID / 运行状态

### 执行轨迹卡

- Runtime / Harness / MCP 执行事件
- 工具调用和产品级执行进度
- 不展示模型私有 chain-of-thought

用户可以在 Desktop DIY Center 中控制浮卡启用状态、透明度、位置和自动收起时间。

## 安全边界

当前版本坚持：

- replacement shell 只修改当前用户 HKCU 配置
- 不覆盖机器级 Winlogon Shell
- 不删除 Explorer
- ShellHost 保留失败恢复路径
- Harness 只通过 TuringDesk 审核后的 MCP / Capability 接口操作 Windows
- 不默认给 Agent 无限制 PowerShell / Bash / 管理员能力
- 用户显式的桌面文件操作与 Agent 能力边界分离

紧急恢复时可以打开任务管理器：

```text
Ctrl + Shift + Esc
```

运行 PowerShell，然后执行：

```powershell
& "$env:LOCALAPPDATA\TuringDesk\Restore-Explorer.ps1"
```

## 本地构建

### Runtime / Harness

```powershell
cd runtime
corepack enable
pnpm install --no-frozen-lockfile
pnpm typecheck
pnpm build
pnpm test:mcp
pnpm test:harness
```

### Windows Desktop

```powershell
cd ..
dotnet build src/TuringDesk.Desktop/TuringDesk.Desktop.csproj -c Release
dotnet build src/TuringDesk.ShellHost/TuringDesk.ShellHost.csproj -c Release
```

### 构建 ARM64 MSI

```powershell
./scripts/package-installer.ps1 -Version v0.11 -RuntimeIdentifier win-arm64
```

输出：

```text
artifacts/TuringDesk-v0.11-win-arm64.msi
```

CI 还会验证：

- Runtime typecheck / build
- Windows MCP smoke test
- DeepSeek Harness 启动
- 官方 Harness WebUI 从最终安装布局启动
- Desktop / ShellHost Release 编译
- EXE Application Icon 可提取
- ARM64 MSI 成功生成

## 当前限制

v0.11 仍属于开发阶段，重点已从“能不能替换桌面”转向“能否稳定作为 AI 桌面使用”。仍需继续验证和完善：

- 第三方系统托盘 / notification area 完整兼容
- Jump Lists
- 桌面图标自由位置持久化
- 更完整的多显示器行为
- 第三方 Explorer Context Menu Extension 托管
- 全屏游戏 / 独占模式软件兼容性
- 安装包代码签名与 SmartScreen 体验
- x64 正式安装包

## 文档

- `docs/ARCHITECTURE.md` — 总体进程与能力边界
- `docs/HARNESS-MCP.md` — Harness、WebUI 与 Windows MCP 集成
- `docs/SHELL-REPLACEMENT.md` — Windows replacement shell 与恢复机制
- `docs/SHELL-SURFACE.md` — 桌面 / Start / Taskbar 表面
- `docs/DESKTOP-UX.md` — AI Native Desktop UX 原则
- `docs/ROADMAP.md` — 后续开发路线

## License

MIT
