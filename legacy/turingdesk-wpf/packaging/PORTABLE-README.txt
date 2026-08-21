TuringDesk v0.11 ARM64 Windows Installer Build
=============================================

Target: Windows 11 ARM64
Installer: TuringDesk-v0.11-win-arm64.msi
Install model: Standard Windows Installer (MSI)
Default install location: Program Files\GoodLoong Studio\TuringDesk

PRODUCT DIRECTION
TuringDesk is a Windows AI Native Desktop with two tightly integrated layers:
1. Deep Windows desktop experience: desktop surface, Start, taskbar/AppBar, window/task handling, desktop files, customization and Explorer recovery.
2. DeepSeek Harness Agent kernel: always available while TuringDesk is running, with Windows actions constrained through TuringDesk MCP / Capability boundaries.

V0.11 INSTALL FLOW
1. Run TuringDesk-v0.11-win-arm64.msi.
2. Windows Installer installs TuringDesk under Program Files and creates Start menu entries.
3. Launch TuringDesk normally first to validate the desktop on the target PC.
4. TuringDesk automatically starts/ensures the bundled DeepSeek Harness service in the background.
5. If you want TuringDesk to become the current user's replacement desktop, explicitly choose:
   启用 TuringDesk 桌面
6. Sign out/sign in to enter the replacement-shell session.
7. To return to normal Explorer, choose:
   恢复 Windows Explorer 桌面

Installing the MSI does NOT silently replace Explorer.

WINDOWS INSTALLER LIFECYCLE
- Application files are owned by Windows Installer.
- Standard Program Files installation.
- Start menu integration.
- Repair / upgrade / uninstall lifecycle.
- Real uninstall restores Explorer before removing installed shell files.
- Replacement-shell activation remains an explicit current-user action.

NEW IN v0.11
- Real multi-size TuringDesk Windows Application Icon embedded in TuringDesk.Desktop.exe.
- Real multi-size TuringDesk Windows Application Icon embedded in TuringDesk.ShellHost.exe.
- MSI / Start menu product icon uses the TuringDesk brand icon.
- Windows-native icon resolution is now the preferred visual source.
- Real files/apps use their Windows Shell icons when available.
- Windows system features use Windows Stock Icons when available.
- TuringDesk vector icons are fallback/product-specific visuals instead of replacements for recognizable Windows/application branding.
- DeepSeek Harness runtime family 0.1.0-rc.6 is bundled with the installed product.
- TuringDesk starts/ensures Harness automatically when the desktop application starts.
- Official DeepSeek Harness WebUI is packaged and displayed through a WebView2 desktop shell.
- Harness WebView is optional UI; closing it does not stop the Agent kernel.
- Quick Agent text/voice interaction remains available without opening Harness WebView.
- Conversation Card and Execution Trace Card remain TuringDesk-native desktop surfaces.
- Windows ARM64 CI now builds and verifies the MSI artifact.

DEEPSEEK HARNESS
Harness is the Agent kernel, not the whole TuringDesk UI.

Service lifecycle:
- TuringDesk startup -> ensure Harness is running.
- Harness is expected to remain available while TuringDesk is active.
- Official WebUI is opened only when requested.

Full console:
- TuringDesk launches the official Harness web profile on loopback.
- WebView2 wraps that local UI without normal browser chrome.
- TuringDesk injects the Windows MCP integration patch.

Desktop-native AI UX remains separate:
- quick Agent input
- always-on voice
- Conversation Card
- Execution Trace Card
- Windows-specific desktop status and approvals

AGENT DYNAMIC CARDS
Conversation Card:
- automatically appears when configured Agent-card behavior is enabled and a desktop Agent command starts;
- shows request, phase/status, Run ID and final reply/error;
- remains useful even when the official Harness WebView is closed.

Execution Trace Card:
- appears with the Conversation Card;
- shows product-visible Runtime / Harness / MCP execution events;
- may show tool/runtime trajectory;
- does NOT expose hidden model chain-of-thought.

DESKTOP DIY CENTER
TuringDesk preserves deep desktop customization independently of Harness WebUI.

Current concepts include:
- System Fluent / Deep Space / Graphite presets.
- Windows / custom / solid wallpaper sources.
- Cover / Contain / Stretch wallpaper fitting.
- Accent color.
- Taskbar opacity.
- Agent card enabled state.
- Agent card opacity.
- completion auto-hide timing.
- left/right Agent-card placement.

NATIVE ICON POLICY
Resolution order:
1. Real Windows file/application Shell Icon.
2. Windows Stock Icon for system functions.
3. TuringDesk vector fallback.

TuringDesk Agent/product-specific controls can use TuringDesk artwork.
Known Windows/app identities should remain recognizable.

DESKTOP / TASKBAR
- Current-user + Public Desktop items.
- Native Windows icons when available.
- Windows-style desktop interactions such as open, copy/paste, rename, Recycle Bin and properties.
- User-driven file operations remain separate from Agent permission expansion.
- Start menu indexes normal Windows shortcuts.
- Taskbar/AppBar surfaces native top-level windows and persistent Agent input.
- Desktop theme/wallpaper/Agent cards remain TuringDesk-owned UI.

REPLACEMENT SHELL
TuringDesk uses the current-user Custom User Interface policy:

HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\System\Shell

This does not overwrite the machine-wide Winlogon Shell.
Other users keep Explorer unless they explicitly enable TuringDesk themselves.

NORMAL RECOVERY
Use the TuringDesk session/power/recovery UI or Start menu entry:

恢复 Windows Explorer 桌面

EMERGENCY RECOVERY
1. Press Ctrl+Shift+Esc to open Task Manager.
2. Run a new task: powershell
3. Execute:

   & "$env:LOCALAPPDATA\TuringDesk\Restore-Explorer.ps1"

4. Sign out/sign in if required.

AUTOMATIC FAIL-SAFE
- ShellHost supervises TuringDesk Desktop in replacement-shell mode.
- Repeated early shell exits should restore the current user to Explorer rather than creating a restart loop.
- Recovery state is stored under HKCU\Software\TuringDesk\Shell.
- Explorer is not deleted or patched.

SAFETY BOUNDARY
- Current-user replacement Shell only.
- No machine-wide Winlogon Shell overwrite.
- No driver installation.
- No unrestricted PowerShell/Bash capability exposed to the Agent by default.
- No unrestricted administrator capability exposed to Harness by default.
- Harness operates Windows through TuringDesk-owned MCP / Capability boundaries.
- Desktop recycle/rename/copy operations are explicit user UI actions unless reviewed Agent capabilities are added later.
- Power/session actions remain explicit user actions with confirmation.
- Agent trace cards expose product execution events, not private chain-of-thought.

CI ACCEPTANCE
The v0.11 ARM64 MSI pipeline verifies:
- Runtime install/typecheck/build.
- Windows MCP protocol smoke test.
- bundled DeepSeek Harness startup.
- TuringDesk Harness integration profile.
- final installed-layout Harness Agent smoke test.
- final installed-layout official Harness WebUI smoke test.
- TuringDesk Desktop Release build.
- TuringDesk ShellHost Release build.
- executable Application Icon extraction.
- successful MSI generation.
- non-empty/valid installer artifact upload.

KNOWN LIMITATIONS
- Windows 11 ARM64 is the current formal MSI target.
- x64 formal MSI build is still pending.
- Third-party Explorer notification-area/tray icons are not fully hosted yet.
- Jump Lists are not implemented yet.
- Desktop icon free-position persistence is not implemented yet.
- Third-party Explorer context-menu extension hosting is incomplete.
- Full-screen games and unusual exclusive-mode applications still require more real-device testing.
- The installer is currently unsigned developer software, so SmartScreen may warn.
