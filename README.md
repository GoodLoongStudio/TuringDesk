# TuringDesk（图灵桌面）

**An agent-native desktop environment for Windows.**

TuringDesk is an AI-first desktop shell that sits on top of Windows. It keeps familiar Windows apps and interaction patterns, while adding a persistent Agent layer that can translate intent into reviewed desktop capabilities.

> Product rule: **80% familiar desktop + 20% agent-native interaction.**

## v0.2 developer preview

TuringDesk v0.2 includes:

- Windows desktop shell UI (WPF / .NET 8)
- persistent Turing Agent rail and bottom command bar
- always-on Windows Desktop Speech / SAPI wake-phrase flow
- beginner-friendly model onboarding with Windows Credential Manager storage
- DeepSeek, Ollama, LM Studio and custom OpenAI-compatible model entry points
- **DeepSeek Harness embedded as the Agent Kernel for every real model**
- automatic bundled `dsh-jsonrpc-agent` startup and supervision
- a TuringDesk-owned Harness Cordis profile
- automatic Windows MCP registration
- loopback-only Windows Capability API (`127.0.0.1:4318`)
- allow-listed app launch and native Win32 window management
- Mock mode for safe no-key testing
- self-contained Windows x64 portable packaging

TuringDesk v0.2 remains a normal application. It does **not** replace `explorer.exe`, install a driver/service, request administrator privileges, or modify the Windows shell registry.

## Architecture

```text
Windows 11
   |
   +-- TuringDesk.Desktop (.NET / WPF)
   |      +-- familiar desktop shell
   |      +-- always-on voice entry
   |      +-- model onboarding
   |      +-- Capability Server :4318
   |      +-- App Launcher / Window Manager
   |
   +-- TuringDesk Runtime (Node / TypeScript) :4317
          |
          +-- bundled Harness supervisor / JSON-RPC gateway
                 |
                 v
          DeepSeek Harness 0.1.0-rc.6
                 |
                 +-- Agent spine / sessions / compaction
                 +-- DeepSeek model adapter
                 +-- generic OpenAI-compatible model adapter
                 +-- TuringDesk Windows MCP client
                         |
                         v
                  Windows MCP server
                         |
                         v
                  Capability Server :4318
                         |
                         v
                  Win32 / Windows apps
```

See [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md), [`docs/HARNESS-MCP.md`](docs/HARNESS-MCP.md), and [`docs/DESKTOP-UX.md`](docs/DESKTOP-UX.md).

## Portable build

The intended first-test experience is the self-contained Windows x64 package:

```text
TuringDesk-v0.2-win-x64/
  Start-TuringDesk.cmd
  desktop/
  runtime/
    node/node.exe
    app/
      server.js
      harness/turingdesk.cordis.yml
      node_modules/@deepseek-ai/...
```

Extract the package and double-click `Start-TuringDesk.cmd`. Node, .NET runtime files, DeepSeek Harness, the TuringDesk Harness profile, and the MCP bridge are bundled; the user does not install or configure Harness manually.

## Model setup

Open **模型** and select a provider:

- **DeepSeek API** — paste the API key; Harness uses the official DeepSeek adapter.
- **Ollama** — set the local model ID; API key is normally unnecessary.
- **LM Studio** — set the local model ID; API key is normally unnecessary.
- **OpenAI-compatible API / gateway** — set Base URL, model ID and key if required.
- **Mock** — no model/key; safe desktop capability testing.

Every non-Mock provider is routed through the embedded DeepSeek Harness Agent Kernel. Ollama, LM Studio and custom gateways use Harness's generic provider adapter rather than bypassing the Agent runtime.

Secrets are stored in Windows Credential Manager. The JSON settings file contains model metadata, not the raw API key.

## Windows Agent capabilities in v0.2

Stable TuringDesk capability names:

```text
app.launch
window.list
window.find
window.focus
window.move
window.resize
window.tile
```

Harness receives these through the TuringDesk MCP server. Example intent:

> `打开 Chrome 和 VS Code，左右排列`

The Agent can launch allow-listed applications, discover their real top-level HWNDs, and tile them through policy-controlled native operations.

## Safety

Embedding Harness does not mean giving the model an unrestricted system shell. The v0.2 TuringDesk Harness profile deliberately omits unrestricted Bash, PowerShell, filesystem mutation, package installation, power and administrator tools.

Current rules include:

- no Explorer replacement
- no administrator requirement
- no shell-registry takeover
- no driver/service installation
- capability API is loopback-only
- `app.launch` is allow-listed to Chrome, VS Code and Windows Terminal
- TuringDesk refuses to manage its own window through the window API
- no close/delete/install/power capability yet
- move/resize operations are clamped to the Windows work area

## Harness integration acceptance

CI does not accept a portable build based on TypeScript compilation alone. Before the Windows artifact is uploaded it must pass:

1. Harness dependency installation.
2. Runtime typecheck/build.
3. TuringDesk MCP smoke test.
4. Real bundled `dsh-jsonrpc-agent` boot.
5. TuringDesk Cordis profile load and MCP startup.
6. JSON-RPC `initialize` identity verification (`deepseek-harness-sdk-runtime`).
7. Windows desktop Release build.
8. A second Harness boot from the **final portable directory** using the embedded Windows `node.exe` and packaged production dependencies.

## Development

Prerequisites for source development only:

- Windows 11 for native desktop testing
- .NET 8 SDK
- Node.js 22.19+
- Corepack / pnpm

Run Runtime:

```powershell
cd runtime
corepack enable
pnpm install
pnpm dev
```

Run Desktop in another PowerShell:

```powershell
dotnet run --project src/TuringDesk.Desktop/TuringDesk.Desktop.csproj
```

Or:

```powershell
./scripts/run-dev.ps1
```

Normal users of the portable package do not need these developer prerequisites.

## Repository layout

```text
src/TuringDesk.Desktop/    Windows desktop shell + native capability server
runtime/                   Runtime + bundled Harness gateway + MCP server
runtime/harness/           TuringDesk-owned Harness Cordis profile
docs/                      architecture / Harness / UX docs
packaging/                 portable notices and user guide
scripts/                   developer and packaging commands
.github/workflows/         CI + portable acceptance gates
```

## Status

**v0.2 developer preview.** The embedded DeepSeek Harness Agent Kernel, Windows MCP bridge, always-on voice entry, model onboarding and native window/app capabilities are wired and CI-gated. Real-world microphone quality and live provider/model behavior still require normal Windows 11 interactive testing.
