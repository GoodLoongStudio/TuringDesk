# TuringDesk Roadmap

## v0.1 — Desktop bootstrap

- [x] Windows desktop shell UI
- [x] local runtime process boundary
- [x] DeepSeek Harness stdio JSON-RPC gateway
- [x] persistent Harness chat session identity
- [x] launch Chrome / VS Code / Terminal
- [x] discover native top-level windows
- [x] native side-by-side window layout demo
- [x] CI builds Runtime + Windows Desktop
- [x] portable Windows x64 developer build

## v0.2 — Capability bus, voice, model onboarding

- [x] stable TuringDesk capability protocol
- [x] loopback-only Windows Capability Server
- [x] `app.launch`
- [x] `window.list/find/focus/move/resize/tile`
- [x] TuringDesk MCP server for DeepSeek Harness
- [x] MCP compatibility smoke test
- [x] remove command parsing from the WPF UI
- [x] Runtime-side desktop intent path for no-key testing
- [x] always-on Windows desktop speech recognition
- [x] wake phrase flow: “图灵桌面” / “Turing Desk”
- [x] visible pause/resume microphone control
- [x] beginner-friendly model settings dialog
- [x] DeepSeek API preset
- [x] Ollama and LM Studio local-model presets
- [x] generic OpenAI-compatible endpoint
- [x] Windows Credential Manager storage for the active API key
- [x] model connection test
- [ ] real Windows 11 microphone validation
- [ ] live DeepSeek Harness + MCP agent turn on Windows

Target demos:

> “图灵桌面，打开 Chrome。”

> “图灵桌面” → “打开 Chrome 和 VS Code，左右排列。”

> Settings → DeepSeek → paste API key → Test → Save.

> Settings → Ollama / LM Studio → fill model ID → Save without an API key.

## v0.3 — Agentic desktop

- [ ] tool approval UI and policy broker
- [ ] stream Harness tool activity into Desktop
- [ ] persistent/recoverable sessions
- [ ] robust app/process/window identity
- [ ] Windows UI Automation inspection/action tools
- [ ] file search and safe file actions
- [ ] dynamic workspace cards
- [ ] browser surface/integration
- [ ] task/goal center
- [ ] context awareness for active workspace/windows
- [ ] optional cloud speech fallback for languages not installed locally

## v0.5 — Daily-driver preview

- [ ] local encrypted settings migration/versioning
- [ ] crash recovery
- [ ] auto start/login experience
- [ ] multi-monitor workspace management
- [ ] local + cloud model routing policies
- [ ] signed MSIX/installer and updater
- [ ] migrate speech backend to `Windows.Media.SpeechRecognition` when packaged identity is available

## v1.0 — AI-native Windows shell

Only after the desktop is stable:

- optional Explorer shell replacement on supported Windows editions
- system-level window/workspace lifecycle
- plugin/skill distribution
- security policy and audit log
- developer SDK for third-party capabilities and AI surfaces
