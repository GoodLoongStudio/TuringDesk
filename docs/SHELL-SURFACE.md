# TuringDesk Shell Surface v0.11

TuringDesk v0.11 is no longer just “a program that can be the Windows shell”. The product direction is a usable AI-native Windows desktop with familiar native behavior, deeper visual customization and an always-available Agent layer.

## Session layout

```text
Windows sign-in
  -> TuringDesk.ShellHost
      -> TuringDesk.Desktop --shell
          -> bundled Runtime / Harness
          -> DesktopSurfaceWindow
          -> ShellBarWindow (Windows AppBar)
              -> StartMenuWindow
              -> native application task buttons
              -> persistent Agent command box
          -> Conversation Card
          -> Execution Trace Card
          -> Harness WebView2 Console (optional)
```

Harness starts with TuringDesk. The WebView console is opened only when the user wants the full official Harness UI.

## Desktop surface

`DesktopSurfaceWindow` is the visible shell background in replacement-shell mode.

It indexes the current-user and Public Desktop without moving files merely to render them.

Desktop files and shortcuts use Windows-native icon extraction first. The fallback chain is:

```text
real file/app Shell Icon
    -> Windows Stock Icon
    -> TuringDesk vector fallback
```

This keeps Windows-native objects visually familiar while reserving TuringDesk's own vector language for product-specific features.

## Desktop interactions

The current shell supports practical Windows-style operations such as:

- open
- open containing folder
- copy / paste
- rename
- move to Recycle Bin with explicit user confirmation
- properties
- refresh
- new folder
- new text document
- display settings / personalization entry points
- keyboard flows such as Enter, F2, Delete, Ctrl+C and Ctrl+V

These direct desktop interactions are user-driven UI behavior and do not automatically become Agent capabilities.

## Start menu

`StartMenuWindow` indexes current-user and all-user Windows Start Menu shortcut trees.

Launching continues through Windows ShellExecute-compatible paths so `.lnk`, `.url`, registered protocols and normal Windows application behavior remain compatible.

Application entries prefer the shortcut/application's native Windows icon.

## Taskbar / Shell Bar

`ShellBarWindow` is a real Windows AppBar and participates in the work-area layout.

Current responsibilities include:

- Start
- Show Desktop
- TuringDesk / Control Center access
- pinned applications
- top-level native window task buttons
- active-window state
- task switching
- persistent Agent input
- status / clock / session surfaces
- Desktop DIY access
- Explorer recovery

Pinned app identity and task icons should prefer the underlying process/application icon rather than replacing known application branding with TuringDesk artwork.

## Agent-native surfaces

TuringDesk keeps two lightweight native cards available even when the official Harness WebUI is closed.

### Conversation Card

Shows the current request, Agent phase/status, Run ID and final reply/error.

### Execution Trace Card

Shows product-visible Runtime / Harness / MCP execution events and tool trajectory.

It must not expose hidden model chain-of-thought.

The cards can be customized through Desktop DIY settings, including visibility, opacity, side placement and completion auto-hide behavior.

## Harness console

The “DeepSeek Harness” entry opens the upstream official Harness WebUI in a WebView2 shell.

This is a full console, not a replacement for the desktop's compact Agent interaction.

Closing the console leaves the Harness service and TuringDesk quick Agent surfaces available.

## Desktop DIY

TuringDesk preserves its own desktop visual system independently of Harness WebUI.

Current customization concepts include:

- System Fluent / Deep Space / Graphite presets
- follow Windows / custom / solid wallpaper modes
- Cover / Contain / Stretch image fitting
- accent color
- taskbar opacity
- Agent card opacity
- Agent card side placement
- Agent card auto-hide timing

## Safety boundary

The shell surface must remain familiar and recoverable:

- replacement-shell configuration is current-user only
- Explorer remains installed and recoverable
- direct file operations require explicit user interaction
- ShellHost provides a failure recovery path
- Harness actions remain behind TuringDesk MCP / Capability boundaries

## Next shell milestones

1. Full third-party notification-area / tray compatibility.
2. Jump Lists and recent documents.
3. Desktop free-position persistence.
4. More complete multi-monitor shell behavior.
5. Richer Windows Explorer context-menu extension compatibility.
6. Global shell hotkeys without interfering with secure Windows shortcuts.
7. Deeper full-screen game / exclusive-mode compatibility testing.
8. Signed installer and x64 release build.
