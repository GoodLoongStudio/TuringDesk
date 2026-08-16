TuringDesk v0.9 ARM64 Replacement Shell
=======================================

Target: Windows 11 ARM64
Artifact: TuringDesk-v0.9-win-arm64

PRODUCT DIRECTION
TuringDesk has two equal product pillars:
1. Deep Windows desktop replacement: TuringDesk owns the desktop surface, taskbar/AppBar, Start, task switching, desktop files, multi-monitor shell state, customization and recovery path.
2. DeepSeek Harness integration: Harness is the reasoning/execution kernel of the desktop, not a floating chatbot. Its execution state and trajectory are visible in the shell and it acts only through TuringDesk-owned MCP/Capability boundaries.

DIRECT INSTALL FLOW
v0.9 no longer ships Preview or normal-app entry points in the user-facing package.
1. Extract the entire ARM64 archive.
2. Double-click Install-TuringDesk.cmd.
3. TuringDesk stages the new build under %LOCALAPPDATA%\TuringDesk\Versions\<version-build> and points the CURRENT USER replacement Shell at that staged ShellHost. This allows installing a new TuringDesk build while an older Shell build is still running.
4. TuringDesk asks whether to sign out now.
5. Choose Yes to sign out immediately; the next sign-in enters the newly installed TuringDesk directly instead of Explorer.
6. Choose No only if you want to postpone the new Shell until a later sign-out/sign-in.

NEW IN v0.9
- ARM64 is the only produced Windows package/artifact.
- The package is direct replacement-shell only; Preview-TuringDeskShell and Start-TuringDesk are intentionally not shipped.
- Direct upgrades use versioned staging instead of overwriting the currently running Shell binaries.
- A Wallpaper Engine-inspired Desktop DIY Center is accessible from the taskbar Settings button and the Control Center Settings button.
- DIY properties persist in %LOCALAPPDATA%\TuringDesk\shell-settings.json and update the live shell.
- Customization includes wallpaper source/path/fill, accent color, taskbar opacity, Agent card enable/opacity/auto-hide/side, plus reusable visual presets.
- The DIY Center contains a live desktop/taskbar/Agent-card preview and links to model + DeepSeek Harness settings.
- Calling Agent dynamically opens TWO independent floating cards: Conversation and Execution Trace.
- Conversation and Trace card text uses editable/selectable TextBox controls, so standard select/copy/paste and Ctrl+C/Ctrl+V behavior works.
- Conversation card shows the user prompt, result/error and Run ID.
- Trace card polls live Runtime state and renders real TuringDesk/Harness trace items.
- DeepSeek Harness gateway forwards selected live Harness session/tool/reasoning/checkpoint events into the Runtime trace state.
- Agent cards can auto-hide after completion, stay visible indefinitely, move to the left/right side, or be disabled from DIY Center.

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
- request and response text can be selected, edited, copied and pasted;
- includes Paste and Copy All controls.

Execution Trace card:
- appears together with the Conversation card;
- refreshes from /v1/agent/state while the run is active;
- surfaces Runtime / DeepSeek Harness / MCP-oriented trace steps when available;
- trace text can be selected, edited, copied and pasted;
- does not expose hidden model chain-of-thought; it only shows product execution events and tool/runtime trajectory.

DEEPSEEK HARNESS
- DeepSeek Harness runtime family: 0.1.0-rc.6.
- Every real model provider is mediated by the bundled Harness kernel.
- TuringDesk loads its own Cordis profile and Windows MCP bridge.
- Execution remains Harness -> TuringDesk MCP -> Capability API -> Win32.
- The shell can expose execution status/trajectory without granting unrestricted shell access.

DESKTOP / TASKBAR
- Real current-user + Public Desktop items with native Windows icons when available.
- Windows-style desktop blank-area context operations remain available.
- Desktop files retain Open / Open file location / Properties actions.
- Custom/system wallpaper is applied by TuringDesk desktop surfaces and updates live from DIY settings.
- Taskbar includes Start, Show Desktop, Control Center, Task Switcher, pinned apps, window tasks, Agent input, status area, DIY Settings, notifications, clock and session/power.
- Pinned apps persist and synchronize across monitor taskbars.

SHELL ACTIVATION / SIGN-OUT
Install-TuringDesk.cmd is the user-facing entry point.
Internally it invokes Enable-TuringDeskShell.ps1.
After install/update TuringDesk asks whether the user wants to sign out immediately.
- Yes: current user signs out and the staged replacement Shell takes effect on next sign-in.
- No: current session continues and the staged new Shell waits until a later sign-out/sign-in.

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
- The currently selected version directory is recorded in the same state key.
- TuringDesk does not overwrite the machine-wide Winlogon Shell value.

SAFETY BOUNDARY
- Current-user replacement Shell only.
- No machine-wide Shell overwrite.
- No driver or Windows service installation.
- No unrestricted PowerShell/Bash capability is exposed to Agent.
- No file.delete, package install, admin or power capability is exposed to Harness.
- Power/session actions remain explicit user UI actions with confirmation.
- Agent trace cards expose execution events, not private chain-of-thought.

KNOWN LIMITATIONS
- Third-party Explorer notification-area/tray icons are not fully hosted yet.
- Jump Lists are not implemented.
- Desktop icon free-position persistence is not implemented yet.
- Wallpaper slideshow/per-monitor independent wallpaper behavior still needs deeper implementation.
- Full native Explorer context-menu extension hosting is not implemented yet.
- Full-screen games and unusual exclusive-mode applications still need VM/real-device testing.
- This is unsigned developer software, so SmartScreen may warn.