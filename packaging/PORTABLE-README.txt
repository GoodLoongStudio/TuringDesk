TuringDesk v0.6 Replacement Shell Developer Preview
===================================================

Target: Windows 11 x64

IMPORTANT
v0.6 can REALLY replace Explorer for the current Windows user. Test inside a VM first. Do not enable login shell mode until normal mode and shell preview both work on that machine.

SAFE ORDER
1. Extract the whole archive to a normal folder.
2. Double-click Start-TuringDesk.cmd and verify normal mode.
3. Run Preview-TuringDeskShell.ps1 and verify shell mode without changing login settings.
4. Verify desktop icons, wallpaper, Start search, taskbar, persistent pins, task switcher, Agent input and Explorer recovery.
5. Only then double-click Enable-TuringDeskShell.cmd.
6. Sign out and sign in again. TuringDesk ShellHost will start instead of Explorer for this user.

WHAT "REAL SHELL" MEANS
- Explorer.exe is not the login shell for the configured user.
- TuringDesk owns the visible desktop surfaces, Start menu and bottom taskbar/AppBar surfaces.
- Native apps remain ordinary top-level Windows windows.
- TuringDesk does not embed Chrome, VS Code or other desktop apps into its own window.
- Agent text entry remains available from each TuringDesk Shell Bar.

NEW IN v0.6
- Desktop surfaces follow the current Windows wallpaper when a readable wallpaper file is available.
- Desktop right-click menu now exposes Refresh, New Folder, Desktop Folder, Display Settings, Personalization and TuringDesk control center.
- Taskbar pins are no longer hard-coded: they persist in %LOCALAPPDATA%\TuringDesk\shell-settings.json.
- Right-click a running task with an accessible executable path to pin it to the TuringDesk taskbar.
- Right-click a pinned taskbar item to unpin it.
- A TuringDesk Task Switcher can be opened from the taskbar or Ctrl+Alt+Space.
- Windows native Alt+Tab behavior remains untouched.
- A Session / Power menu now provides Lock, Sign out, Restart, Shut down and Restore Explorer actions.
- Restart, Shut down, Sign out and Explorer recovery require an explicit user confirmation and are not exposed as Agent tools.

DESKTOP
- Reads the current user's Desktop plus Public Desktop.
- Displays real Windows file/folder/shortcut icons when the Shell API can provide them.
- Double-click opens the target through normal Windows ShellExecute behavior.
- Right-click the desktop for familiar shell actions.
- Hidden/system desktop entries are not shown.
- Desktop contents and current wallpaper refresh automatically.

START MENU
- Indexes current-user and all-user Windows Start Menu shortcut trees.
- Search filters app names and categories locally.
- Indexed shortcuts use native Windows Shell icons when available.

TASKBAR / APPBAR
- Start button.
- Show Desktop button.
- TuringDesk control-center button.
- TuringDesk Task Switcher button.
- Persistent pinned app area.
- Native application task buttons with process icons when accessible.
- Clicking the active task minimizes it; clicking an inactive task restores/focuses it.
- Right-click running tasks to pin when their process path is accessible.
- Right-click pinned apps to unpin.
- Network / sound / power status area.
- Clock/date.
- Session / Power menu.

TASK SWITCHING
- Windows native Alt+Tab is intentionally not replaced in v0.6.
- Ctrl+Alt+Space opens the TuringDesk Task Switcher when the hotkey can be registered.
- The Task Switcher lists windows across monitors and focuses the selected native window.

STATUS AREA NOTE
v0.6 does NOT pretend to be fully compatible with Explorer's third-party notification area. TuringDesk implements its own network/sound/power status first instead of depending on undocumented Explorer internals.

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
- If the desktop shell repeatedly exits shortly after startup, ShellHost automatically removes the TuringDesk CustomShell policy and launches explorer.exe.
- Recovery state is stored under HKCU\Software\TuringDesk\Shell.
- The shell policy is current-user only; v0.6 does not overwrite the machine-wide Winlogon Shell value.

WINDOWS INTEGRATION
TuringDesk uses the Windows Custom User Interface / CustomShell current-user policy:
HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\System\Shell

VOICE
- Windows Desktop Speech recognition remains always-on when an installed recognizer is available.
- Say “图灵桌面” and then a command.
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
- Start Menu and desktop launches happen only from explicit user clicks/double-clicks.

KNOWN LIMITATIONS
- Multi-monitor support is still evolving; pinned apps are shared across displays rather than independently configured per monitor.
- Third-party Explorer notification-area/tray icons are not hosted yet.
- Jump lists and drag-reorder of taskbar pins are not complete.
- Win+D registration depends on Windows allowing that reserved hotkey in the active shell session.
- Native icon extraction can be denied for protected/elevated processes; those task buttons may have no icon and cannot always be pinned.
- Wallpaper synchronization currently follows the classic Windows desktop wallpaper path and is not yet per-monitor slideshow aware.
- Full-screen games and unusual exclusive-mode applications still need VM/real-device testing.
- This is unsigned developer software, so SmartScreen may warn.
