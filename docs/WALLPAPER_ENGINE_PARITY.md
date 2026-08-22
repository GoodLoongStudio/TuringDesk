# TuringDesk Wallpaper Engine parity roadmap

Goal: evolve the native TuringDesk wallpaper subsystem from a capable Windows desktop wallpaper host into a polished Wallpaper Engine-class desktop engine while keeping the Search / AI layers isolated and lightweight.

## Current baseline

Already present on `main`:

- Windows 11 raised-desktop / WorkerW / Progman fallback mounting.
- Three native Direct2D scenes: Aurora Flow, Neon Flow, Quiet Grid.
- Image wallpaper via WIC.
- Video wallpaper via Media Foundation.
- Enable / stop / resume and tray controls.
- Display-change reattachment and mount diagnostics.
- Per-monitor DPI awareness.
- Multi-monitor topology with Span / Clone / Primary-only / Independent layout and negative-coordinate support.
- Stable monitor identity using DisplayConfig target device paths, with GDI fallback.
- Independent per-monitor Scene/image/video assignments persisted across reconnect and reorder.
- Image Cover / Contain / Stretch / Center / Tile with focal alignment.
- Video Cover / Contain / Stretch / Center via MFPlay source crop/aspect policy; video Tile still uses a safe Center fallback pending a bounded implementation.
- Adaptive wallpaper performance policy with configurable FPS, fullscreen/maximized actions, Remote Desktop, battery saver, lock and idle handling.
- Video loop, mute/volume, playback rate, precise seek/restart and bounded recovery synchronized across monitor surfaces.
- Persistent local wallpaper library with import, generated thumbnails, search, favorites, recent history and optional managed copies.
- Persistent Playlist / Schedule / Profile automation with sequential/random rotation, day/time windows and full wallpaper configuration snapshots.

## Delivery order

Each item is completed in `main`, with native self-test/CI coverage added or updated before moving on.

### P0 — desktop engine fundamentals

- [x] 1. Multi-monitor topology and layout engine
  - Detect all active monitors, primary monitor, physical desktop bounds and hot-plug changes.
  - Layout modes: span across displays, clone/fill on every display, primary-only.
  - Correct coordinates for monitors left/above the primary display and mixed DPI.
  - Persist layout selection and expose monitor diagnostics.
  - ARM64/x64 native CI green at `53f2bb67`.

- [ ] 2. Wallpaper scaling and alignment
  - [x] Cover, contain, stretch and center for image/video.
  - [x] Image tile mode.
  - [x] Horizontal/vertical focal alignment and persisted crop semantics.
  - [ ] Bounded video tile mode without unbounded decoder duplication.

- [x] 3. Performance / playback rules
  - [x] User-selectable FPS caps: 15/30/45/60/120.
  - [x] Continue/throttle/pause/stop policy for fullscreen and maximized applications.
  - [x] Remote Desktop, Windows battery saver, session lock/unlock and idle rules.
  - [x] Dynamic render-timer throttling and active-rule diagnostics.
  - [x] ARM64/x64 native CI green at `2a52fff7`.

- [ ] 4. Video playback controls
  - [x] Loop policy, mute/volume, playback rate and restart behavior.
  - [x] Smooth pause/resume without rebuilding the player.
  - [x] Synchronized controls across Clone and Independent monitor surfaces.
  - [x] Precise ±10 second seek controls and timeline diagnostics.
  - [x] Bounded media/device failure recovery: three attempts with cooldown/stability reset.
  - [x] Control-before-play startup prevents transient audio leakage.
  - [ ] Exact hardware/software decoder diagnostics; MFPlay currently reports Windows Media Foundation/EVR system-managed negotiation.
  - [x] ARM64/x64 native CI green through `0c532f38`.

### P1 — Wallpaper Engine-class daily use

- [x] 5. Wallpaper library
  - [x] Import image/video/web/scene wallpapers into `%LOCALAPPDATA%\\TuringDesk\\WallpaperLibrary`.
  - [x] Generate 320×180 Shell/WIC thumbnails for image/video imports when Windows provides a thumbnail.
  - [x] Persistent metadata, search, favorites and recently used history.
  - [x] Non-destructive source references plus optional managed copies.
  - [x] Searchable All/Favorites/Recent native library UI with import, favorite, remove-record and apply actions.
  - [x] Built-in Aurora/Neon/Grid scenes live in the same library model; Web items can be catalogued ahead of the item-9 backend.
  - [x] Library persistence/search/favorite/recent behavior participates in `TuringDeskWallpaper --self-test`.
  - [x] ARM64/x64 native CI green at `93c3f81e`.

- [x] 6. Per-monitor independent wallpaper assignment
  - [x] Different Scene/image/video wallpaper per monitor, including mixed types at the same time.
  - [x] Stable monitor identity persists assignments across reconnect/reorder; saved offline monitors are retained.
  - [x] Missing assignment/library/source/Web-backend cases fall back to the global wallpaper with actionable diagnostics.
  - [x] Native wallpaper library exposes a target-monitor selector; choosing a monitor switches to Independent mode automatically.
  - [x] Fullscreen/maximized/lock/idle performance policy and video seek/restart controls apply to Independent surfaces.
  - [x] Assignment persistence and independent resolution participate in `TuringDeskWallpaper --self-test`.
  - [x] ARM64/x64 native CI green at `0c532f38`.

- [x] 7. Playlists, schedules and profiles
  - [x] Timed sequential/random playlist rotation with persisted cursor and last-rotation state.
  - [x] Time-of-day and day-of-week schedules, including cross-midnight windows.
  - [x] Named Profiles bundle wallpaper, layout, scaling/focal alignment, FPS/performance rules and video settings.
  - [x] Native automation manager supports save/apply/delete Profile, edit/activate/advance Playlist and create/delete Schedule.
  - [x] Main wallpaper runtime evaluates automation once per second and reports the active Schedule/last automation action in diagnostics.
  - [x] Automation persistence/time-window/rotation behavior participates in `TuringDeskWallpaper --self-test`.
  - [x] ARM64/x64 native CI green at `4378a20f`.

- [ ] 8. Application rules
  - [x] Persistent per-executable rule model with enabled state, trigger, action and priority.
  - [x] Trigger modes: foreground, running, fullscreen and maximized.
  - [x] Explicit Continue/Throttle/Pause/Stop rules override generic fullscreen/maximized defaults while system protection remains stronger.
  - [x] Native application-rule manager supports EXE picker, recent-foreground capture, live-match diagnostics and rule CRUD.
  - [ ] Native self-test/CI green for the integrated application-rule runtime and UI.

- [ ] 9. Web wallpaper backend
  - Isolated WebView2 wallpaper process/window.
  - Local HTML packages and trusted remote URLs.
  - Audio policy, navigation restrictions and crash recovery.

### P2 — scene quality and interaction

- [ ] 10. Scene parameter system
  - Strongly typed properties: sliders, toggles, colors, enums and text.
  - Per-scene defaults and persisted overrides.
  - Live preview without restarting the engine.

- [ ] 11. Advanced native scene renderer
  - GPU-backed composition path for richer particles, gradients, blur, glow and post effects.
  - Quality tiers and resolution scaling.
  - Stable device-loss recovery.

- [ ] 12. Mouse/audio reactive wallpapers
  - Optional pointer position/click input routed to wallpapers.
  - System/audio spectrum input with explicit privacy controls.
  - Per-wallpaper interaction toggles.

- [ ] 13. Smooth transitions
  - Cross-fade between wallpapers and playlist entries.
  - Transition duration/easing controls.
  - No desktop flash during engine restart or wallpaper switch.

### P3 — Windows polish and productization

- [ ] 14. Virtual desktop awareness
  - Follow-global or per-virtual-desktop assignment where Windows APIs allow it safely.
  - Robust Explorer restart / session reconnect behavior.

- [ ] 15. HDR / color / high-DPI polish
  - HDR-aware output diagnostics and SDR fallback.
  - Mixed-DPI/mixed-refresh correctness.
  - Color-space and scaling consistency across monitors.

- [ ] 16. Settings Center integration
  - Replace the legacy mini wallpaper dialog with a full Fluent settings/library experience.
  - Monitor preview cards, live wallpaper preview, properties panel and performance controls.

- [ ] 17. Reliability, recovery and diagnostics
  - Persist last-known-good wallpaper state.
  - Recover from Explorer restart, display driver reset, monitor hot-plug and media failure.
  - Structured diagnostics for mount/render/media/performance state.

- [ ] 18. Import/export and compatibility surface
  - Portable TuringDesk wallpaper package format.
  - Metadata/versioning/licensing fields.
  - Import validation and safe extraction.

## Acceptance standard

A roadmap item is not considered complete until:

1. It works on ARM64 Windows 11 through the one-click deployment path.
2. Existing wallpaper scenes/image/video continue to work.
3. Configuration survives restart and relevant Windows state changes.
4. Failure produces an actionable diagnostic instead of silently falling back.
5. Native self-test/CI coverage is updated when the behavior is testable without an interactive desktop.
