# TuringDesk Architecture

## Product boundary

TuringDesk is a **Windows AI Desktop**, not a Windows kernel replacement. Windows remains the compatibility, driver and application layer. TuringDesk becomes the user's primary desktop/intent surface.

```text
User
 │
 ▼
TuringDesk Desktop
 ├─ AI Workspace
 ├─ App Surfaces
 ├─ Window Manager
 └─ Permission UX
 │
 ▼
TuringDesk Runtime API   ← stable product boundary
 │
 ├─ Agent Gateway
 ├─ Capability Registry
 ├─ Policy Broker
 └─ Context/Memory
 │
 ▼
DeepSeek Harness         ← replaceable upstream runtime
 │
 ▼
Models / Agents / Tools
```

## Why Harness is isolated

DeepSeek Harness is still rapidly evolving. TuringDesk must not spread imports of Harness packages throughout UI and Windows code. All Harness-specific behavior belongs behind `runtime/src/harness-gateway.ts` (and later dedicated plugin packages).

Rules:

1. Never modify Harness Core for normal TuringDesk features.
2. Pin a known-good Harness SDK/runtime version.
3. Keep Windows capabilities owned by TuringDesk contracts.
4. Add compatibility tests before upgrading Harness.
5. Fork Harness only when a required capability cannot be implemented through a documented extension seam.

## Process model

### Desktop process

`TuringDesk.Desktop.exe`

Responsibilities:

- render the desktop shell
- launch Windows applications
- discover/focus/layout top-level windows
- show permission prompts
- present AI/runtime activity

It should not own model credentials or execute privileged system mutations directly.

### Runtime process

Node/TypeScript service on loopback only.

Responsibilities:

- own agent sessions
- talk to DeepSeek Harness
- route model requests
- translate agent actions into TuringDesk capability calls
- maintain AI-specific context

In v0.1 this is a small HTTP process. Later it can move to named pipes/JSON-RPC without changing the desktop capability contract.

### Privileged broker (planned)

A separate signed Windows service will own capabilities that require elevation. The LLM will never receive a raw unrestricted administrator shell by default.

## Capability model

Planned stable names:

```text
app.list
app.launch
app.close

window.list
window.focus
window.move
window.resize
window.tile

file.search
file.open
file.move
file.copy
file.delete

system.status
system.volume.set
system.power.shutdown

ui.inspect
ui.invoke
ui.input
```

The implementation can change (Win32, UI Automation, Windows App SDK, PowerShell, etc.) without changing the agent-facing capability name.

## Automation priority

When TuringDesk needs to operate another app, prefer:

1. native API / app integration
2. Windows UI Automation
3. browser DOM integration
4. computer vision
5. synthetic mouse/keyboard input

This keeps automation reliable and auditable.
