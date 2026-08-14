# TuringDesk（图灵桌面）

**An agent-native desktop environment for Windows.**

TuringDesk is an AI-first desktop shell that sits on top of Windows. It keeps the Windows app ecosystem, but moves the primary interaction model from **App → UI → Action** to **Intent → Agent → Capability → Result**.

> v0.1 is intentionally safe: it runs as a normal maximized Windows app and does **not** replace `explorer.exe`.

## v0.1

The first bootstrap contains:

- Windows desktop shell UI (WPF/.NET 8)
- AI command bar and activity feed
- quick launching for Chrome, VS Code and Terminal
- native Win32 window discovery, focus and tiling
- a stable local Runtime HTTP boundary (`127.0.0.1:4317`)
- mock runtime by default, so the desktop works without an API key
- DeepSeek Harness gateway using the official newline-delimited stdio JSON-RPC protocol
- one persistent Harness session for desktop chat context
- architecture and roadmap docs
- Windows + Node CI

A built-in local command already supports the first demo loop:

> `打开 Chrome 和 VS Code，左右排列`

TuringDesk launches the apps (when installed), finds their real top-level Windows windows, and tiles them side by side.

## Architecture

```text
Windows 11
   │
   ├── TuringDesk.Desktop (.NET / WPF)
   │      ├── AI Desktop UI
   │      ├── App Launcher
   │      └── Win32 Window Manager
   │
   └── TuringDesk Runtime (Node / TypeScript)
          ├── stable local API
          ├── Harness JSON-RPC Gateway
          └── DeepSeek Harness (optional in v0.1)
```

See [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

## Prerequisites

- Windows 11
- .NET 8 SDK
- Node.js 22.19+
- Corepack / pnpm

## Run

### 1. Start the AI runtime

```powershell
cd runtime
corepack enable
pnpm install
pnpm dev
```

By default the runtime starts in `mock` mode, so no model credentials are required.

### 2. Start the desktop

In another PowerShell window:

```powershell
dotnet run --project src/TuringDesk.Desktop/TuringDesk.Desktop.csproj
```

Or run both with:

```powershell
./scripts/run-dev.ps1
```

## DeepSeek Harness mode on Windows

TuringDesk does not modify Harness Core and does not require the Harness npm client package. The runtime speaks Harness's documented stdio JSON-RPC protocol directly and launches an external Harness JSON-RPC runtime process.

For v0.1 on Windows, use a source-built DeepSeek Harness Node runtime:

```powershell
git clone https://github.com/deepseek-ai/deepseek-harness.git
cd deepseek-harness
corepack enable
pnpm install
pnpm run build
```

Then configure TuringDesk before starting `pnpm dev` in `TuringDesk/runtime`:

```powershell
$env:DEEPSEEK_API_KEY="sk-..."
$env:TURINGDESK_RUNTIME_MODE="harness"
$env:TURINGDESK_HARNESS_COMMAND="node"
$env:TURINGDESK_HARNESS_ARGS='["C:\\path\\to\\deepseek-harness\\packages\\examples\\jsonrpc-demo\\lib\\bin.js","C:\\path\\to\\deepseek-harness\\examples\\jsonrpc-agent\\cordis.yml"]'
$env:TURINGDESK_HARNESS_PROVIDER="deepseek-official"
$env:TURINGDESK_HARNESS_MODEL="deepseek-v4-flash"
$env:TURINGDESK_AGENT_CWD="C:\\Users\\you"
pnpm dev
```

The exact Harness checkout/runtime command is deliberately external. TuringDesk owns only the stable gateway boundary, so Harness can be pinned and upgraded independently.

> DeepSeek Harness currently ships its bundled Python SDK runtime carriers for Linux/macOS, while the Harness product itself has native Windows runtime paths. TuringDesk therefore uses the Node/source-build path on Windows for this developer preview.

## Repository layout

```text
src/TuringDesk.Desktop/    Windows desktop shell
runtime/                   AI runtime + Harness protocol boundary
docs/                      architecture and roadmap
scripts/                   developer commands
.github/workflows/         CI
```

## Safety

v0.1 does not request administrator privileges, replace Explorer, or give an LLM unrestricted PowerShell/root-style access. Destructive/system-level capabilities will go through an explicit permission broker in later versions.

## Status

**v0.1 bootstrap / developer preview.** The next milestone is wiring Windows capabilities into Harness as typed tools so natural-language requests can invoke the same native window/app APIs through policy-controlled tool calls.
