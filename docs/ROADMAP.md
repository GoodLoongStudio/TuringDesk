# TuringDesk Roadmap

## v0.1 — Desktop bootstrap

- [x] Windows desktop shell UI
- [x] local runtime process boundary
- [x] optional DeepSeek Harness SDK adapter
- [x] launch Chrome / VS Code / Terminal
- [x] discover native top-level windows
- [x] native side-by-side window layout demo
- [x] CI skeleton
- [ ] test on a real Windows 11 machine
- [ ] package a downloadable developer build

## v0.2 — Harness-controlled Windows tools

- [ ] define typed TuringDesk capability protocol
- [ ] expose `app.*` and `window.*` as Harness tools
- [ ] tool approval UI
- [ ] streaming agent activity into Desktop
- [ ] persistent sessions
- [ ] app catalog/index
- [ ] robust process/window identity (not title matching)

Target demo:

> “打开 Chrome 和 VS Code，左右排列，然后在浏览器打开 Cocos 文档。”

The request should be interpreted by the agent and executed through typed tools rather than the v0.1 local demo parser.

## v0.3 — Agentic desktop

- [ ] UI Automation inspection/action tools
- [ ] file search and file actions
- [ ] dynamic workspace cards
- [ ] browser surface/integration
- [ ] task/goal center
- [ ] context awareness for active workspace/windows

## v0.5 — Daily-driver preview

- [ ] permission broker
- [ ] local encrypted credentials
- [ ] crash recovery
- [ ] auto start/login experience
- [ ] multi-monitor workspace management
- [ ] local + cloud model routing
- [ ] signed installer and updater

## v1.0 — AI-native Windows shell

Only after the desktop is stable:

- optional Explorer shell replacement on supported Windows editions
- system-level window/workspace lifecycle
- plugin/skill distribution
- security policy and audit log
- developer SDK for third-party capabilities and AI surfaces
