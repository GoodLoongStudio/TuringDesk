# TuringDesk Next-Generation Desktop Plan

Status: active mainline plan, 2026-08-23.

## Product target

TuringDesk is a Windows AI desktop rather than a launcher with several unrelated utilities. The three runtime layers and the wallpaper engine must behave as one product:

1. **L1 application discovery** — instant local app matching.
2. **L2 file discovery** — Everything-class filename search without shipping Everything.
3. **L3 agent** — Codex CLI as the preferred action/reasoning runtime, branded to the user as **图灵智能桌面 / Turing Intelligent Desktop**.
4. **Desktop engine** — Wallpaper Engine-class media support, authoring, automation and performance behavior, with AI-assisted wallpaper creation.

## L2: goz migration

### Architecture

`TuringDesk Search UI -> compatibility adapter -> goz.exe -> \\.\pipe\goz-v1 -> gozd Windows service -> NTFS MFT + USN Journal`

The UI remains TuringDesk-owned. goz is an implementation dependency, not a user-facing product surface.

### Delivery requirements

- Pin goz source version in RuntimeBundle.
- Build native ARM64 `goz.exe` and `gozd.exe` in the vendoring workflow.
- Preserve the upstream MIT notice.
- Install `gozd` once as an auto-start LocalSystem service; ordinary TuringDesk UI remains unelevated.
- Keep queries off the Search UI thread.
- Reject stale asynchronous query results.
- CI must install the service on an ARM64 runner, create a real probe file and confirm it is returned through goz.
- Remove Everything binaries, deployment logic, runtime-lock entries and third-party notices.

### Follow-up optimization

The first stable integration invokes the official `goz.exe` client per query so upstream owns named-pipe protocol/security behavior. After real latency telemetry is collected, replace process-per-query with a persistent TuringDesk pipe client only if process startup is material to perceived latency. The adapter boundary makes that change isolated.

## L3: Codex CLI-first agent

### Target architecture

`TuringDesk L3 window -> CodexRuntime -> bundled codex.exe app-server --stdio -> configured Responses provider -> TuringDesk native tools`

Rules:

- Bundle the complete Codex CLI, not only a standalone app-server binary.
- Prefer Codex whenever `CodexRuntime::CanHandle()` succeeds.
- Keep Direct Tool / Direct Model only as compatibility fallback while non-Responses providers still exist.
- Product identity in developer instructions: **图灵智能桌面 (Turing Intelligent Desktop), built into TuringDesk**. The assistant must not present itself to users as a raw Codex CLI shell.
- TuringDesk remains the owner of provider/model/base URL/API key configuration and Windows Credential Manager storage.
- `codex app-server` is an implementation detail; the UI exposes capabilities and runtime health rather than upstream branding.
- Use `workspace-write` sandbox for the desktop workspace and native dynamic tools for privileged/supported desktop operations; never claim an action succeeded before the tool reports success.

### Provider compatibility work

Codex currently needs a Responses-compatible endpoint. Until all configured providers have a Responses bridge, fallback remains necessary. The long-term target is one compatibility gateway so all L3 turns can use Codex CLI without duplicating a local agent loop.

## Desktop engine target

### Runtime media matrix

P0 / production baseline:

- Static image: JPG/JPEG, PNG, BMP, TIFF, WebP; animated GIF where Windows codecs support it.
- Video: MP4/H.264/H.265 where system Media Foundation codecs are available, WMV/M4V/MOV where supported; MKV/WebM capability detected rather than falsely advertised when codecs are unavailable.
- Web: local HTML packages and trusted HTTPS URLs hosted in WebView2.
- Native procedural scenes: current Aurora/Neon/Grid evolve into a versioned scene package format.
- Multi-monitor: span, clone, per-monitor and independent wallpaper assignments.

P1:

- Audio-reactive web/native scene input.
- Shader/effect graph for blur, color grading, parallax, bloom-like post effects and particles.
- Layered 2D scene composition: image/video/web/text/particle layers with transform, opacity, blend and timeline properties.
- Scene parameters exposed to users as sliders/toggles/dropdowns.

P2:

- 3D scene layer based on a maintained graphics runtime, with model/camera/light primitives.
- Optional application wallpaper type only after sandbox/security and lifecycle behavior are defined.

### `.tdwall` wallpaper package

Introduce a versioned TuringDesk package instead of encoding every wallpaper as INI fields:

```
MyWallpaper.tdwall/
  manifest.json
  thumbnail.png
  assets/
  scene.json | index.html | media file
  scripts/        # optional, web scene only
```

Manifest fields include package version, type, title, author, entry point, aspect policy, audio policy, FPS policy, user parameters, required capabilities and provenance (`imported`, `user-authored`, `ai-generated`).

The library imports legacy loose files by wrapping them as package records without forcing users to understand packages.

## Desktop editor

The editor must be task-oriented rather than exposing raw engine internals.

### Core layout

- Left: asset/layer tree.
- Center: live desktop preview with monitor overlays and safe-area guides.
- Right: contextual properties.
- Bottom: timeline for animated properties.
- Top: Undo/Redo, Preview, Save, Apply, Export, AI Create.

### Editing capabilities

P0:

- import/replace source media;
- crop/cover/contain/stretch and focal point;
- per-monitor preview;
- video loop, mute, volume, speed and seek;
- web URL/local package entry and refresh controls;
- duplicate/rename/favorite/export wallpaper;
- undo/redo and autosave draft.

P1:

- layers and z-order;
- transforms, anchors, opacity and masks;
- text layer;
- effects and parameter animation;
- particles;
- audio-reactive bindings;
- reusable templates.

P2:

- 3D layers, camera and lighting;
- advanced shader graph;
- scripting API with permission/capability manifest.

## AI-generated desktop files

AI creation is package generation, not just image generation.

### User flows

- “生成一个蓝紫色极光桌面，鼠标靠近时有扰动” -> procedural/Web `.tdwall`.
- “把这张图做成有景深和粒子的动态桌面” -> layered scene package.
- “根据这段视频做循环无缝壁纸” -> video package + loop metadata and optional trim.
- “给我做一个赛博朋克时钟桌面” -> Web package with generated HTML/CSS/JS and declared parameters.

### Agent tool surface

Codex receives narrow TuringDesk tools rather than arbitrary undocumented file conventions:

- `wallpaper.create_package`
- `wallpaper.inspect_package`
- `wallpaper.add_asset`
- `wallpaper.set_manifest`
- `wallpaper.validate_package`
- `wallpaper.render_thumbnail`
- `wallpaper.preview`
- `wallpaper.install`
- `wallpaper.apply`

Every AI-generated package passes schema validation, asset path containment, web-content security checks and a preview smoke test before it can be applied.

## Performance and lifecycle parity

Required behaviors:

- pause/throttle/stop policy for fullscreen apps, maximized windows, battery saver, remote session, lock screen and idle;
- per-wallpaper FPS cap;
- decode/render recovery without restarting the whole desktop;
- GPU/device-loss recovery;
- monitor hot-plug recovery;
- no taskbar/desktop-icon z-order regressions;
- crash isolation between Web wallpaper processes and the control UI;
- measurable CPU/GPU/RAM budgets and diagnostics.

## Delivery order

### Milestone A — runtime foundations

- [x] Replace Everything query path with goz adapter.
- [ ] Vendor/build goz ARM64 and remove Everything payload.
- [ ] ARM64 real MFT/USN CI probe.
- [ ] Bundle full Codex CLI.
- [ ] Make Codex preferred L3 runtime and set TuringDesk identity.

### Milestone B — wallpaper package foundation

- [ ] `.tdwall` manifest/schema and validator.
- [ ] Unified media capability detector.
- [ ] Import existing Image/Video/Web/Scene entries into package abstraction.
- [ ] Export/import package.

### Milestone C — editor v1

- [ ] Live preview, source replacement, transforms/crop/focal point.
- [ ] Video/Web controls.
- [ ] Undo/redo, autosave and apply workflow.

### Milestone D — AI authoring v1

- [ ] Codex wallpaper package tools.
- [ ] Generate Web/procedural templates from natural language.
- [ ] Validate -> preview -> install -> apply pipeline.

### Milestone E — scene system depth

- [ ] layer compositor;
- [ ] effects/particles;
- [ ] parameter animation/timeline;
- [ ] audio-reactive bindings;
- [ ] 3D scene investigation and prototype.

## Definition of done

The target is reached when a normal user can install TuringDesk, press Alt+Space and get instant app/file search, enter the AI layer and have the TuringDesk agent actually operate supported desktop capabilities, then browse/import/create/edit/apply multimedia wallpapers without needing to know about goz, Codex, Harness, WebView2, MFT/USN or internal package formats.
