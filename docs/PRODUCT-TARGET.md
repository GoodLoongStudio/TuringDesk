# TuringDesk Product Target

## Product definition

TuringDesk is a Windows desktop engine with two equal pillars:

1. **Wallpaper Engine-class desktop engine** — visual scene playback, authoring, profiles, rules, performance controls and multi-monitor behavior.
2. **DeepSeek Harness-powered AI desktop** — an always-available agent layer whose advanced concepts are hidden behind beginner-friendly flows.

The default product must enhance Explorer rather than replace it. Replacement Shell remains an advanced mode.

## Non-negotiable capability matrix

### A. Wallpaper playback engine

TuringDesk must support these wallpaper/project types:

- Image / animated image
- Video
- Web (HTML/CSS/JS)
- Scene (layers, effects, particles, timeline, audio response, parallax, shaders, script)

A project is described by `scene.json` and a package directory. Renderer implementations are pluggable. The public scene model must not depend on WPF.

### B. Scene authoring

The editor must eventually expose:

- Layer hierarchy
- Transform / opacity / blend controls
- Image and video layers
- Text and widget layers
- Particle systems
- Timeline animation
- Mouse parallax
- Audio-reactive parameters
- Effect/shader stack
- Script hooks
- Preview / pause / FPS controls
- Export/import project package

The first-party editor can start simple, but the scene package format must support the full model from day one.

### C. User properties

Every scene can publish beginner-editable properties:

- color
- slider
- toggle
- dropdown
- text
- media/texture replacement
- shortcut/action
- grouped properties

The Properties panel is generated from metadata. No scene-specific hard-coded settings UI.

### D. Library / playlists

- Installed scene library
- Favorites and tags
- Search
- Import local packages
- Playlists
- Random / ordered rotation
- Time-based rotation
- Per-monitor playlist
- Profile snapshots

### E. Multi-monitor

- Different scene per monitor
- One scene spanning monitors
- Clone/mirror mode
- Independent playlists per monitor
- Save/load monitor profiles
- React to monitor hot-plug and topology changes

### F. Performance behavior

Global rules and per-app rules must support:

- keep running
- mute
- pause
- stop / release renderer resources
- configurable FPS
- battery / power saver behavior
- fullscreen behavior
- focused/maximized application behavior
- per-executable overrides

TuringDesk AI services remain alive when visual renderers are paused or stopped unless the user explicitly disables AI.

### G. Application automation

Rules may trigger on an executable and can:

- load scene
- load playlist
- load multi-monitor profile
- change playback state
- change audio behavior
- change AI desktop profile

### H. Audio / interaction

- Audio-reactive scene input
- Scene audio with mute/volume rules
- Mouse parallax
- Optional scene pointer interaction
- Hotkeys
- Media/session-aware behavior where appropriate

### I. DeepSeek Harness integration

Harness is the agent kernel, not the product UI.

TuringDesk owns:

- one-click provider setup
- API key storage
- model presets
- local model discovery where available
- permission profiles
- AI Orb / global hotkey
- voice entry
- native conversation/result cards
- native execution/approval cards
- Windows capability/MCP boundary
- workspace selection simplified into user concepts such as Desktop, Documents, Current Project
- Harness lifecycle and health
- optional advanced WebUI console

Harness advanced WebUI remains available for power users but is never required for normal use.

### J. Beginner UX contract

A first-time user should only need to understand:

1. Choose AI: DeepSeek / OpenAI-compatible / Ollama / Local
2. Paste API key if required
3. Pick a desktop scene
4. Press Alt+Space or click the AI Orb
5. Say what they want

Permission prompts use product language, not MCP/Harness jargon:

- “Allow TuringDesk to read files in this folder?”
- “Allow TuringDesk to open this app?”
- “Allow this command once / always for this workspace?”

Advanced terms — provider endpoint, MCP server, Cordis plugin, Harness profile — live under Advanced settings.

## Default navigation

- **Desktop** — current scene + monitor layout
- **Library** — installed scenes and playlists
- **Create** — scene editor
- **AI** — simple provider + permissions + Orb/voice
- **Performance** — FPS, fullscreen, battery, app rules
- **Settings** — startup, language, updates, advanced Harness console

## Release gate

Do not call a release “Wallpaper Engine-class” until all of the following are demonstrably usable:

- image/video/web/scene playback
- multi-monitor assignment
- playlists/profiles
- user properties
- per-app rules
- fullscreen pause/stop
- editor with layers/effects/timeline/particle path
- AI Orb with beginner model setup
- Harness tool execution with native permission UX

Build/CI/installer health is necessary but is not a user-facing feature and must not be presented as product completion.
