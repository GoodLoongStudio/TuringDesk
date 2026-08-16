TuringDesk v0.5 Replacement Shell Developer Preview
===================================================

Target: Windows 11 x64

IMPORTANT
v0.5 can REALLY replace Explorer for the current Windows user. Test inside a VM first. Do not enable login shell mode until normal mode and shell preview both work on that machine.

SAFE ORDER
1. Extract the whole archive to a normal folder.
2. Double-click Start-TuringDesk.cmd and verify normal mode.
3. Run Preview-TuringDeskShell.ps1 and verify shell mode without changing login settings.
4. Verify desktop icons, Start search, taskbar, Agent input and Explorer recovery.
5. Only then double-click Enable-TuringDeskShell.cmd.
6. Sign out and sign in again. TuringDesk ShellHost will start instead of Explorer for this user.

WHAT "REAL SHELL" MEANS
- Explorer.exe is not the login shell for the configured user.
- TuringDesk owns the visible desktop surfaces, Start menu and bottom taskbar/AppBar surfaces.
- Native apps remain ordinary top-level Windows windows.
- TuringDesk does not embed Chrome, VS Code or other desktop apps into its own window.
- Agent text entry remains available from each TuringDesk Shell Bar.

NEW IN v0.5
- Native Windows Shell icons are extracted for desktop items, Start Menu shortcuts and running task buttons when available.
- One desktop surface is created for every detected display.
- One Windows AppBar taskbar is created for every detected display.
- Desktop files/folders remain on the primary display; secondary displays use a clean TuringDesk desktop surface.
- Running task buttons are filtered to the display containing each window.
- Display topology is checked while the shell is running and surfaces rebuild when monitors are added, removed or rearranged.
- The taskbar now has a TuringDesk-owned status area for network availability, sound settings and battery/AC status.
- Ctrl+Esc is registered as a Start shortcut when Windows allows it.
- TuringDesk attempts to register Win+D for Show Desktop. Windows reserves Win-key hotkeys, so registration can legitimately fail without breaking the shell.

DESKTOP
- Reads the current user's Desktop plus Public Desktop.
- Displays real Windows file/folder/shortcut icons when the Shell API can provide them.
- Double-click opens the target through normal Windows ShellExecute behavior.
- Hidden/system desktop entries are not shown.
- Desktop contents refresh automatically.

START MENU
- Indexes current-user and all-user Windows Start Menu shortcut trees.
- Search filters app names and categories locally.
- Indexed shortcuts use native Windows Shell icons when available.
- Pinned Chrome, VS Code, Terminal, Files and TuringDesk entries remain available.

TASKBAR / APPBAR
- Start button.
- Show Desktop button.
- TuringDesk control-center button.
- Pinned Chrome / VS Code / Terminal launchers.
- Native application task buttons with process icons when accessible.
- Clicking the active task minimizes it; clicking an inactive task restores/focuses it.
- Network / sound / power status area.
- Clock/date.
- Explorer recovery button.

STATUS AREA NOTE
v0.5 does NOT pretend to be fully compatible with Explorer's third-party notification area. Windows documents Shell_NotifyIcon for applications sending icons to the taskbar status area, but the complete third-party-shell receiving/hosting behavior is not a stable public compatibility contract. v0.5 therefore implements TuringDesk-owned system status indicators first instead of relying on undocumented Explorer internals.

RECOVERY
Normal recovery:
- Click the right-most recovery arrow on any TuringDesk Shell Bar.
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
- The shell policy is current-user only; v0.5 does not overwrite the machine-wide Winlogon Shell value.

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
- Start Menu and desktop launches happen only from explicit user clicks/double-clicks.

KNOWN LIMITATIONS
- Multi-monitor support is a foundation: independent bars/surfaces exist, but advanced per-monitor pinning and workspace persistence are not complete.
- Third-party Explorer notification-area/tray icons are not hosted yet.
- Jump lists, taskbar pin persistence and drag-reorder are not complete.
- Win+D registration depends on Windows allowing that reserved hotkey in the active shell session.
- Native icon extraction can be denied for protected/elevated processes; those task buttons may have no icon.
- Full-screen games and unusual exclusive-mode applications still need VM/real-device testing.
- This is unsigned developer software, so SmartScreen may warn.
