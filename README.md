# TuringDesk（图灵桌面）

TuringDesk 是一个面向 Windows 的 **可编程桌面引擎 + AI 工作台入口**。

产品由两个同等重要的部分组成：

1. Wallpaper Engine 式桌面体验：场景播放、导入、编辑、多屏、播放列表、应用规则和性能控制。
2. 分层 AI 工作流：桌面搜索栏负责应用、文件和轻量多轮对话，复杂任务进入官方 DeepSeek Harness WebUI。

## 默认产品结构

TuringDesk 默认保留 Explorer，只提供三个一级界面：

1. **桌面搜索栏**
2. **桌面设置**
3. **AI 工作台**

搜索栏采用固定的四级路由：

```text
L1 应用
L2 文件
L3 常驻轻量多轮对话
L4 官方 DeepSeek Harness 工作台
```

L3 不拥有本机工具，也不启动 Node/Harness。只有用户明确选择“深度处理”时，TuringDesk 才按需启动官方 `dsh --profile web` 并在 WebView2 中显示。

## 设计基线

产品方向、界面边界、目标架构和发布标准统一由以下文档定义：

- [TuringDesk 产品与系统设计规范](docs/TURINGDESK-DESIGN-SPEC.md)
- [首个对外版本 Scene 发布范围](docs/V1-SCENE-RELEASE-SCOPE.md)

后续开发计划：

- [AI 工作台统一与旧 Agent 链路清理计划](docs/AI-WORKBENCH-CONSOLIDATION-PLAN.md)
- [旧代码与仓库冗余清理计划](docs/LEGACY-REDUNDANCY-CLEANUP-PLAN.md)

如果 README、任务描述、历史提交或当前代码行为与设计规范冲突，以已评审的设计规范为准。

## 当前架构方向

```text
Windows / Explorer
  -> TuringDesk Desktop Scene Engine
  -> Desktop Search Bar
       -> application search
       -> file search
       -> lightweight L3 conversation
       -> explicit L4 handoff
  -> Desktop Settings
  -> Harness WebView2 Window
       -> official dsh --profile web
```

目标架构不再包含：

- 第二套 TuringDesk Agent Runtime；
- SDK JSON-RPC Harness/Cordis profile；
- 原生 Agent Conversation/Trace cards；
- 默认 Windows MCP 或 Capability Server；
- 旧 Home/Workspace/Tasks/Memory 仪表盘。

这些旧链路已从代码中清除。设计文档保留历史决策记录。

## 开发与验证

Windows 目标机快速验证入口：

```text
QUICK-VERIFY.cmd
```

当前脚本已移除旧 4317/4318 端口检查，验证目标为三个主界面和场景引擎。

## Replacement Shell

默认模式保留 Explorer。仓库中的 Replacement Shell 是高级、显式、独立验收范围，不得决定默认产品架构，也不得为它保留第二套 Agent 内核。

是否继续正式支持 Replacement Shell，需要在后续产品评审中明确。启用、恢复和卸载安全机制在做出决定前不得破坏。

## 安全原则

- 默认增强模式不修改 Windows Shell 注册表。
- 不默认获取管理员权限。
- 不向 L3 轻量对话暴露本机文件或工具。
- 官方工作台默认不注入旧 TuringDesk Windows MCP。
- API Key 不进入命令行、普通日志或错误页面。
- 场景、AI 或 WebView 故障不得破坏 Explorer。

## License

MIT
