# TuringDesk Roadmap

## 已完成基础阶段

### v0.1 — Desktop bootstrap

- [x] Windows desktop shell UI
- [x] local runtime process boundary
- [x] DeepSeek Harness integration path
- [x] native Windows app/window discovery
- [x] basic app launch and window layout
- [x] CI build pipeline

### v0.2 — Capability bus / voice / model onboarding

- [x] stable TuringDesk Capability boundary
- [x] loopback-only Windows Capability Server
- [x] TuringDesk Windows MCP bridge
- [x] Windows speech recognition / wake flow
- [x] beginner-oriented model connection path
- [x] Windows Credential Manager secret storage
- [x] MCP / Harness smoke tests

### v0.3–v0.10 — Replacement shell and desktop productization

- [x] optional current-user replacement Shell
- [x] resilient `TuringDesk.ShellHost`
- [x] Explorer recovery path
- [x] Desktop Surface
- [x] Start Menu
- [x] Windows AppBar-based taskbar
- [x] desktop file / shortcut indexing
- [x] practical Windows-style desktop context operations
- [x] persistent Agent command entry
- [x] Conversation Card
- [x] Execution Trace Card
- [x] Desktop DIY Center
- [x] wallpaper / theme / taskbar / Agent-card customization
- [x] multi-monitor shell/taskbar groundwork
- [x] persistent taskbar pinning groundwork
- [x] semantic TuringDesk vector icon fallback system

## v0.11 — Standard installer + native-first icons + official Harness WebUI

- [x] Windows ARM64 MSI installer
- [x] Program Files installation ownership
- [x] Start menu / repair / upgrade / uninstall lifecycle
- [x] restore Explorer before real uninstall
- [x] real multi-size TuringDesk Application Icon embedded into Desktop EXE
- [x] real multi-size TuringDesk Application Icon embedded into ShellHost EXE
- [x] installer / Start menu product icon
- [x] Windows native Shell Icon first for real apps/files
- [x] Windows Stock Icon fallback for Windows system functions
- [x] TuringDesk vector icon only when native icon is unavailable
- [x] bundle DeepSeek Harness runtime family `0.1.0-rc.6`
- [x] official Harness WebUI packaged and smoke-tested from final install layout
- [x] WebView2 wrapper for official Harness WebUI
- [x] TuringDesk launch automatically starts/ensures Harness in background
- [x] Harness WebView remains optional UI rather than service lifecycle owner
- [x] Quick Agent interactions remain available without opening the Harness WebView
- [x] Conversation / Execution Trace Cards remain TuringDesk-native
- [x] ARM64 CI generates a verified MSI artifact

Current release target:

```text
TuringDesk-v0.11-win-arm64.msi
```

## v0.12 — Usability and reliability pass

Priority: **make v0.11 comfortable to use repeatedly on a real Windows machine**.

- [ ] real-device Windows 11 ARM64 install / upgrade / uninstall matrix
- [ ] real replacement-shell sign-in / recovery soak testing
- [ ] Harness automatic restart / health-state UX hardening
- [ ] faster cold start and smaller installation footprint
- [ ] reduce duplicated Runtime/Harness packaging work in CI
- [ ] quick Agent interaction UX polish
- [ ] better Conversation / Trace Card streaming updates
- [ ] explicit stop/cancel affordance for active Agent runs
- [ ] approval UI for capabilities that require user confirmation
- [ ] desktop / taskbar animation and Fluent polish
- [ ] more complete native icon resolution for special Windows shell objects
- [ ] keyboard/global shortcut pass

## v0.13 — Windows compatibility pass

- [ ] notification-area / system-tray compatibility layer
- [ ] Jump Lists
- [ ] desktop free-position persistence
- [ ] richer Explorer context-menu extension compatibility
- [ ] multi-monitor edge cases and independent monitor state
- [ ] DPI / work-area / display hot-plug hardening
- [ ] full-screen / borderless / exclusive-mode game compatibility tests
- [ ] sleep / resume / Fast User Switching validation

## v0.14 — Agent capability expansion

Capabilities should grow behind policy rather than by exposing a generic shell.

- [ ] robust app/process/window identity
- [ ] Windows UI Automation inspection/action tools
- [ ] safe file search and reviewed file actions
- [ ] browser DOM integration
- [ ] workspace context awareness
- [ ] task/goal center
- [ ] recoverable Agent sessions
- [ ] structured audit/event history
- [ ] permission scopes per capability / plugin

## v0.15 — Distribution quality

- [ ] Windows x64 MSI build
- [ ] code signing
- [ ] SmartScreen / reputation-friendly distribution flow
- [ ] installer upgrade migration tests
- [ ] automatic updater strategy
- [ ] settings migration/versioning
- [ ] crash reporting / diagnostics export
- [ ] release channel separation (stable / preview / dev)

## v1.0 — AI-native Windows desktop

v1.0 should only be declared after TuringDesk is stable enough to behave like a real desktop rather than a technical preview.

Target qualities:

- reliable optional replacement-shell experience
- fast recovery path
- persistent Agent kernel
- quick text and voice interaction
- full Harness console available on demand
- observable Agent execution
- strong Windows app compatibility
- safe capability / permission model
- plugin / skill distribution
- developer SDK for third-party capabilities and AI surfaces
- signed installer and updater

The goal is not “put a chatbot on Windows”. The goal is a Windows desktop where AI is a first-class interaction and execution layer without discarding the Windows ecosystem.
