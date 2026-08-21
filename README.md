# TuringDesk

TuringDesk 正在进行原生架构重构。

## 当前仓库状态

旧的 C# / .NET 8 / WPF 实现已经冻结并归档到：

`legacy/turingdesk-wpf/`

该目录仅用于行为参考、资产迁移和历史回溯，不再作为新产品主线继续开发。

新的 TuringDesk 将在本仓库重新定义技术架构，设计依据包括：

- 已确认的 TuringDesk 产品需求与设计基线；
- Windows 桌面引擎、顶部搜索、L1/L2/L3/L4 分层需求；
- Wallpaper Engine 等成熟桌面产品的架构与资源管理方式；
- 低常驻内存、低空闲 CPU、按需启动重组件；
- 核心能力可跨平台复用，Windows 系统集成保持平台专属实现。

## 文档

产品需求与设计基线继续保留在 `docs/`。

新的技术架构将在 `docs/architecture/` 中形成正式文档后，再创建新的 Native 工程骨架。

## Legacy

归档前基线：`8a43540f6b57223713f93a90517a3487e71f30fa`
