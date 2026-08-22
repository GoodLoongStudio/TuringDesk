# TuringDesk Wallpaper Engine parity roadmap

Goal: evolve the native TuringDesk wallpaper subsystem from a capable Windows desktop wallpaper host into a polished Wallpaper Engine-class desktop engine while keeping the Search / AI layers isolated and lightweight.

## Current baseline

Already present on `main`:

- Windows 11 raised-desktop / WorkerW / Progman fallback mounting.
- Three native Direct2D scenes: Aurora Flow, Neon Flow, Quiet Grid.
- Image wallpaper via WIC.
- Video wallpaper via Media Foundation.
- Enable / stop / resume and tray controls.
- Pause dynamic/video wallpaper while a foreground application is fullscreen.
- Display-change reattachment and mount diagnostics.
- Per-monitor DPI awareness.

## Delivery order

Each item is completed in `main`, with native self-test/CI coverage added or updated before moving on.

### P0 — desktop engine fundamentals

- [ ] 1. Multi-monitor topology and layout engine
  - Detect all active monitors, primary monitor, physical desktop bounds and hot-plug changes.
  - Layout modes: span across displays, clone/fill on every display, primary-only.
  - Correct coordinates for monitors left/above the primary display and mixed DPI.
  - Persist layout selection and expose monitor diagnostics.

- [ ] 2. Wallpaper scaling and alignment
  - Cover, contain, stretch, center and tile.
  - Horizontal/vertical focal alignment and crop preview semantics.
  - Apply consistently to image and video backends.

- [ ] 3. Performance / playback rules
  - User-selectable FPS caps (15/30/45/60/120 where meaningful).
  - Pause/stop/throttle rules for fullscreen, maximized windows, Remote Desktop, lock screen and battery saver.
  - Adaptive render throttling when desktop is occluded or idle.

- [ ] 4. Video playback controls
  - Loop policy, mute/volume, playback rate and seek/restart behavior.
  - Hardware-decoding diagnostics and clean device-loss recovery.
  - Smooth pause/resume without restarting the video.

### P1 — Wallpaper Engine-class daily use

- [ ] 5. Wallpaper library
  - Import image/video/web/scene wallpapers into a local library.
  - Thumbnail generation, metadata, search, favorites and recently used.
  - Non-destructive source references plus optional managed copies.

- [ ] 6. Per-monitor independent wallpaper assignment
  - Different wallpaper per monitor.
  - Monitor identity persistence across reconnect/reorder.
  - Graceful fallback when a saved monitor is missing.

- [ ] 7. Playlists, schedules and profiles
  - Timed rotation, random/sequential playlists.
  - Time-of-day and day-of-week schedules.
  - Named profiles that bundle wallpaper + layout + performance settings.

- [ ] 8. Application rules
  - Per-executable pause/stop/throttle rules.
  - Game/fullscreen defaults and user overrides.
  - Rule diagnostics showing which rule is active.

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
