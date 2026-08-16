TuringDesk v0.10 ARM64 Replacement Shell
========================================

Target: Windows 11 ARM64
Artifact: TuringDesk-v0.10-win-arm64

PRODUCT DIRECTION
TuringDesk has two equal product pillars:
1. Deep Windows desktop replacement: TuringDesk owns the desktop surface, taskbar/AppBar, Start, task switching, desktop files, multi-monitor shell state, customization and recovery path.
2. DeepSeek Harness integration: Harness is the reasoning/execution kernel of the desktop, not a floating chatbot. Its execution state and trajectory are visible in the shell and it acts only through TuringDesk-owned MCP/Capability boundaries.

DIRECT INSTALL FLOW
v0.10 is a direct replacement-shell package and does not ship Preview or normal-app entry points.
1. Extract the entire ARM64 archive.
2. Double-click Install-TuringDesk.cmd.
3. TuringDesk stages the new build under %LOCALAPPDATA%\TuringDesk\Versions\<version-build> and points the CURRENT USER replacement Shell at that staged ShellHost.
4. TuringDesk asks whether to sign out now.
5. Choose Yes to sign out immediately; the next sign-in enters TuringDesk instead of Explorer.
6. Choose No only if you want to postpone the new Shell until a later sign-out/sign-in.

NEW IN v0.10
- Replaces visible placeholder glyphs in Start/taskbar/desktop shell surfaces with a reusable vector ShellIcon system.
- Start fixed actions now have semantic Browser / Code / Terminal / Folder / Agent / Search / Settings / Desktop icons.
- App and desktop-item fallbacks use semantic vector icons when Windows cannot provide a native file/app icon.
- Taskbar pinned-app fallback and Agent send affordance no longer use placeholder text glyphs.
- Desktop blank-area context menu now includes Refresh, New Folder, New Text Document, Paste, Open Desktop Folder, Display Settings, Personalization and TuringDesk Control Center.
- Desktop item context menu now includes Open, Open File Location, Copy, Rename, Move to Recycle Bin and Properties.
- Desktop keyboard workflow now supports Enter, F2, Delete, Ctrl+C and Ctrl+V.
- Delete is an explicit user action with confirmation and sends the item to the Windows Recycle Bin instead of permanently deleting it.
- Existing v0.9 Desktop DIY Center, Agent Conversation card, Agent Execution Trace card and DeepSeek Harness trajectory remain intact.

DESKTOP DIY CENTER
- Presets: System Fluent, Deep Space, Graphite.
- Wallpaper: follow Windows / custom image / solid dark background.
- Wallpaper fill: Cover / Contain / Stretch.
- Accent: configurable #RRGGBB accent applied live to shell resources.
- Taskbar opacity: live persisted setting.
- Agent dynamic cards: enabled state, opacity, completion auto-hide seconds, left/right placement.
- Model & DeepSeek Harness settings remain available from inside the DIY Center.

AGENT DYNAMIC CARDS
Conversation card:
- automatically appears when an Agent command starts from Control Center, taskbar input or voice flow;
- shows request, live phase, Run ID and final reply/error;
- request and response text can be selected, edited, copied and pasted.

Execution Trace card:
- appears together with the Conversation card;
- refreshes from /v1/agent/state while the run is active;
- surfaces Runtime / DeepSeek Harness / MCP-oriented trace steps when available;
- does not expose hidden model chain-of-thought; it only shows product execution events and tool/runtime trajectory.

DEEPSEEK HARNESS
- DeepSeek Harness runtime family: 0.1.0-rc.6.
- Every real model provider is mediated by the bundled Harness kernel.
- TuringDesk loads its own Cordis profile and Windows MCP bridge.
- Execution remains Harness -> TuringDesk MCP -> Capability API -> Win32.
- The shell can expose execution status/trajectory without granting unrestricted shell access.

DESKTOP / TASKBAR
- Real current-user + Public Desktop items with native Windows icons when available.
- Semantic TuringDesk vector icon fallback when native icon extraction is unavailable.
- Windows-style blank-area context operations and practical file-item context operations.
- File/folder drag-in remains copy-only and never silently removes the source.
- Custom/system wallpaper is applied by TuringDesk desktop surfaces and updates live from DIY settings.
- Taskbar includes Start, Show Desktop, Control Center, Task Switcher, pinned apps, window tasks, Agent input, status area, DIY Settings, notifications, clock and session/power.
- Pinned apps persist and synchronize across monitor taskbars.

RECOVERY
Normal recovery:
- Open the Session / Power menu on any TuringDesk taskbar.
- Choose Restore Explorer and confirm.

Emergency recovery:
1. Press Ctrl+Shift+Esc to open Task Manager.
2. Run a new task: powershell
3. Execute:
   & "$env:LOCALAPPDATA\TuringDesk\Restore-Explorer.ps1"
4. Sign out/sign in if required.

AUTOMATIC FAIL-SAFE
- ShellHost supervises TuringDesk Desktop.
- Repeated early shell exits cause automatic current-user Explorer recovery.
- Recovery state is stored under HKCU\Software\TuringDesk\Shell.
- TuringDesk does not overwrite the machine-wide Winlogon Shell value.

SAFETY BOUNDARY
- Current-user replacement Shell only.
- No machine-wide Shell overwrite.
- No driver or Windows service installation.
- No unrestricted PowerShell/Bash capability is exposed to Agent.
- No file.delete, package install, admin or power capability is exposed to Harness.
- Desktop recycle/rename/copy operations are explicit user UI actions, not Agent capabilities.
- Power/session actions remain explicit user UI actions with confirmation.
- Agent trace cards expose execution events, not private chain-of-thought.

KNOWN LIMITATIONS
- Third-party Explorer notification-area/tray icons are not fully hosted yet.
- Jump Lists are not implemented.
- Desktop icon free-position persistence is not implemented yet.
- Wallpaper slideshow/per-monitor independent wallpaper behavior still needs deeper implementation.
- Full native third-party Explorer context-menu extension hosting is not implemented yet; v0.10 provides the common Windows desktop operations directly in TuringDesk.
- Full-screen games and unusual exclusive-mode applications still need VM/real-device testing.
- This is unsigned developer software, so SmartScreen may warn.
