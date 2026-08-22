# TuringDesk

TuringDesk 是 Windows 11 ARM64 原生 AI 桌面：桌面壁纸引擎 + 顶部搜索入口 + AI/Agent 工作台 + DeepSeek Harness Web UI 宿主。

## 当前正式平台

- Windows 11 ARM64
- C++23 / Native Win32
- 正式交付只从 `main` 构建
- CI 只构建、验证和发布 ARM64

正式原生目标：

- `TuringDesk.exe` — Search / L1-L3 / 设置中心
- `TuringDeskWallpaper.exe` — 桌面壁纸引擎
- `TuringDeskHarness.exe` — DeepSeek Harness WebView2 宿主

旧 C# / .NET / WPF 实现已冻结在 `legacy/turingdesk-wpf/`，只用于历史参考。

## 一键测试

在 Windows 11 ARM64 上同步 `main` 后，只运行：

```text
DEPLOY-NATIVE-ARM64.cmd
```

脚本会：

1. `git pull --ff-only` 同步 `main`；
2. 从仓库自身 `runtime/arm64/` 校验并展开固定 RuntimeBundle；
3. 获取当前 `main` 已通过 CI 的 ARM64 三个原生 EXE；
4. 运行 Search / Wallpaper / Harness self-test；
5. 真实启动一次仓库内 DeepSeek Harness 做 smoke test；
6. 启动 TuringDesk。

第三方运行时不会在用户机器现场通过 npm、Node 官网、NuGet、Everything 官网或 Codex Release 下载。Node、DeepSeek Harness 完整生产依赖树、Everything、Codex ARM64 和编译所需 WebView2 SDK 都由 `runtime/arm64/` 的固定版本清单管理。

> Windows 11 自带/系统维护的 Microsoft Edge WebView2 Runtime 视为操作系统组件；仓库内固定的是 WebView2 SDK 和 ARM64 static loader。

## DeepSeek Harness

TuringDesk 不 fork、不魔改 DeepSeek Harness。仓库 vendoring 流程从官方 `@deepseek-ai/dsh` 固定版本生成完整离线生产依赖树；运行时 `TuringDeskHarness.exe` 只允许：

```text
Runtime\Node\node.exe
Runtime\Node\node_modules\@deepseek-ai\dsh\lib\bin.js web --host 127.0.0.1 --port 3080
```

不会回退到系统 Node、全局 npm、`npx` 或在线安装。

## RuntimeBundle

版本锁：`runtime/arm64/runtime-lock.json`

完整性检查：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/verify-arm64-runtime-bundle.ps1
```

RuntimeBundle 的生成、版本和目录约定见 `runtime/arm64/README.md`。

## 文档

产品、技术和 Wallpaper Engine parity 路线位于 `docs/`。
