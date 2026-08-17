# TuringDesk Pending Issues

Updated: 2026-08-17

This file is the current source of truth for unresolved product issues during rapid source iteration. Formal delivery still goes only to `main`.

## P0 — Scene application is still not visibly replacing the Windows desktop

Current symptom:
- Clicking **Apply to Desktop** changes the selected/current Scene state in TuringDesk, but the real Explorer desktop can still keep showing the original Windows wallpaper.
- Existing structural checks can report a valid Explorer attachment while the rendered Scene is still not visually present.

Next work:
- Add detailed runtime logging for the complete chain: `Apply click -> persisted SceneId -> wallpaper host reload -> Explorer host candidate discovery -> SetParent -> Z-order -> visibility verification -> renderer frame`.
- Write logs to a stable local path under `%LOCALAPPDATA%\TuringDesk\logs\` and keep `desktop-engine-status.json` as the compact state probe.
- Log every WorkerW/Progman candidate with HWND, parent/grandparent class, selected strategy, SetParent result, Win32 last error, rectangle, visibility, Z-order and fallback reason.
- Log SceneRendererControl load/render state so we can distinguish "Explorer host invisible" from "renderer did not paint".
- Do not treat `SetParent` / `Attached=true` as final success.
- Validate on the real target Windows machine. CI cannot prove Explorer desktop visibility.

Acceptance:
- Applying Aurora / Neon / Orbital changes the actual primary desktop within about 1 second.
- Desktop icons, right-click menu and taskbar continue working normally.
- If application fails, one log file must be enough to identify which stage failed.

## P0 — Desktop search must be a clear, usable desktop entry

Current state:
- AI model picker, local application search, text/code file search and AI submission paths exist.
- UI has been iterated several times and still needs target-machine visual verification.

Required behavior:
- Left side clearly communicates **AI model** and current model name.
- Default model selection order: current usable configured model -> usable DeepSeek credential -> explicit no-model state.
- No usable model must show a conspicuous setup hint instead of silently failing.
- Selecting a model must actually configure Runtime/Harness before sending the request.
- Search results must open installed applications and local text/code files directly.
- AI request timeout remains 30 seconds and restores the compact search bar.
- Settings and voice actions must be visually recognizable, not empty/icon-only ambiguous controls.

Acceptance:
- A new user can identify model selector, search field, voice and settings without explanation.
- Changing model changes the model that actually answers.
- Typing an installed app name and pressing Enter launches it.

## P1 — Local desktop search/indexing quality

Current implementation:
- Start Menu application catalog.
- In-memory text/code file index with FileSystemWatcher incremental refresh.

Next work:
- Improve ranking, deduplication and path display.
- Define indexed roots and supported text/code extensions clearly.
- Avoid expensive full rescans.
- Evaluate an NTFS MFT/USN implementation or optional Everything-compatible backend later; do not require Everything to be installed for the basic experience.

Acceptance:
- Common installed apps appear immediately.
- Common user text/code files can be found quickly.
- Index updates after file create/rename/delete without restarting TuringDesk.

## P1 — Settings Center / Harness UI consistency

Current direction:
- Use normal readable Windows-style UI instead of dark custom panels with low contrast.
- Default windows should be compact, centered and constrained to the monitor work area.

Remaining verification:
- Settings Center never extends under the taskbar at 100% / 125% / 150% DPI.
- Harness window follows the same compact work-area-safe behavior.
- No low-contrast black-text-on-dark-background controls remain.

## P1 — One-click source verification workflow

Source iteration workflow is fixed as:
1. Pull latest `main`.
2. Double-click repository-root `QUICK-VERIFY.cmd`.

Requirements:
- Script automatically prepares missing Node / pnpm / .NET SDK dependencies in the project-local tools area.
- Git should be auto-discovered where possible; lack of Git must not prevent validation of an already-present checkout.
- Script builds Runtime + Desktop, stops stale processes and launches the latest source build.
- Do not replace this workflow with manual `dotnet run` instructions.

## P2 — Packaging is intentionally deferred

During current rapid iteration:
- Do **not** spend time generating MSI packages unless explicitly requested.
- Normal `main` CI should validate Runtime and Windows Desktop compilation only.
- Target-machine verification uses `QUICK-VERIFY.cmd`.

## Development priority

1. Scene visibility + full diagnostic logging.
2. Confirm/fix desktop search visual clarity on target machine.
3. Confirm actual AI model switching and no-model experience.
4. Improve local application/file search quality.
5. Finish Settings/Harness UI consistency.
6. Resume packaging only when explicitly requested.
