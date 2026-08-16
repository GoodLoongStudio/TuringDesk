# TuringDesk Desktop Enhancement Mode

## Goal

TuringDesk v0.12 changes the default desktop strategy from replacing Explorer to enhancing Explorer.

The normal product path is now:

```text
Windows / DWM
  |
  +-- Explorer
  |    +-- Desktop icons
  |    +-- Start / taskbar
  |    +-- notification area
  |    +-- shell context menus / compatibility
  |
  +-- TuringDesk
       +-- desktop visual scene (behind Explorer icons)
       +-- AI floating cards / overlays
       +-- always-on speech
       +-- DeepSeek Harness
       +-- Windows MCP / Capability API
```

Replacement Shell remains available as an advanced mode and is no longer the default route.

## Explorer desktop host

`ExplorerDesktopHost` discovers Explorer's `Progman` / `WorkerW` wallpaper hierarchy and reparents the TuringDesk scene HWND into the wallpaper layer behind `SHELLDLL_DefView`.

The scene window is configured as:

- non-activating,
- hidden from Alt-Tab/taskbar,
- mouse transparent,
- a child of Explorer's wallpaper host,
- sized to the Windows virtual desktop.

If the wallpaper host cannot be found, TuringDesk does not cover or replace Explorer. AI Runtime, Harness, voice and ordinary control-center features remain usable.

## Scene architecture

The v0.12 first renderer is intentionally small: it reuses the existing wallpaper source plus a light ambient visual layer.

The host boundary is renderer-independent. Future renderers should plug into the same Explorer desktop host:

```text
ExplorerDesktopHost
       |
       v
Desktop Scene Host
  +-- WPF ambient renderer      (v0.12 bootstrap)
  +-- Direct3D / DirectComposition renderer
  +-- GPU video renderer
  +-- Web / WebView renderer
  +-- AI-reactive scene renderer
```

The long-term target for rich scenes is Direct3D / DirectComposition rather than putting all animation into WPF.

## Performance behavior

Desktop visuals must yield to foreground applications.

v0.12 includes foreground full-screen detection. When another application occupies the full monitor, TuringDesk pauses its ambient scene layer while keeping the base wallpaper and AI services alive.

Future video / Direct3D renderers should implement the same policy with stronger levels:

- `keep` — continue rendering,
- `pause` — keep resources but stop animation/render ticks,
- `stop` — release heavy GPU/video resources and restore on resume.

Harness, speech, MCP and Agent services are independent from this visual pause policy.

## Launch modes

Normal launch:

```text
TuringDesk.Desktop.exe
```

Starts Desktop Enhancement Mode and keeps Explorer.

Control-center-only diagnostic launch:

```text
TuringDesk.Desktop.exe --control-only
```

Does not attach the desktop scene.

Advanced replacement shell:

```text
TuringDesk.Desktop.exe --shell
```

Runs the existing TuringDesk replacement-shell surfaces. This mode remains explicit and opt-in.

## Product rule

TuringDesk should use Windows-native functionality whenever Windows already provides the mature implementation. TuringDesk owns the layers where it adds unique value: programmable desktop visuals, AI interaction, Agent execution, voice, context, and automation.
