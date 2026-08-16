TuringDesk v0.4 Replacement Shell Developer Preview
===================================================

Target: Windows 11 x64

IMPORTANT
v0.4 can REALLY replace Explorer for the current Windows user. Do not enable shell mode until the normal preview and shell preview both work on your PC.

SAFE ORDER
1. Extract the whole archive to a normal folder.
2. Double-click Start-TuringDesk.cmd and verify normal mode.
3. Run Preview-TuringDeskShell.ps1 and verify shell mode without changing login settings.
4. Only then double-click Enable-TuringDeskShell.cmd.
5. Sign out and sign in again. TuringDesk ShellHost will start instead of Explorer for this user.

WHAT "REAL SHELL" MEANS
- Explorer.exe is not the login shell for the configured user.
- TuringDesk owns the desktop surface, Start menu and bottom taskbar surface.
- TuringDesk Shell Bar registers as a Windows AppBar and reserves work area.
- Native apps remain normal top-level Windows windows.
- The taskbar lists running windows; clicking the active task minimizes it, clicking again restores/focuses it.
- Agent text entry is always available from the Shell Bar.

NEW IN v0.4
- A dedicated desktop surface replaces the Explorer desktop view.
- The desktop surface reads the current user's Desktop and Public Desktop folders.
- Desktop files, folders and shortcuts can be opened directly by double-clicking them.
- Desktop contents refresh automatically while the shell is running.
- A TuringDesk Start menu indexes current-user and all-user Windows Start Menu shortcuts.
- Start search filters installed shortcut names and categories.
- Pinned Chrome, VS Code, Terminal, Files and TuringDesk entries are available from Start.
- A Show Desktop button minimizes ordinary application windows and reveals the desktop surface.
- Shell Bar reacts to display, DPI and work-area changes and repositions its AppBar.

RECOVERY
Normal recovery:
- Click the right-most recovery arrow on the TuringDesk Shell Bar.
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
- The shell policy is current-user only; v0.4 does not overwrite the machine-wide Winlogon Shell value.

WINDOWS INTEGRATION
TuringDesk v0.4 uses the Windows Custom User Interface / CustomShell user policy:
HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\System\Shell

This is intentionally different from the Enterprise-only Shell Launcher feature.

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
- v0.4 is still primary-monitor-first; independent taskbars on each monitor come later.
- The Windows notification area/system tray is not reimplemented yet.
- Jump lists, taskbar pin persistence, drag-reorder and full Start Menu app metadata/icons are not complete yet.
- Desktop icons currently use TuringDesk glyphs instead of extracting native Windows shell icons.
- Full-screen games and unusual exclusive-mode applications still need real-device testing.
- This is unsigned developer software, so SmartScreen may warn.
