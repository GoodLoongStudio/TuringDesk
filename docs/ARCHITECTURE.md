# TuringDesk Architecture

## Product boundary

TuringDesk is a **Windows AI Desktop**, not a Windows kernel replacement.

Windows remains the compatibility, driver, application and security foundation. TuringDesk sits above Windows as the user's primary desktop / intent surface and optional current-user replacement Shell.

```text
User
 │
 ▼
TuringDesk Desktop / Shell
 ├─ Desktop Surface
 ├─ Start / Taskbar / Window Surfaces
 ├─ Desktop DIY / Theme
 ├─ Voice + Quick Agent Input
 ├─ Conversation Card
 ├─ Execution Trace Card
 └─ Harness WebView2 Console (optional UI)
 │
 ▼
TuringDesk Runtime / Capability API
 │
 ├─ Agent Gateway
 ├─ Capability Registry
 ├─ Policy Boundary
 └─ Agent Activity / Trace Projection
 │
 ▼
DeepSeek Harness                 ← always-on while TuringDesk is running
 │
 ├─ official Agent runtime
 ├─ official sessions
 └─ official WebUI (:4319, opened on demand through WebView2)
 │
 ▼
TuringDesk Windows MCP
 │
 ▼
Win32 / Windows Apps
```

## Core rule: Harness service != Harness WebView

The DeepSeek Harness process is part of the TuringDesk runtime lifecycle.

When TuringDesk starts, it starts/ensures the bundled Harness service in the background. The user does **not** need to open the Harness WebUI first.

The official Harness WebUI is only a full-console presentation surface. TuringDesk wraps it with WebView2 so it behaves like a native desktop panel instead of exposing a browser address bar or tabs.

Closing the WebView window does not disable the Agent kernel.

Quick text input, voice commands, Conversation Cards and Execution Trace Cards remain TuringDesk-native surfaces and continue to work independently of the WebView window.

## Why Harness stays isolated

DeepSeek Harness is an upstream runtime and will continue to evolve. TuringDesk must not spread Harness implementation details through shell, UI and Win32 code.

Rules:

1. Do not modify Harness Core for normal TuringDesk features.
2. Pin and test a known-good Harness runtime family.
3. Keep Windows capabilities owned by TuringDesk contracts.
4. Keep the Harness WebUI upstream instead of rebuilding it in TuringDesk.
5. Add compatibility/smoke tests before upgrading Harness.
6. Fork Harness only when a required capability cannot be implemented through supported extension seams.

v0.11 pins the bundled runtime family to `0.1.0-rc.6`.

## Process model

### `TuringDesk.Desktop.exe`

Responsibilities:

- render the desktop shell and control surfaces
- launch Windows applications through reviewed desktop paths
- discover/focus/layout top-level windows
- host the Capability Server boundary
- own always-on speech UX
- show quick Agent input
- show Conversation / Execution Trace Cards
- host the official Harness WebUI inside WebView2 when requested
- start/ensure Harness during application startup without blocking desktop presentation

The Desktop process should not expose unrestricted administrator shell access to the model.

### `TuringDesk.ShellHost.exe`

ShellHost is the resilient replacement-shell supervisor.

Responsibilities:

- start TuringDesk Desktop in replacement-shell mode
- monitor repeated early exits
- preserve shell recovery state
- restore Explorer rather than entering a shell crash loop

ShellHost is intentionally small and separate from the visible desktop UI.

### TuringDesk Runtime

Node/TypeScript runtime bound to local machine interfaces only.

Responsibilities:

- own TuringDesk-side agent execution state
- integrate with DeepSeek Harness
- expose the reviewed Windows MCP bridge
- route structured capability requests
- project product-visible execution activity to the desktop
- keep model/runtime logic outside WPF view code

### DeepSeek Harness

Harness is the Agent reasoning/execution kernel of the desktop.

TuringDesk packages and supervises the upstream runtime. It uses the official Harness Web profile for the full WebUI and a TuringDesk-owned Cordis/MCP integration layer for Windows capabilities.

The important boundary is:

```text
Harness
  -> TuringDesk MCP
    -> TuringDesk Capability API
      -> reviewed Win32 / Windows operation
```

Harness does not receive unrestricted raw Windows administration by default.

## UI ownership

TuringDesk owns the desktop-native UX even though the full Harness console is upstream.

TuringDesk-owned surfaces include:

- Desktop Surface
- Start Menu
- Taskbar / AppBar
- Desktop DIY Center
- quick Agent text input
- voice controls
- Conversation Card
- Execution Trace Card
- approval / recovery UX

Harness-owned UI:

- the full official Harness WebUI presented inside a WebView2 shell

This avoids maintaining two competing full Agent frontends while preserving the product's AI-native desktop experience.

## Icon ownership

Icon resolution follows this priority:

```text
real Windows file/app Shell icon
        ↓
Windows Stock Icon
        ↓
TuringDesk vector fallback
```

The TuringDesk executables and installer use the TuringDesk brand Application Icon.

## Capability model

Stable capability naming should remain independent of the underlying implementation.

Current reviewed surface includes the window/app family such as:

```text
app.launch
window.list
window.find
window.focus
window.move
window.resize
window.tile
```

Future families may include safe file, UI Automation, browser and system capabilities, but they should be added behind policy rather than by exposing a generic unrestricted shell.

## Automation priority

When TuringDesk needs to operate another application, prefer:

1. native API / app integration
2. Windows UI Automation
3. browser DOM integration
4. computer vision
5. synthetic mouse/keyboard input

This keeps automation more reliable, observable and auditable.

## Installation boundary

v0.11 formalizes the Windows product lifecycle through MSI.

- MSI owns Program Files application files.
- MSI provides Start menu / repair / upgrade / uninstall lifecycle.
- replacement-shell activation is an explicit post-install user action.
- shell activation changes only the current-user policy.
- real uninstall restores Explorer before installed shell files are removed.

TuringDesk does not overwrite the machine-wide Winlogon Shell value.
