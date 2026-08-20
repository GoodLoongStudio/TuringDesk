# TuringDesk 旧代码与仓库冗余清理计划

> 状态：待执行  
> 日期：2026-08-20  
> 设计依据：[TURINGDESK-DESIGN-SPEC.md](./TURINGDESK-DESIGN-SPEC.md)  
> AI 专项计划：[AI-WORKBENCH-CONSOLIDATION-PLAN.md](./AI-WORKBENCH-CONSOLIDATION-PLAN.md)  
> 本文只制定清理顺序，不包含业务代码修改

## 1. 目标

在不破坏当前三个主界面、Explorer 桌面和恢复能力的前提下，清除新旧路线并存造成的代码、依赖、脚本、CI、打包和文档冗余。

清理后的仓库必须具备：

- 一个产品设计基线；
- 三个默认主界面；
- 一个官方 Harness 深度工作台；
- 一个桌面设置中心；
- 一个清晰的默认增强模式生命周期；
- 与默认产品隔离的 Replacement Shell；
- 没有为已删除架构保留的依赖、端口、脚本和 CI 断言。

## 2. 清理原则

### 2.1 先迁移，后删除

只要旧组件仍承担真实职责，就不能因为名称陈旧直接删除。必须先迁移调用者，再删除旧实现。

### 2.2 按能力族拆分提交

每个清理变更只处理一个能力族，例如工作台生命周期、模型测试、Windows MCP、原生 Agent UI、旧设置中心、Shell 或打包。禁止一次性删除所有旧文件后再集中修复编译错误。

### 2.3 没有普通入口不等于死代码

WPF XAML、反射、启动参数、脚本和安装器可能不产生直接 C# 引用。删除前必须同时检查：

- C# 与 XAML；
- `App.xaml` 和启动参数；
- PowerShell/CMD；
- CI workflow；
- WiX/打包脚本；
- 安装升级与恢复逻辑。

### 2.4 共享基础服务不随旧入口删除

下列组件仍被真实原生功能使用，不能批量删除：

- `AppLauncher.cs`；
- `WindowManager.cs`；
- `PackagedAppLauncher.cs`；
- `WallpaperService.cs`；
- 场景库、场景渲染与 Explorer desktop host；
- Shell 恢复脚本和 ShellHost fail-safe。

### 2.5 清理不能改变设计

不得借清理增加第四个主界面、恢复 AI Orb、重建 Harness UI 或扩大 Windows 自动化权限。

## 3. 已立即完成的文档清理

本轮删除下列已经互相冲突的旧设计、路线和问题文档：

- `docs/ARCHITECTURE.md`
- `docs/DESKTOP-UX.md`
- `docs/ENHANCEMENT-MODE.md`
- `docs/HARNESS-MCP.md`
- `docs/PENDING-ISSUES.md`
- `docs/PRODUCT-TARGET.md`
- `docs/ROADMAP.md`
- `docs/SHELL-REPLACEMENT.md`
- `docs/SHELL-SURFACE.md`
- `docs/V0.14-SEARCH-DESKTOP.md`

后续 `docs` 只承担三类职责：

1. 设计规范；
2. 经批准的专项设计/ADR；
3. 有退出条件的执行计划或操作 Runbook。

## 4. 冗余总览

| 能力族 | 当前问题 | 处理方式 | 风险 |
|---|---|---|---|
| 旧 MainWindow 仪表盘 | 默认隐藏但仍拥有大量状态和事件 | 提取控制器后删除旧 UI | 高 |
| 两个设置中心 | `DesktopLibraryWindow` 与 `DesktopDiyCenterWindow` 并存 | 统一到 Desktop Library | 中 |
| 双 Agent Runtime | 官方 WebUI 与 4317 SDK Runtime 并存 | 删除旧 Runtime | 高 |
| 原生 Agent 卡片 | Conversation/Trace/Activity 与官方工作台重复 | 迁移入口后删除 | 中 |
| Windows MCP/Capability | 旧工具注入 WebUI 并常驻 4318 | 移除完整暴露链 | 高 |
| Replacement Shell | 与默认产品混编并保留旧 Agent 输入 | 隔离、冻结或独立验收 | 高 |
| 开发脚本 | 仍把 4317 当成功条件 | 改为三个界面目标契约 | 中 |
| 打包/CI | 仍要求旧 profile、smoke 和依赖 | 删除旧 payload 与断言 | 高 |
| 模型设置 | 参数和连接测试依赖 RuntimeClient | 迁移到独立探测 | 高 |
| 场景类型/宿主 | 增强与 shell 模式存在并行抽象 | 审核后收口共享边界 | 中 |

## 5. 必须保留的目标组件

### 5.1 三个主界面

- `DesktopSearchBarWindow.*`
- `DesktopLibraryWindow.*`
- `HarnessConsoleWindow.*`

### 5.2 搜索与轻量对话

- 应用搜索和 Everything/file search 服务；
- `DesktopSearchIndexService.cs`；
- `DesktopQuickAnswerService.cs`；
- `DesktopAiModelChoiceService.cs`。

### 5.3 桌面与编辑

- `EnhancementWallpaperWindow.*`；
- `ExplorerDesktopHost.cs`；
- `SceneRendererControl.*`；
- Scene catalog/library/manifest/instance settings；
- `SceneEditorWindow.*`；
- `ScenePropertiesWindow.*`；
- `MonitorProfilesControl.*`；
- 播放列表、应用规则和性能策略。

### 5.4 官方工作台

- `HarnessWebUiService.cs`，解除旧 MCP 后保留；
- `HarnessModelBridgeService.cs`；
- `runtime/src/harness-web-smoke.ts`；
- 官方 `@deepseek-ai/dsh` 和必需的 Node。

## 6. 阶段 A：建立可验证基线

工作：

1. 测试包显示版本、commit、架构和构建时间。
2. 为三个主界面建立启动与截图基线。
3. 记录默认模式的进程、监听端口、内存和首次交互时间。
4. 为 L1/L2/L3/L4、设置和应用场景建立最小回归清单。
5. 确认重复“多屏配置”来自旧包还是目标机缓存。
6. 决定 Replacement Shell 状态：正式、实验或冻结。

退出条件：后续删除造成的用户可见变化都能追溯到明确提交。

## 7. 阶段 B：移除旧 MainWindow 产品界面

### 7.1 当前问题

`MainWindow` 在默认增强模式中被移到屏幕外并隐藏，但仍承担应用生命周期、语音、模型状态、搜索/设置导航、Capability Server、RuntimeClient、旧页面和旧 Agent 活动列表，是隐形 service locator。

### 7.2 迁移顺序

1. 提取默认模式应用控制器，负责搜索栏、设置和工作台导航。
2. 提取增强桌面生命周期，避免依赖 MainWindow 可视控件。
3. 将语音接入搜索栏路由，不再写入旧 CommandBox。
4. 把模型状态交给统一模型服务和搜索栏。
5. 将诊断从旧 ActivityList 移到稳定日志/高级诊断页。
6. 删除旧 Home/Apps/Workspaces/Tasks/Memory/Agent 页面和事件。
7. 若 MainWindow 只剩宿主，替换为无 UI 控制器或最小宿主。

退出条件：默认模式不再创建屏幕外完整仪表盘，三个主界面仍可使用。

## 8. 阶段 C：统一设置中心

目标是只保留 `DesktopLibraryWindow`。

迁移步骤：

1. 对比 `DesktopDiyCenterWindow` 与 Desktop Library 的设置项。
2. 将仍有价值的外观/主题选项迁入统一设置。
3. 所有入口使用同一个窗口管理器，避免重复实例。
4. AI 设置移除 `RuntimeClient` 构造依赖。
5. 首次设置复用相同的模型和场景保存路径。

迁移后删除：

- `DesktopDiyCenterWindow.xaml`
- `DesktopDiyCenterWindow.xaml.cs`
- `MainWindow.ShowDiyCenter`
- 只服务旧 DIY Center 的样式、字段和事件

不能删除 `WallpaperService`，它仍被场景渲染和 shell 桌面使用。

退出条件：任何入口都不会打开第二套设置中心。

## 9. 阶段 D：统一 AI 工作台

严格按 AI 专项计划执行：

1. 工作台解除 `RuntimeHostService` lease；
2. 模型连接测试迁出 `RuntimeClient`；
3. 删除 Windows MCP patch 和 4318 Capability Server；
4. 删除 4317 Runtime 和 SDK JSON-RPC Harness；
5. 删除原生 Agent 卡片；
6. 收缩 Node 依赖和打包 payload。

WPF 删除候选：

- `RuntimeClient.cs`
- `RuntimeHostService.cs`
- `CapabilityServer.cs`
- `AgentFloatingCardsService.cs`
- `AgentConversationCardWindow.*`
- `AgentTraceCardWindow.*`
- `AgentActivityWindow.*`
- `AgentStatusBadge.*`

Runtime 删除候选：

- `server.ts`
- `agent-activity.ts`
- `mock-agent.ts`
- `model-gateway.ts`
- `model-config.ts`（确认无保留调用后）
- `harness-gateway.ts`
- `harness-runtime.ts`
- `harness-integration-smoke.ts`
- `capability-client.ts`
- `windows-mcp-server.ts`
- `windows-mcp-smoke.ts`
- `harness/turingdesk.cordis.yml`

退出条件：默认运行只使用 4319 官方 WebUI，仓库中无 4317/4318 入口。

## 10. 阶段 E：隔离 Replacement Shell

当前 ShellBar 自带 RuntimeClient 和旧 Agent 输入，AgentStatusBadge 打开旧 ActivityWindow，ShellHost 仍检查 packaged `server.js`。

如果继续支持：

1. 保留 ShellHost fail-safe、Explorer 恢复和当前用户策略。
2. ShellBar 的 AI 入口改为同一个官方工作台或搜索入口。
3. 删除 shell 内部 RuntimeClient、Agent cards 和 Runtime health。
4. ShellHost 不再要求 `runtime/app/server.js`。
5. 单独建立 UI、登录、崩溃恢复和卸载测试。
6. 新建纯操作型 Shell Runbook，只写启用、验证与恢复。

如果冻结或停止支持：

1. 先从安装器和开始菜单移除启用入口。
2. 保留一个版本周期的恢复脚本与升级清理。
3. 确认已启用账户升级后恢复 Explorer。
4. 再删除 Shell UI 和 ShellHost。

退出条件：Shell 不再决定默认产品依赖，支持状态和恢复路径明确。

## 11. 阶段 F：脚本与开发入口

明确过期候选：

- `scripts/Start-TuringDesk.ps1` 和 `.cmd`，直接依赖 `server.js`；
- `scripts/verify-lazy-runtime.ps1`，把 4317 当目标契约；
- `scripts/quick-verify.ps1` 中 4317 占用、就绪和提示逻辑；
- 任何要求先启动旧 Runtime 的开发命令。

`QUICK-VERIFY.cmd` 可以保留，但应改为验证：

1. 桌面增强模式和场景宿主；
2. 搜索栏 L1/L2；
3. L3 不启动 Node/Harness；
4. 显式打开 L4 后只出现 4319；
5. 设置页和场景应用；
6. 关闭工作台后的进程回收；
7. 当前 commit/build 信息。

退出条件：开发脚本不再引用 4317、4318、旧 profile 或旧 Agent cards。

## 12. 阶段 G：CI、依赖与打包

### 12.1 依赖

保留 `@deepseek-ai/dsh`。删除只服务 SDK JSON-RPC profile 的 agent spine、SDK server、session、checkpoint、compaction、token meter 和 MCP client。若官方 Web profile 间接需要依赖，应由 lockfile 和 final-layout 测试证明。

### 12.2 CI

删除或替换：

- RuntimeClient lazy lease 断言；
- RuntimeHostService lifecycle 断言；
- Windows MCP routing metadata 断言；
- SDK Harness integration smoke；
- 旧 Cordis profile 存在性检查。

新增：

- L3 不引用 Runtime/Harness；
- Workbench 不引用 RuntimeHost/MCP patch；
- final-layout 官方 WebUI 交互态检查；
- 三个主界面入口检查；
- 已删除文件和端口无回归检查。

### 12.3 打包

移除 `server.js`、SDK integration smoke、旧 profile、Windows MCP server、旧必需文件清单和 BUILD-INFO 旧描述。

保留官方 `dsh`、Node、Harness Web smoke、WebView2 依赖，以及 Shell 产品决策完成前的恢复文件。

退出条件：新包只携带目标架构文件，覆盖升级能删除旧 payload。

## 13. 阶段 H：场景与共享模型审计

本阶段先审核，不默认删除。重点检查：

- `SceneEngine/SceneModels.cs` 与 `Services/SceneEngine/SceneManifest.cs`；
- `DesktopSurfaceWindow` 与 `EnhancementWallpaperWindow` 的共享场景逻辑；
- `ShellSettingsStore`、`DesktopPlaybackSettingsStore`、`SceneInstanceSettingsStore`；
- `SceneLibraryStore` 与 `SceneCatalogService`；
- 设置页和首次设置中的场景应用代码；
- 多屏配置与显示器热插拔的持久化模型。

处理顺序：确定数据所有权和格式、选择权威模型、编写迁移测试、迁移调用者、最后删除重复类型。不能仅因文件名相似就合并；不同桌面模式可以有不同窗口宿主，但应共享场景领域模型。

退出条件：同一业务概念只有一个权威数据模型和持久化入口。

## 14. 阶段 I：命名与目录收口

功能迁移完成后再执行：

- 清除容易与旧 4317 服务混淆的 `Runtime` 命名；
- 服务按 Search、Desktop、Scene、Harness、Settings、Shell 分组；
- 视图不再通过隐藏 MainWindow 获取服务；
- Desktop DIY/Library/Settings 统一为 Desktop Settings；
- 端口、路径、版本和进程名集中定义；
- 删除未使用资源、样式、事件与旧注释。

## 15. 每阶段验证要求

每个清理 PR 至少完成：

1. 仓库级引用检查；
2. WPF Desktop 编译；
3. Runtime/官方 WebUI 类型检查；
4. 对应单元或 smoke test；
5. `git diff --check`；
6. Windows 目标机最小验证；
7. 涉及打包时执行 final-layout 验证；
8. 涉及 Shell 时验证 Explorer 恢复。

删除 XAML 时还要检查 `App.xaml`、`x:Class`、`InitializeComponent`、窗口 profile、快捷方式和按名称创建窗口的代码。

## 16. 建议提交顺序

| 顺序 | 主题 | 前置条件 |
|---|---|---|
| 1 | build/commit 与三个 UI 基线 | 无 |
| 2 | 工作台解除 RuntimeHost | 1 |
| 3 | 模型测试迁出 RuntimeClient | 2 |
| 4 | 删除 Windows MCP/Capability | 2、3 |
| 5 | 删除 4317 Runtime/SDK Harness | 3、4 |
| 6 | 删除 Agent cards 和旧 Agent 入口 | 5 |
| 7 | 统一 Settings，删除 DIY Center | 3 |
| 8 | 提取控制器，删除旧 MainWindow UI | 6、7 |
| 9 | 处理 Shell Agent 耦合 | 5、6 |
| 10 | 收缩脚本、CI、依赖和打包 | 5、9 |
| 11 | 审计场景模型和持久化重复 | 前面行为稳定后 |
| 12 | 命名、目录、注释与资源整理 | 所有迁移完成后 |

## 17. 不应执行的清理

- 不删除 Explorer host、场景引擎或编辑器来换取代码更少。
- 不删除 `AppLauncher`/`WindowManager` 而破坏用户驱动的原生 UI。
- 不在未决定 Shell 状态前删除恢复脚本。
- 不把旧 Windows MCP 替换为无限制 PowerShell。
- 不用新的自研 Agent UI 替代原生 cards。
- 不清空用户场景、播放列表、模型或 Harness 会话数据。
- 不让升级包保留已删除 executable/profile 并继续被旧快捷方式启动。
- 不以“能编译”作为清理完成标准。

## 18. 完成定义

必须同时满足：

- `docs` 只保留当前设计、批准的 ADR/规格、执行计划和必要 Runbook；
- 默认产品只有三个主界面；
- 没有旧 MainWindow 仪表盘的隐藏业务依赖；
- 只有一个 Desktop Settings；
- 只有官方 Harness WebUI 深度工作台；
- 没有 4317 Runtime、4318 Capability、旧 profile 和 Windows MCP；
- 没有原生 Agent cards 或旧 Runtime 状态 UI；
- L3 仍是无工具、低资源路径；
- Shell 状态明确，恢复能力经过验证；
- 脚本、CI、依赖、安装包和 README 与设计一致；
- 覆盖升级能删除旧文件且不丢失用户数据；
- win-x64/win-arm64 支持状态明确。
