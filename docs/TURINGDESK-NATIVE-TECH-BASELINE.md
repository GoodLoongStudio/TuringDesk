# TuringDesk Native 技术路线基线

- 状态：已确认
- 日期：2026-08-21
- 适用范围：TuringDesk 新 Native 主线
- 旧实现：`legacy/turingdesk-wpf/`，仅作为参考，不再作为新主线架构
- 产品基线：`docs/TURINGDESK-DESIGN-SPEC.md`

## 1. 产品只包含三个一级能力

TuringDesk 的顶层定义固定为：

```text
TuringDesk
├─ 1. Desktop Search：L1-L3
├─ 2. Wallpaper Engine 级桌面引擎
└─ 3. L4 DeepSeek Harness WebView
```

一句话定义：

> TuringDesk = 桌面 Search（L1-L3） + Wallpaper Engine 级桌面引擎 + DeepSeek Harness WebView（L4）。

任何新增功能首先必须明确归属于以上三块之一；不属于三块的功能默认不进入核心主线。

---

## 2. 总体技术原则

新主线以 Windows 上的性能、常驻内存、安装体积和系统集成质量为第一优先级，同时通过接口隔离给未来跨平台留下空间。

核心原则：

1. 常驻核心使用 C++23。
2. 常驻核心不依赖 WPF、.NET、Electron、CEF、Qt、Python、Node.js。
3. Web Runtime 只在确实需要 Web 的功能中按需启动。
4. L3 不依赖 Harness，不通过 WebView，不通过 Node/Python/本地代理。
5. L4 才启动 DeepSeek Harness 和 WebView2。
6. 普通动态 Scene 不使用 WebView2/Chromium，走原生 GPU 渲染。
7. Web Wallpaper 独立进程运行，避免污染主进程常驻内存。
8. 代码存在、编译通过、Mock/Loopback 通过均不等于产品完成；真实设备可用才算完成。

---

## 3. 一级能力一：Desktop Search（L1-L3）

### 3.1 技术栈

```text
语言                 C++23
窗口                 Win32 HWND
UI 绘制              Direct2D
文字                 DirectWrite
合成/动画            DirectComposition
网络                 WinHTTP
凭据                 Windows Credential Manager
文件搜索 V1          Everything IPC / SDK
文件搜索 V2          NTFS USN Journal + 自研索引
```

Search 是最轻、最高频、长期常驻的用户入口，因此禁止使用 WPF、Qt、Electron 或 WebView 来实现主 Search UI。

### 3.2 L1：应用搜索

由 TuringDesk 自己维护轻量应用索引，来源包括：

```text
Start Menu
App Paths
注册表应用信息
UWP / MSIX 应用
常用系统程序
```

排序能力至少包括：

```text
Exact
Prefix
Substring
Fuzzy
使用频率
最近启动
```

### 3.3 L2：文件搜索

V1 直接使用 Everything IPC / SDK，优先获得成熟、低延迟、低开发风险的文件搜索能力。

长期 V2 可替换为：

```text
NTFS USN Journal
    ↓
增量索引
    ↓
SQLite / mmap / 自定义紧凑索引
    ↓
TuringDesk L2
```

上层通过接口隔离，不把 Everything 绑定到业务层。

### 3.4 L3：TuringDesk 原生轻 Agent

L3 属于 TuringDesk 自己，不属于 Harness。

结构：

```text
Search
  ↓
L3 Tool Router
  ├─ Native Tools
  └─ Model Provider
       ↓
     WinHTTP
       ↓
DeepSeek / OpenAI-compatible
```

L3 模型通信：

```text
API Key
  ↓
Windows Credential Manager
  ↓
WinHTTP HTTPS
  ↓
/chat/completions
  ↓
SSE streaming
  ↓
Search UI 实时显示
```

明确禁止：

```text
L3 → Harness
L3 → WebView
L3 → Node.js sidecar
L3 → Python sidecar
L3 → 本地 HTTP 代理
模型输出 → 任意 Shell 直接执行
```

所有本地动作必须通过注册、校验、可审计的 Tool 边界执行。

---

## 4. 一级能力二：Wallpaper Engine 级桌面引擎

这部分按“小型专用原生游戏引擎”设计，而不是普通 UI 模块。

### 4.1 核心技术栈

```text
语言                 C++23
桌面集成             Win32 / Explorer / WorkerW
图形 API             Direct3D 11
2D                   Direct2D
Shader               HLSL
显示/适配器          DXGI
视频                 Media Foundation
音频采集             WASAPI Loopback
音频分析             原生 FFT
Web Wallpaper        独立 WebView2 Host 进程
Application Wallpaper 外部 EXE + 原生窗口/进程管理
```

首版优先 Direct3D 11，而不是 Direct3D 12：兼容性更高、驱动风险更低、资源管理更简单，并且壁纸场景性能已经足够。后续如有明确需求，再增加 D3D12 backend。

### 4.2 Scene Wallpaper

原生 Scene Engine 至少包含：

```text
Scene Graph
├─ Sprite
├─ Mesh
├─ Texture
├─ Material
├─ HLSL Shader
├─ Particle
├─ Animation
├─ Camera
├─ Light
└─ Post Processing
```

目标能力按 Wallpaper Engine 级别持续补齐，包括：

```text
图片壁纸
视频壁纸
Web 壁纸
2D Scene
3D Scene
Shader
粒子
动画
音频响应
交互
脚本
多显示器
性能规则
Application Wallpaper
壁纸导入/管理/编辑/切换
```

### 4.3 视频壁纸

视频链路：

```text
Video File
  ↓
Media Foundation
  ↓
Hardware Decode
  ↓
D3D11 Texture
  ↓
Desktop Renderer
```

优先硬件解码，降低 CPU 占用。

### 4.4 音频响应

```text
WASAPI Loopback
  ↓
FFT
  ↓
Bass / Mid / Treble / Spectrum
  ↓
Scene Parameters
```

可驱动粒子、Glow、Shader 参数、动画强度等。

### 4.5 Web Wallpaper

Web Wallpaper 不进入主进程：

```text
TuringDesk.WebWallpaper.exe
  ↓
WebView2
```

只有当前壁纸确实是 Web 类型时才启动该进程。

普通图片、视频、2D/3D Scene 不得因此加载 WebView2。

### 4.6 Application Wallpaper

```text
External EXE
  ↓
TuringDesk Process Manager
  ↓
Desktop Window Host / WorkerW
```

TuringDesk 负责启动、停止、窗口层级、异常退出恢复、性能规则和进程监控。

### 4.7 多显示器与系统生命周期

至少正确处理：

```text
每屏独立壁纸
复制壁纸
跨屏壁纸
主屏模式
不同 DPI
显示器插拔
Explorer 重启
睡眠/唤醒
锁屏
显卡设备丢失
桌面层重建
```

### 4.8 性能策略

支持状态级资源策略：

```text
全屏游戏      → Pause / Stop
最大化程序    → 降 FPS / Pause
电池模式      → 降 FPS / Pause
锁屏          → Stop
休眠          → Release GPU Resources
```

Stop 必须允许真正释放 Scene、Texture、Video Decoder、GPU Resource，而不是仅停止刷新。

---

## 5. 一级能力三：L4 DeepSeek Harness

L4 不重写 DeepSeek Harness。

技术路线固定为：

```text
用户进入 L4
  ↓
TuringDesk 启动官方 DeepSeek Harness
  ↓
等待 Harness WebUI 本地服务
  ↓
TuringDesk Harness Window
  ↓
WebView2
  ↓
官方 DeepSeek Harness WebUI
```

### 5.1 技术栈

```text
Host                 C++ / Win32
Web 容器             Microsoft Edge WebView2
AI Agent             官方 DeepSeek Harness
启动策略             按需启动
```

选择 WebView2 而不是 CEF，主要原因：

- Windows 通常已有 WebView2 Runtime；
- 不需要随安装包携带完整 Chromium；
- 安装体积更小；
- 与 Windows 生命周期、窗口和安全模型集成更自然。

### 5.2 与 L3 的关系

```text
L3 = TuringDesk Native Agent
L4 = DeepSeek Harness
```

二者是不同层级，不允许把 Harness 作为 L3 transport。

共享的只有用户模型配置：

```text
Model Configuration
├─ L3 → WinHTTP direct provider
└─ L4 → Harness synchronized configuration
```

Harness 关闭时，L1/L2/L3 必须继续完全可用。

---

## 6. 推荐进程模型

### 常驻

```text
TuringDesk.exe
C++ Native Core
├─ Tray
├─ Hotkey
├─ Search L1
├─ Search L2
├─ Search L3
├─ Desktop Host
├─ Scene Engine
└─ Process Manager
```

### 按需

```text
TuringDesk.WebWallpaper.exe
└─ WebView2
```

仅 Web Wallpaper 使用。

```text
DeepSeek Harness
+
TuringDesk Harness WebView Window
```

仅 L4 使用。

```text
Application Wallpaper EXE
```

仅对应壁纸启用时存在。

目标不是“让 WebView2/Chromium 变得极轻”，而是让它们根本不进入不需要它们的常驻路径。

---

## 7. 新工程基础选型

```text
Language              C++23
Build                  CMake
Windows API            Win32
Search UI              Direct2D + DirectWrite + DirectComposition
Wallpaper Renderer     Direct3D 11 + HLSL
Video                  Media Foundation
Audio                  WASAPI
HTTP / SSE             WinHTTP
Credential             Windows Credential Manager
File Search V1         Everything IPC / SDK
File Search V2         NTFS USN Journal
Web Wallpaper          WebView2 独立进程
L4                     DeepSeek Harness + WebView2
```

主线尽量不引入以下依赖：

```text
WPF / .NET
Qt
Electron
CEF
Python
Node.js（仅 Harness 自身需要时存在，不进入 Native Core）
大型游戏引擎
大型 Agent Framework
Boost（无明确收益时不引入）
```

---

## 8. 跨平台原则

Windows 首版优先极致原生体验，但核心接口不得让 Windows 类型无边界渗透到业务层。

例如：

```text
IHttpClient
ICredentialStore
IFileSearchBackend
IWindowHost
IRenderBackend
IAudioCapture
IAppDiscovery
```

Windows 实现：

```text
WinHttpClient
WindowsCredentialStore
Everything/UsnFileSearchBackend
Win32WindowHost
D3D11RenderBackend
WasapiAudioCapture
WindowsAppDiscovery
```

未来 macOS/Linux 可替换平台实现，而不重写 Search/L3/Scene 数据模型等上层逻辑。

---

## 9. 性能目标

以下为工程验收目标，不是未经测试的承诺值：

### 空闲 / 无动态壁纸

```text
TuringDesk Native Core
Private Memory：目标 < 30 MB
CPU idle：接近 0%
GPU idle：接近 0%
```

### 普通原生 Scene

内存主要由纹理、Scene 和 GPU Resource 决定；禁止因为普通 Scene 启动 WebView2、Node.js、.NET 或 Chromium。

### Harness / Web Wallpaper

允许显著增加内存，但关闭对应功能后，其进程与绝大部分资源必须释放。

---

## 10. 完成标准

统一使用以下定义：

```text
代码存在        ≠ 完成
编译成功        ≠ 完成
单元测试通过    ≠ 完成
Mock 通过       ≠ 完成
Loopback 通过   ≠ 完成
真实设备可用    = 完成
```

每个阶段必须有真实 Windows 设备黑盒验收。

特别是：

- L3 必须用真实模型 API 收到流式回复；
- Wallpaper 必须真实嵌入 Explorer 桌面层并通过多显示器/睡眠/Explorer 重启测试；
- L4 必须真实启动官方 DeepSeek Harness 并在 WebView2 中正常交互。

---

## 11. 架构边界总结

```text
                    TuringDesk
                  /      |       \
                 /       |        \
           Search     Wallpaper    Harness
           L1-L3       Engine        L4
             |            |           |
      Native C++      Native C++   WebView2
      WinHTTP         D3D11/MF     Official Harness
```

最终边界固定为：

> Search = C++ / Win32 / Direct2D / DirectWrite / DirectComposition / WinHTTP
>
> Wallpaper = C++ / Win32 / Direct3D 11 / HLSL / Media Foundation / WASAPI
>
> L4 = 官方 DeepSeek Harness + WebView2
>
> Web Wallpaper = 独立 WebView2 Host
>
> 其余常驻路径禁止 Web Runtime。
