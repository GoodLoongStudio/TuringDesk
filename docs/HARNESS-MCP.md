# DeepSeek Harness + TuringDesk Windows MCP

TuringDesk v0.11 embeds DeepSeek Harness as the desktop Agent kernel.

Users do not need to install Harness separately, provide a Harness command, or manually assemble a Cordis profile for the normal packaged product.

## Runtime topology

```text
TuringDesk Desktop
      |
      | starts/ensures on app startup
      v
DeepSeek Harness 0.1.0-rc.6
      |
      | TuringDesk MCP integration
      v
runtime/app/windows-mcp-server.js
      |
      | HTTP loopback only
      v
TuringDesk Capability Server :4318
      |
      v
Win32 / Windows apps
```

The official Harness WebUI is also available:

```text
DeepSeek Harness Web profile :4319
      |
      v
TuringDesk HarnessConsoleWindow
      |
      v
WebView2
```

The WebView window is optional UI. It is **not** the Harness service lifecycle owner.

## Always-on Harness lifecycle

When `TuringDesk.Desktop.exe` starts, TuringDesk begins ensuring the bundled Harness service in the background.

Important product behavior:

- TuringDesk desktop appearance is not blocked while Harness boots.
- Quick Agent text input does not require the user to open the WebView first.
- Voice Agent commands do not require the WebView first.
- Conversation Card / Execution Trace Card remain available without the WebView.
- Opening the Harness console reuses/ensures the same local Harness service path.
- Closing the Harness WebView does not mean “stop the Agent kernel”.

If the background Harness startup fails, the desktop remains usable and surfaces the error instead of making the entire shell unusable.

## Official WebUI strategy

TuringDesk intentionally does **not** maintain a second full Harness frontend.

The complete Harness control surface is the upstream official WebUI, launched using the bundled `dsh --profile web` path and displayed inside WebView2.

TuringDesk adds only the desktop-native shell around it:

- no browser address bar
- no browser tabs
- native TuringDesk window chrome
- local-only URL
- TuringDesk Windows MCP patch injected at startup

This keeps compatibility with future Harness UI improvements while avoiding duplicate implementation.

## TuringDesk-native Agent UI remains

Using the official Harness WebUI does not remove TuringDesk's own AI desktop experience.

TuringDesk continues to own:

- quick Agent command input
- always-on voice interaction
- Conversation Card
- Execution Trace Card
- runtime/model status surfaces
- desktop approvals and Windows-specific UX

The cards show product-visible state and execution trajectory, not private model chain-of-thought.

## Embedded runtime

v0.11 pins the published DeepSeek Harness runtime family to:

```text
0.1.0-rc.6
```

TuringDesk-owned integration profile:

```text
runtime/harness/turingdesk.cordis.yml
```

The packaged runtime includes native dependencies required by Harness, including the Windows ARM64 `node-pty` / subprocess support used by the upstream runtime.

Developer overrides such as `TURINGDESK_HARNESS_COMMAND` remain development escape hatches and are not required by normal installation.

## Windows tools

The reviewed TuringDesk capability family currently includes:

```text
app.launch
window.list
window.find
window.focus
window.move
window.resize
window.tile
```

Harness sees MCP-qualified tool names such as:

```text
mcp__turingdesk__app_launch
mcp__turingdesk__window_list
mcp__turingdesk__window_find
mcp__turingdesk__window_focus
mcp__turingdesk__window_move
mcp__turingdesk__window_resize
mcp__turingdesk__window_tile
```

## Safety boundary

Harness is the reasoning/execution kernel, but it does not receive unrestricted Windows authority.

Current rules include:

- Capability endpoint binds only to loopback.
- Windows actions pass through TuringDesk-owned MCP / Capability contracts.
- No unrestricted PowerShell/Bash tool is exposed by default.
- No unrestricted administrator capability is exposed by default.
- Destructive desktop file operations remain explicit user UI actions unless a separately reviewed Agent capability is added later.
- Power/session operations remain explicit user actions with confirmation.
- TuringDesk avoids exposing its own HWND to Agent window-management operations.
- Move/resize operations are constrained to valid Windows work areas.

## CI acceptance gate

The v0.11 ARM64 MSI is uploaded only after the important integration gates pass:

- Runtime install / typecheck / build
- MCP protocol smoke test
- bundled DeepSeek Harness boot
- TuringDesk Cordis profile load
- final installed-layout Harness Agent smoke test
- final installed-layout official Harness WebUI smoke test
- Windows Desktop + ShellHost Release build
- executable Application Icon extraction check
- MSI generation and artifact verification

This is intentionally stronger than a source-only compile check: Harness is tested again from the same layout that is placed in the Windows installer.
