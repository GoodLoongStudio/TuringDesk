# TuringDesk ARM64 RuntimeBundle

This directory is the repository-vendored, network-free runtime used by formal Windows ARM64 builds and local deployment.

The bundle is generated from `runtime-lock.json` by `.github/workflows/vendor-arm64-runtime.yml`. Normal CMake builds must not download third-party dependencies.

Runtime components:

- `node/` — official portable Node.js ARM64 archive.
- `harness/` — pinned production tree of `@deepseek-ai/dsh`.
- `goz/` — TuringDesk-built Windows ARM64 `goz.exe` + `gozd.exe` from pinned MIT source. `gozd` is the L2 MFT/USN index service.
- `codex/` — official full OpenAI Codex CLI ARM64 release. L3 launches `codex app-server --stdio`.
- `webview2-sdk/` — pinned WebView2 headers and ARM64 static loader used at build time.

Everything is intentionally not part of RuntimeBundle v2.

`runtime-manifest.json` contains SHA-256 hashes for every vendored artifact. `scripts/verify-arm64-runtime-bundle.ps1` rejects stale, missing, corrupted, or legacy bundles before native compilation.
