# TuringDesk Desktop UX

TuringDesk should feel immediately familiar to a Windows user while making the Agent continuously available.

## Product rule

**80% familiar Windows desktop + 20% agent-native interaction.**

Ordinary mouse, keyboard, app-launch, desktop-file and window habits remain valid. AI augments those habits instead of forcing the user into a full-screen chat product.

## UI ownership rule

TuringDesk and DeepSeek Harness have different UI responsibilities.

### TuringDesk owns the desktop-native experience

- Desktop Surface
- Start Menu
- Taskbar / AppBar
- Desktop DIY / theme and wallpaper
- quick Agent command input
- always-on voice affordance
- Conversation Card
- Execution Trace Card
- Windows-specific permission and recovery UX

### DeepSeek Harness owns the full Agent console

The official Harness WebUI is reused directly and wrapped by WebView2 so it looks like an integrated desktop window instead of a browser tab.

TuringDesk should **not** rebuild the entire Harness WebUI.

The WebView console is optional. Closing it must not disable Harness or remove quick Agent interactions from the desktop.

## Agent presence

The Agent should remain visible through compact desktop surfaces rather than permanently occupying a large panel.

Useful states include:

- starting
- ready / idle
- listening
- understanding
- executing
- waiting for approval
- completed
- failed

Agent actions must remain observable and interruptible as capabilities grow.

## Quick interaction first

The common path should be faster than opening a full control console:

```text
user types or speaks a request
        ↓
TuringDesk quick Agent input
        ↓
Conversation Card appears
        ↓
Execution Trace Card shows product-visible progress
        ↓
result returns to the card / desktop
```

The user opens the full Harness WebView only when they want deeper Harness session/control UI.

## Conversation Card

The Conversation Card is a lightweight desktop surface, not a replacement for the official Harness console.

It should show information such as:

- current user request
- phase/status
- Run ID
- final answer
- surfaced error state

Text should remain selectable/copyable where practical.

## Execution Trace Card

The Execution Trace Card shows product execution events such as:

- Runtime activity
- Harness lifecycle / session state
- MCP/tool invocation events
- Windows capability progress

It must **not** expose private model chain-of-thought.

The purpose is observability: the user should understand what the desktop is doing without requiring a developer console.

## Desktop customization remains a first-class feature

Using the official Harness WebUI does not turn TuringDesk into a web wrapper.

The surrounding desktop remains deeply customizable through TuringDesk-owned UI, including wallpaper, theme, taskbar appearance and Agent floating-card presentation.

Current Desktop DIY concepts include:

- System Fluent
- Deep Space
- Graphite
- follow Windows wallpaper
- custom wallpaper
- solid background
- accent color
- taskbar opacity
- Agent card opacity
- left/right card placement
- completion auto-hide

## Native Windows visual priority

TuringDesk should preserve native application identity whenever Windows already provides it.

Icon rule:

```text
real Windows file/app icon
  -> Windows Stock Icon
  -> TuringDesk vector fallback
```

Do not replace Chrome, VS Code, folders, settings or other recognizable Windows/application icons with generic TuringDesk artwork just for visual uniformity.

TuringDesk-specific concepts such as Agent affordances may use the product's own visual language.

## Familiarity first

- Existing Windows apps remain native windows.
- Direct app launch remains available even if AI services are degraded.
- Desktop files remain real files, not imported cards.
- Start and task surfaces follow Windows interaction expectations.
- Voice and Agent commands are additive paths, not mandatory ones.
- Users should not need prompt syntax for ordinary actions.
- Agent UI should not permanently steal a large part of the workspace.
- Explorer recovery must remain obvious in replacement-shell mode.

## Framework strategy

v0.11 keeps WPF as the desktop shell while Harness, Windows speech, WebView2 and replacement-shell behavior are stabilized.

The UI remains organized around shell/page/service boundaries so a future presentation-layer migration can occur without rewriting the Runtime / MCP contracts.

A future WinUI 3 migration should be treated as a presentation milestone, not mixed into Agent-kernel architecture changes.

## Anti-patterns

Do not evolve TuringDesk into:

- a full-screen chatbot with a few app shortcuts;
- a WebView-only shell around Harness;
- a permanent three-column AI dashboard;
- a permanent wide Agent sidebar that reduces workspace space;
- an invisible automation daemon with no execution feedback;
- a shell that removes familiar Windows interactions before better replacements exist;
- a UI layer that directly owns privileged system policy or unrestricted model execution.
