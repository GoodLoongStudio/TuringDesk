TuringDesk v0.7 Replacement Shell Developer Preview
===================================================

Target: Windows 11 x64

IMPORTANT
v0.7 can REALLY replace Explorer for the current Windows user. Test inside a VM first. Verify Preview mode and Explorer recovery before enabling the login shell.

SAFE ORDER
1. Extract the whole archive.
2. Run Start-TuringDesk.cmd and verify normal mode.
3. Run Preview-TuringDeskShell.ps1.
4. Test desktop, Start, taskbar, notifications, Agent input and Restore Explorer.
5. Run Enable-TuringDeskShell.cmd only after recovery works.
6. Sign out and sign in again.

NEW IN v0.7
- Start Menu apps can be right-clicked to pin/unpin them from the TuringDesk taskbar.
- Pinned apps can be dragged to reorder them.
- Pin/unpin/reorder changes synchronize across all TuringDesk taskbars in the current shell session.
- External files/folders can be dropped onto the primary TuringDesk desktop; TuringDesk copies them into the user's Desktop folder with collision-safe names.
- Desktop items can be dragged outward using the standard Windows FileDrop data format.
- A TuringDesk Notification Center now collects Shell and Agent activity.
- Agent replies, taskbar changes, desktop drops and desktop folder creation can surface as TuringDesk notifications.

DESKTOP
- Reads current-user Desktop plus Public Desktop.
- Uses native Windows icons when available.
- Double-click opens targets through normal ShellExecute behavior.
- Current Windows wallpaper is mirrored when readable.
- Right-click: Refresh, New Folder, Desktop Folder, Display Settings, Personalization, TuringDesk.
- Drop files/folders onto the primary desktop to COPY them to Desktop. TuringDesk does not silently move/delete the source.

START MENU
- Indexes current-user and all-user Start Menu shortcut trees.
- Local search by app name/category.
- Native Windows shortcut icons when available.
- Right-click an indexed app to pin/unpin it from the TuringDesk taskbar.

TASKBAR / APPBAR
- Start, Show Desktop, TuringDesk control center and Task Switcher.
- Persistent pinned app area stored in %LOCALAPPDATA%\TuringDesk\shell-settings.json.
- Drag pinned apps to reorder them.
- Pin changes synchronize across multi-monitor taskbars in the same TuringDesk process.
- Native running-window task buttons; click active task to minimize, inactive task to focus/restore.
- Right-click a running task with an accessible executable path to pin it.
- Agent input, network/sound/power status, Notification Center, clock/date and Session/Power menu.

NOTIFICATION CENTER
- TuringDesk-owned notifications only; this is not an emulation of Explorer's undocumented third-party tray-host protocol.
- Shows recent Shell and Agent activity.
- Can be cleared by the user.

TASK SWITCHING
- Native Windows Alt+Tab remains untouched.
- Ctrl+Alt+Space opens the TuringDesk Task Switcher when registration succeeds.

SESSION / POWER
- Lock, Sign out, Restart, Shut down and Restore Explorer.
- High-impact actions require explicit user confirmation.
- These actions remain UI-only and are not exposed to Harness/MCP.

RECOVERY
Normal: Session / Power -> Restore Explorer -> confirm.
Emergency:
1. Ctrl+Shift+Esc
2. Run new task: powershell
3. Execute:
   & "$env:LOCALAPPDATA\TuringDesk\Shell\Restore-Explorer.ps1"
4. Sign out/sign in if needed.

AGENT KERNEL
- DeepSeek Harness runtime family: 0.1.0-rc.6.
- Bundled TuringDesk Cordis profile + Windows MCP bridge.
- Harness identity is verified before accepting a real model provider.
- Capability path remains Harness -> MCP -> Capability API -> Win32.

SAFETY BOUNDARY
- Current-user CustomShell only; no machine-wide Winlogon Shell overwrite.
- No driver/service install.
- No unrestricted PowerShell/Bash tool for the Agent.
- Destructive file/delete/install/power capabilities remain unavailable to the model.
- Desktop drag/drop is explicit user interaction and COPY-only on inbound drops.
- Session/power actions require explicit user interaction and confirmation.

KNOWN LIMITATIONS
- Third-party Explorer notification-area/tray icons are not hosted yet.
- Jump Lists are not implemented yet.
- Taskbar pin dragging currently reorders relative to another pinned item; advanced free-position/drop indicators come later.
- Desktop icon free-position persistence is not implemented; items are still laid out by TuringDesk.
- Wallpaper sync is not yet per-monitor slideshow aware.
- Win+D registration can fail because Windows reserves Win-key hotkeys.
- Fullscreen/exclusive-mode apps still need VM/real-device testing.
- Unsigned developer build: SmartScreen may warn.
