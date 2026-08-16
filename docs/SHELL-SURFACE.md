# TuringDesk Shell Surface v0.4

TuringDesk v0.4 moves from "a program that can be the Windows shell" toward a usable AI-native desktop environment.

## Session layout

```text
Windows sign-in
  -> TuringDesk.ShellHost
      -> bundled Runtime / Harness
      -> hidden TuringDesk control center host
      -> DesktopSurfaceWindow
      -> ShellBarWindow (Windows AppBar)
          -> StartMenuWindow
          -> native application task buttons
          -> persistent Agent command box
```

The control center remains alive while hidden because it owns the Windows capability server, model configuration, activity state and always-on speech service.

## Desktop surface

`DesktopSurfaceWindow` is the visible shell background in replacement-shell mode. It indexes:

- current-user Desktop
- Public Desktop

Desktop items are opened only after direct user interaction. TuringDesk does not alter or move the underlying files simply to render the desktop.

## Start menu

`StartMenuWindow` indexes shortcut entries from the current-user and all-user Windows Start Menu trees. Search is local and filters the indexed shortcut name/category. Launching still delegates to Windows ShellExecute so existing `.lnk`, `.url` and registered protocol behavior remains compatible.

## Taskbar behavior

`ShellBarWindow` is still a real AppBar. v0.4 adds:

- Start menu toggle
- Show Desktop
- TuringDesk control center
- pinned Chrome / VS Code / Terminal launchers
- native top-level window task buttons
- active-window indicator
- click-active-task-to-minimize behavior
- persistent Agent command input
- Explorer recovery button

The AppBar re-queries its position when Windows sends display, DPI or work-area changes.

## Safety boundary

v0.4 does not broaden Agent permissions. Desktop and Start menu launching are user-driven UI actions; unrestricted shell execution is not exposed as an Agent tool. The replacement-shell policy remains current-user-only and ShellHost retains the automatic Explorer recovery path.

## Next shell milestones

1. Extract native Windows file/app icons instead of generic glyphs.
2. Multi-monitor desktop and independent taskbars.
3. Notification area / system tray compatibility layer.
4. Persistent taskbar pinning and drag reorder.
5. Jump lists and recent documents.
6. Shell-level global hotkeys such as Win / Win+D without interfering with secure Windows shortcuts.
7. Rich Agent-native Start results that combine applications, files, tasks and actions with permission-aware execution.
