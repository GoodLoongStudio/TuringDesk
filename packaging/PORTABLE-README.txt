TuringDesk v0.3 Replacement Shell Developer Preview
===================================================

Target: Windows 11 x64

IMPORTANT
v0.3 can REALLY replace Explorer for the current Windows user. Do not enable shell mode until the normal preview works on your PC.

SAFE ORDER
1. Extract the whole archive to a normal folder.
2. Double-click Start-TuringDesk.cmd and verify normal mode.
3. Run Preview-TuringDeskShell.ps1 and verify shell mode without changing login settings.
4. Only then double-click Enable-TuringDeskShell.cmd.
5. Sign out and sign in again. TuringDesk ShellHost will start instead of Explorer for this user.

WHAT "REAL SHELL" MEANS
- Explorer.exe is not the login shell for the configured user.
- TuringDesk Desktop becomes the desktop/control surface.
- TuringDesk Shell Bar registers as a Windows AppBar at the bottom of the screen.
- Native apps remain normal top-level Windows windows.
- The Shell Bar lists running windows and can focus them.
- Maximized apps reserve room for the Shell Bar.
- Agent text entry is always available from the Shell Bar.

RECOVERY
Normal recovery:
- Click the "Explorer" button on the right side of the TuringDesk Shell Bar.
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
- The shell policy is current-user only; v0.3 does not overwrite the machine-wide Winlogon Shell value.

WINDOWS INTEGRATION
TuringDesk v0.3 uses the Windows Custom User Interface / CustomShell user policy:
HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\System\Shell

This is intentionally different from the Enterprise-only Shell Launcher feature. The Custom User Interface policy is available on supported Windows 11 Pro, Enterprise, Education and IoT editions.

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
- app.launch remains allow-listed in this developer preview.

KNOWN LIMITATIONS
- v0.3 Shell Bar is primary-monitor-first; multi-monitor taskbars come later.
- Explorer desktop icons/start menu/taskbar are absent while TuringDesk is the shell.
- TuringDesk does not yet reimplement the full Windows notification area, jump lists or Start menu index.
- Full-screen games and unusual exclusive-mode applications still need real-device testing.
- This is unsigned developer software, so SmartScreen may warn.
