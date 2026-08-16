TuringDesk v0.7 Replacement Shell Developer Preview
===================================================

Target: Windows 11 x64 / ARM64
Use the package that matches the Windows architecture:
- TuringDesk-v0.7-win-x64
- TuringDesk-v0.7-win-arm64

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
- Native Windows ARM64 packaging uses win-arm64 .NET publishes, ARM64 Node.js, and an ARM64-native final Harness/MCP smoke test.

DESKTOP
- Reads current-user Desktop plus Public Desktop.
- Uses native Windows icons when available.
- Double-click opens targets through normal ShellExecute behavior.
- Current Windows wallpaper is mirrored when readable.
- External FileDrop into the primary desktop is explicit user action and copy-only.

START MENU
- Indexes current-user and all-user Start Menu shortcuts.
- Search filters locally.
- Right-click indexed applications to pin/unpin them from TuringDesk taskbars.

TASKBAR / APPBAR
- Start, Show Desktop, TuringDesk control center and Task Switcher.
- Persistent pinned application area.
- Drag pinned icons to reorder them.
- Pin state/order synchronize across monitor taskbars during the current shell session.
- Native top-level application task buttons remain ordinary Windows windows.
- Network / sound / power status area, TuringDesk notifications, clock and session/power menu.

NOTIFICATION CENTER
- Stores TuringDesk-owned Shell and Agent activity for the current process lifetime.
- Shows shell startup, pin changes, desktop operations and Agent replies/failures.
- Can be cleared explicitly by the user.
- Does not emulate Explorer's undocumented third-party notification-area protocol.

TASK SWITCHING
- Windows native Alt+Tab remains untouched.
- Ctrl+Alt+Space opens the TuringDesk Task Switcher when the hotkey can be registered.

RECOVERY
Normal recovery:
- Open the Session / Power menu on any TuringDesk Shell Bar.
- Choose Restore Explorer and confirm.
- ShellHost restores the previous/current-user shell policy and starts explorer.exe.

Emergency recovery:
1. Press Ctrl+Shift+Esc to open Task Manager.
2. Run a new task: powershell
3. Execute:
   & "$env:LOCALAPPDATA\TuringDesk\Shell\Restore-Explorer.ps1"
4. Sign out/sign in if needed.

AUTOMATIC FAIL-SAFE
- ShellHost supervises TuringDesk Desktop.
- Repeated early shell exits cause automatic current-user Explorer recovery.
- Recovery state is stored under HKCU\Software\TuringDesk\Shell.
- The shell policy is current-user only; v0.7 does not overwrite the machine-wide Winlogon Shell value.

VOICE
- Windows Desktop Speech recognition remains available when an installed recognizer is available.
- Keyboard and mouse continue to work normally when speech is unavailable.

MODEL SETUP
- Choose DeepSeek, Ollama, LM Studio, OpenAI-compatible, or Mock.
- Cloud API setup is designed around selecting a provider and pasting an API key.
- API keys are stored in Windows Credential Manager.
- DeepSeek Harness is bundled and starts automatically for real model providers.

EMBEDDED AGENT KERNEL
- DeepSeek Harness runtime family: 0.1.0-rc.6.
- TuringDesk loads its own Cordis profile and Windows MCP bridge.
- Harness startup identity is verified before a real model is accepted.
- The Windows capability path remains Harness -> MCP -> Capability API -> Win32.

SAFETY BOUNDARY
- Current-user shell replacement only.
- No machine-wide Winlogon Shell overwrite.
- No driver installation.
- No service installation.
- No unrestricted PowerShell/Bash capability is exposed to the Agent.
- Destructive Agent capabilities such as file.delete, install and power actions are still not exposed.
- Power/session actions are explicit user UI actions with confirmation.
- Inbound desktop drops are explicit user action and copy-only.

KNOWN LIMITATIONS
- Third-party Explorer notification-area/tray icons are not hosted yet.
- Jump Lists are not implemented.
- Desktop icon free-position persistence is not implemented.
- Wallpaper sync is not yet per-monitor slideshow aware.
- Win+D registration depends on Windows allowing that reserved hotkey in the active shell session.
- Native icon extraction can be denied for protected/elevated processes.
- Full-screen games and unusual exclusive-mode applications still need VM/real-device testing.
- This is unsigned developer software, so SmartScreen may warn.
