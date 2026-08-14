# TuringDesk Desktop UX

TuringDesk should feel immediately familiar to a Windows user while making the Agent impossible to miss.

## Product rule

**80% familiar desktop + 20% agent-native interaction.**

The user must always be able to use ordinary mouse, keyboard, app-launch and window habits. Agent features enhance those habits instead of replacing them.

## Shell information architecture

TuringDesk follows the interaction grammar of a mature Windows desktop application rather than an AI dashboard.

The shell has five stable surfaces:

1. **Compact custom title bar** — product identity, Runtime/model status, native window controls and the primary Agent text box in the center. The Agent input occupies the same high-frequency visual position that a normal desktop app would use for search.
2. **Top navigation** — Home, Apps, Workspaces, Tasks and Memory. Navigation is shallow and page-oriented instead of using a permanent wide sidebar.
3. **Page host** — ordinary desktop application content. Home and Apps use responsive card/grid layouts; other sections are independent pages/surfaces rather than chat tabs.
4. **Agent Control Panel** — an on-demand right drawer showing Agent state, always-on voice state and execution history. It does not permanently consume workspace width.
5. **Always-on Agent affordances** — the title-bar input, microphone button and compact Agent-state pill remain visible even when the Control Panel is closed.

This gives TuringDesk strong Agent presence without turning every screen into a chat interface.

## Agent presence

The Agent should always expose its state through the compact top-level status and, when expanded, the Agent Control Panel:

- idle / ready
- listening
- understanding
- executing
- waiting for approval
- completed
- failed

Actions must be observable and interruptible as capabilities grow. The Agent should never silently perform destructive or privileged operations.

## Familiarity first

- Existing Windows apps remain native windows.
- Direct app launch remains available even when AI Runtime is offline.
- Top-level navigation behaves like a conventional Windows desktop app.
- App libraries use cards/grids rather than prompt-only discovery.
- Voice and Agent commands are additive interaction paths, not mandatory ones.
- TuringDesk should not require users to learn prompt syntax.
- Agent controls should not permanently steal a large fraction of the workspace.

## Agent-native behavior

The Agent may coordinate Apps, Files, Windows, Tasks and Workspaces through structured capabilities. The title-bar Agent box is global: a request can affect the active page, ordinary Windows windows or background task state without requiring the user to navigate to a dedicated chat page.

UI surfaces show Agent suggestions, execution state and approvals, but execution authority remains in the capability/policy layer rather than view code.

## Clean-room reference boundary

Lively Wallpaper is a useful public reference for the *product grammar* of a polished Windows desktop application: compact title treatment, shallow top navigation, page-based content, card/grid presentation, command surfaces and secondary control panels.

TuringDesk does **not** copy Lively source code, XAML, assets, icons, exact dimensions, exact colors, resource dictionaries or implementation details. TuringDesk implements its own WPF shell and visual system from independently written code. Lively is GPL-3.0, so this boundary must remain explicit.

## Framework strategy

The v0.2 developer preview keeps the proven WPF shell so Harness, SAPI voice and portable distribution stay stable while the product interaction model is validated. The UI is organized around a shell/page/service boundary so a future WinUI 3 shell can replace the WPF presentation layer without changing the Agent Runtime or Windows Capability API.

A WinUI 3 migration should be treated as a separate presentation-layer milestone, not mixed into Agent-kernel changes.

## Anti-patterns

Do not evolve TuringDesk into:

- a full-screen chatbot with a few app shortcuts;
- a permanent three-column AI dashboard;
- a permanent wide Agent sidebar that reduces ordinary workspace space;
- an invisible automation daemon that gives no execution feedback;
- a shell that removes familiar Windows interactions before Agent alternatives are clearly better;
- UI code that directly implements model reasoning or privileged system policy.