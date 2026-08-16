# TuringDesk Replacement Shell

TuringDesk v0.11 supports an optional real Windows desktop replacement mode while keeping Explorer recoverable.

## Installation model

v0.11 no longer treats the portable script flow as the normal end-user installer.

The supported product flow is:

```text
TuringDesk-v0.11-win-arm64.msi
    |
    v
Program Files\GoodLoong Studio\TuringDesk
    |
    +-- TuringDesk Desktop
    +-- TuringDesk ShellHost
    +-- bundled Node / Harness runtime
    +-- Enable-TuringDeskShell.cmd/.ps1
    `-- Restore-Explorer.cmd/.ps1
```

Windows Installer owns the application files, Start menu entries, repair, upgrade and uninstall lifecycle.

Installing TuringDesk does **not** silently replace Explorer.

## Explicit Shell activation

Replacement-shell activation remains an explicit user choice after installation.

Start menu entry:

```text
启用 TuringDesk 桌面
```

TuringDesk uses the current-user Windows Custom User Interface policy:

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\System\Shell
```

The value points to the installed `TuringDesk.ShellHost.exe` under the MSI installation directory.

This is a current-user setting. Other Windows users continue using Explorer unless they explicitly enable TuringDesk for their own account.

TuringDesk does **not** overwrite the machine-wide Winlogon Shell value.

## Process model

```text
Windows sign-in
    |
    v
TuringDesk.ShellHost
    |
    v
TuringDesk.Desktop --shell
    |
    +-- DesktopSurfaceWindow
    +-- ShellBarWindow / StartMenuWindow
    +-- Windows Capability Server
    +-- always-on voice
    +-- Quick Agent input
    +-- Conversation / Trace Cards
    |
    `-- ensures bundled DeepSeek Harness
            |
            +-- TuringDesk Windows MCP
            `-- official Harness WebUI :4319
                    |
                    `-- WebView2 console opened only when requested
```

Explorer is not deleted or patched. It is simply not the current user's login shell while TuringDesk replacement-shell mode is enabled.

## ShellHost fail-safe

`TuringDesk.ShellHost.exe` is deliberately separate from the desktop UI process.

If TuringDesk Desktop repeatedly exits too early, ShellHost should avoid a restart loop and restore the user to Explorer.

Recovery state is stored under:

```text
HKCU\Software\TuringDesk\Shell
```

The state includes the previous shell / active installation path needed for recovery.

## Normal recovery

TuringDesk exposes Explorer recovery from its shell UI and Start menu lifecycle entry.

Start menu entry:

```text
恢复 Windows Explorer 桌面
```

The restore script removes/restores the current-user TuringDesk shell policy and starts Explorer when appropriate.

## Uninstall behavior

A real MSI uninstall must not leave the account pointing at a removed executable.

Therefore v0.11 runs the Explorer recovery action before Windows Installer removes the TuringDesk application files.

Major upgrades are treated separately so an in-place upgrade does not unnecessarily bounce the user back to Explorer.

## Emergency recovery

If the TuringDesk desktop is unavailable:

1. Press `Ctrl + Shift + Esc`.
2. In Task Manager choose **Run new task**.
3. Run `powershell`.
4. Execute:

```powershell
& "$env:LOCALAPPDATA\TuringDesk\Restore-Explorer.ps1"
```

5. Sign out/sign in if Windows requires a new shell session.

The recovery script is copied to a stable `%LOCALAPPDATA%\TuringDesk` location so it can remain available even while MSI installation files are being repaired or removed.

## Rollout rule

Do not silently enable replacement shell during a normal MSI install or update.

Recommended test sequence for a new build:

1. install MSI normally,
2. launch TuringDesk as an application,
3. verify Harness / voice / desktop surfaces,
4. explicitly enable TuringDesk desktop,
5. sign out/in,
6. test the real shell session,
7. verify Explorer recovery before treating the build as safe.

## Current limitations

The replacement shell is still under active development. Important remaining compatibility areas include:

- third-party Explorer notification-area / system-tray icons
- Jump Lists
- desktop icon free-position persistence
- richer third-party Explorer context-menu extension hosting
- deeper multi-monitor validation
- full-screen / exclusive-mode game compatibility
- signed installer / SmartScreen experience

These are compatibility gaps, not a reason to give the Agent unrestricted Windows authority.
