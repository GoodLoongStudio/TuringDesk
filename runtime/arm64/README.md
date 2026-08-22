# TuringDesk ARM64 RuntimeBundle

This directory is the single source of third-party runtime/build dependencies for the Windows ARM64 product.

Normal user deployment and normal native CI must not download Node.js, DeepSeek Harness, Everything, Codex, or the WebView2 SDK from third-party sites. The pinned versions live in `runtime-lock.json`; the one-time `vendor-arm64-runtime.yml` workflow materializes the official payloads into this directory and writes `runtime-manifest.json` with SHA-256 hashes.

Expected vendored payloads:

- `node/` — official portable Node.js ARM64 archive.
- `harness/` — official `@deepseek-ai/dsh` production package plus its complete dependency tree, packaged as one ARM64 runtime zip.
- `everything/` — official Everything ARM64 portable archive and license.
- `codex/` — official Codex ARM64 app-server release archive.
- `webview2-sdk/` — pinned WebView2 headers and ARM64 static loader used by CMake.
- `runtime-manifest.json` — hashes and exact versions consumed by offline deployment.
- `.complete` — marker written only after the vendoring workflow has validated all payloads.

At execution time, Microsoft Edge WebView2 Runtime is treated as a Windows 11 operating-system component. TuringDesk vendors the WebView2 SDK/loader used to compile the three native executables but does not redistribute the full Edge browser runtime in this repository.

`DEPLOY-NATIVE-ARM64.cmd` extracts the vendored Node/Harness/Everything/Codex payloads into `%LOCALAPPDATA%\TuringDesk\NativeTest` without installing system Node or running `npm install` on the user machine.
