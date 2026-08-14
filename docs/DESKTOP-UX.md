# TuringDesk Desktop UX

TuringDesk should feel immediately familiar to a Windows user while making the Agent impossible to miss.

## Product rule

**80% familiar desktop + 20% agent-native interaction.**

The user must always be able to use ordinary mouse, keyboard, app-launch and window habits. Agent features enhance those habits instead of replacing them.

## Information architecture

The shell keeps four stable regions:

1. **Navigation rail** — Home, Apps, Files, Tasks, Memory, Settings and quick launch.
2. **Workspace** — normal desktop content, applications, files and task surfaces. It is not a giant chat page.
3. **Persistent Agent rail** — Turing state, voice state, current execution and recent activity.
4. **Agent command bar** — always-available text/voice intent entry, similar in placement and familiarity to desktop search.

## Agent presence

The Agent should always expose its state:

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
- Common navigation stays visible and clickable.
- Voice and Agent commands are additive interaction paths, not mandatory ones.
- TuringDesk should not require users to learn prompt syntax.

## Agent-native behavior

The Agent may coordinate Apps, Files, Windows, Tasks and future Workspaces through structured capabilities. UI can surface Agent suggestions and task state, but the execution authority remains in the capability/policy layer rather than view code.

## Reference boundary

Public Windows desktop applications such as Lively may be studied as product and architecture references. TuringDesk does not copy their source code, XAML, assets, icons, exact visual resources or implementation details. The TuringDesk shell and design system are implemented independently.

## Anti-patterns

Do not evolve TuringDesk into:

- a full-screen chatbot with a few app shortcuts;
- an invisible automation daemon that gives no execution feedback;
- a shell that removes familiar Windows interactions before Agent alternatives are clearly better;
- UI code that directly implements model reasoning or privileged system policy.
