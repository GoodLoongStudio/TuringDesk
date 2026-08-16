TuringDesk v0.8 Replacement Shell Developer Preview
===================================================

Target: Windows 11 x64 / ARM64
Use the package that matches the Windows architecture:
- TuringDesk-v0.8-win-x64
- TuringDesk-v0.8-win-arm64

PRODUCT DIRECTION
TuringDesk v0.8 advances two pillars together:
1. Deep Windows desktop replacement: TuringDesk owns the desktop surface, taskbar/AppBar, Start, task switching, desktop files, multi-monitor shell state and recovery path.
2. DeepSeek Harness integration: Harness is the reasoning/execution kernel of the desktop, not a floating chat widget. Its execution state is visible in the shell and it acts only through TuringDesk-owned MCP/Capability boundaries.

IMPORTANT
v0.8 can REALLY replace Explorer for the current Windows user. Test inside a VM first. Verify Preview mode and Explorer recovery before enabling the login shell.

SAFE ORDER
1. Extract the whole archive.
2. Run Start-TuringDesk.cmd and verify normal mode.
3. Run Preview-TuringDeskShell.ps1.
4. Test desktop, Start, taskbar, Agent activity, notifications and Restore Explorer.
5. Run Enable-TuringDeskShell.cmd only after recovery works.
6. TuringDesk will ask whether to sign out now. Choose Yes to apply immediately or No to apply at the next sign-out/sign-in.

NEW IN v0.8
- Desktop <-> Control Center switching now uses one explicit ShellViewState instead of ad-hoc Hide/Show calls.
- Rapid repeated switching is protected by transition tokens so stale fade completions cannot overwrite the newest state.
- Lightweight ~100-155ms fades make transitions visually continuous without hiding blocking work.
- Desktop surfaces remain lightweight and stop re-enumerating Desktop contents on every view switch.
- Multi-monitor surface rebuilds preserve whether the user is currently in Desktop or Control Center.
- Secondary desktop surfaces no longer steal focus during normal switching.
- Desktop blank-area context menu keeps familiar Windows-style Refresh / New Folder / Display Settings / Personalization actions.
- Desktop items have right-click Open / Open file location / Properties actions.
- Core taskbar placeholder characters are replaced by semantic scalable vector Shell icons; installed/running apps still prefer their real Windows icons.
- Runtime exposes Agent Run ID plus idle/running/completed/error state and recent execution history.
- The taskbar surfaces live Agent state and opens a dedicated TuringDesk Agent Activity panel.
- A read-only desktop.snapshot capability/MCP tool gives Harness a coherent monitor/window/foreground snapshot before desktop planning.
- Harness persona now explicitly operates as the execution kernel of a replacement desktop shell.
- Enable-TuringDeskShell.ps1 asks whether the user wants to sign out immediately after activation/update.
- Explicit automation flags are available: -Logoff and -NoLogoff.

DESKTOP
- Reads current-user Desktop plus Public Desktop.
- Uses native Windows icons when available.
- Double-click opens targets through normal ShellExecute behavior.
- Current Windows wallpaper is mirrored when readable.
- External FileDrop into the primary desktop is explicit user action and copy-only.
- Desktop content refresh is cached/throttled so switching views does not block on icon enumeration.

START MENU
- Indexes current-user and all-user Start Menu shortcuts.
- Search filters locally.
- Right-click indexed applications to pin/unpin them from TuringDesk taskbars.

TASKBAR / APPBAR
- Start, Show Desktop, TuringDesk Control Center and Task Switcher.
- Persistent pinned application area with drag reorder.
- Pin state/order synchronize across monitor taskbars during the current shell session.
- Native top-level application task buttons remain ordinary Windows windows.
- Network / sound / power status area, TuringDesk notifications, clock and session/power menu.
- Agent input is paired with a live execution-state badge instead of acting as a standalone chat box.

AGENT ACTIVITY
- TuringDesk Runtime tracks each desktop Agent turn with a Run ID.
- Shell-visible phases: idle / running / completed / error.
- Recent prompts and result/error previews are retained for the current Runtime process.
- The Agent Activity panel is desktop state UI; DeepSeek Harness remains the actual execution kernel for real model providers.

HARNESS DESKTOP GROUNDING
- desktop.snapshot is read-only.
- It returns monitor geometry, visible top-level windows and the current foreground window in one coherent snapshot.
- Harness is instructed to use it before multi-window/multi-monitor planning when current state matters.
- Execution still flows Harness -> TuringDesk MCP -> Capability API -> Win32.

TASK SWITCHING
- Windows native Alt+Tab remains untouched.
- Ctrl+Alt+Space opens the TuringDesk Task Switcher when the hotkey can be registered.
- Show Desktop and Control Center switching now preserve shell state across display-topology changes.

SHELL ACTIVATION / SIGN-OUT
Normal interactive activation:
- Run Enable-TuringDeskShell.cmd.
- After installation/policy update, TuringDesk asks whether to sign out now.
- Yes: current Windows user signs out and the replacement shell takes effect at next sign-in.
- No: current session continues; the replacement shell takes effect at a later sign-out/sign-in.

Automation:
- powershell -File Enable-TuringDeskShell.ps1 -Logoff
- powershell -File Enable-TuringDeskShell.ps1 -NoLogoff
TuringDesk never forces sign-out when the user chose No.

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
- The shell policy is current-user only; v0.8 does not overwrite the machine-wide Winlogon Shell value.

MODEL SETUP
- Choose DeepSeek, Ollama, LM Studio, OpenAI-compatible, or Mock.
- API keys are stored in Windows Credential Manager.
- Every real provider is mediated by the bundled DeepSeek Harness kernel.

EMBEDDED AGENT KERNEL
- DeepSeek Harness runtime family: 0.1.0-rc.6.
- TuringDesk loads its own Cordis profile and Windows MCP bridge.
- Harness startup identity is verified before a real model is accepted.
- The Windows capability path remains Harness -> MCP -> Capability API -> Win32.

SAFETY BOUNDARY
- Current-user shell replacement only.
- No machine-wide Winlogon Shell overwrite.
- No driver or service installation.
- No unrestricted PowerShell/Bash capability is exposed to the Agent.
- No file.delete, package install, arbitrary process execution, admin or power capability is exposed to Harness.
- Power/session actions remain explicit user UI actions with confirmation.
- Inbound desktop drops remain explicit user action and copy-only.

KNOWN LIMITATIONS
- Third-party Explorer notification-area/tray icons are not hosted yet.
- Jump Lists are not implemented.
- Desktop icon free-position persistence is not implemented.
- Wallpaper sync is not yet per-monitor slideshow aware.
- Full native Explorer context-menu extension hosting is not implemented yet; v0.8 restores familiar TuringDesk context operations first.
- Win+D registration depends on Windows allowing that reserved hotkey in the active shell session.
- Native icon extraction can be denied for protected/elevated processes.
- Full-screen games and unusual exclusive-mode applications still need VM/real-device testing.
- This is unsigned developer software, so SmartScreen may warn.
