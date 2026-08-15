# TuringDesk replacement shell

TuringDesk v0.3 introduces an optional real Windows desktop replacement mode.

## Windows integration

The first supported path uses the Windows **Custom User Interface / CustomShell** user policy rather than overwriting the machine-wide Winlogon shell.

Policy value:

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\System\Shell
```

The configured shell points to the stable installed copy:

```text
%LOCALAPPDATA%\TuringDesk\Shell\shellhost\TuringDesk.ShellHost.exe
```

This is a current-user setting. Other Windows users keep Explorer unless configured separately.

## Process model

```text
Windows sign-in
    |
    v
TuringDesk.ShellHost
    |-- embedded Node runtime
    |     |
    |     v
    |  DeepSeek Harness
    |
    v
TuringDesk.Desktop --shell
    |
    |-- desktop/control surface
    |-- capability server
    |-- always-on voice
    `-- ShellBarWindow (Windows AppBar)
            |
            |-- running native windows
            |-- Agent command box
            |-- clock
            `-- Explorer recovery
```

Explorer is not deleted or modified. It simply is not the login shell while TuringDesk CustomShell is enabled.

## ShellHost fail-safe

ShellHost is deliberately separate from the UI process.

If TuringDesk Desktop repeatedly exits soon after launch, ShellHost:

1. restores the previous current-user CustomShell policy (or removes the TuringDesk value when there was no previous custom shell),
2. starts `explorer.exe`,
3. exits instead of creating a shell restart loop.

The previous shell and enabled state are saved under:

```text
HKCU\Software\TuringDesk\Shell
```

## Shell Bar

The v0.3 Shell Bar registers with Windows using the AppBar API and reserves the bottom of the primary monitor. Ordinary maximized Win32 windows therefore have a taskbar-like work area.

The first implementation includes:

- TuringDesk home button,
- application entry,
- running top-level window buttons,
- Agent text command box,
- clock/date,
- one-click Explorer restoration.

## Rollout rule

Replacement shell activation must always be explicit.

Recommended order:

1. normal portable mode,
2. shell preview mode,
3. explicit CustomShell enable,
4. sign out/in,
5. real shell session.

Do not silently enable CustomShell during normal app installation or update.

## Recovery

Normal: use the Explorer button in the Shell Bar.

Emergency: open Task Manager with `Ctrl+Shift+Esc`, run PowerShell, then execute:

```powershell
& "$env:LOCALAPPDATA\TuringDesk\Shell\Restore-Explorer.ps1"
```

## Current limitations

- primary-monitor-first AppBar,
- no full Start menu replacement yet,
- no notification area/system tray replication yet,
- no desktop icon/file surface yet,
- no jump lists yet,
- no multi-monitor Shell Bar lifecycle yet.

These are v0.4+ shell features, not reasons to bring Explorer back into the TuringDesk shell session.
