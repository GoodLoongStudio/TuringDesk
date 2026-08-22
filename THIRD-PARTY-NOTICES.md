# Third-Party Notices

TuringDesk includes, redistributes, or adapts ideas/code from the following third-party components. Windows ARM64 runtime versions are pinned by `runtime/arm64/runtime-lock.json`; applicable upstream license files are retained in the vendored payloads or alongside them.

## DeepSeek Harness

- Project: DeepSeek Harness
- Package: `@deepseek-ai/dsh`
- Source: https://github.com/deepseek-ai/deepseek-harness
- License: MIT
- TuringDesk usage: official upstream package, unmodified at runtime, launched on demand by `TuringDeskHarness.exe` and rendered inside TuringDesk WebView2.

TuringDesk's ARM64 RuntimeBundle contains a pinned production install of the official package plus its complete dependency tree. TuringDesk does not fork or replace the Harness runtime and does not run `npm install`/`npx` on the user machine.

## Node.js

- Project: Node.js
- Source: https://nodejs.org/ / https://github.com/nodejs/node
- Runtime: official portable Windows ARM64 archive pinned by `runtime-lock.json`
- License: Node.js project license plus licenses for bundled third-party components, as shipped in the official archive.

Node is private to the TuringDesk RuntimeBundle. TuringDesk does not install or modify system Node.js.

## goz

- Project: goz
- Source: https://github.com/mustafaahci/goz
- Pinned TuringDesk version: `v0.1.1`
- License: MIT
- TuringDesk usage: L2 instant filename-search backend. `gozd.exe` runs as the LocalSystem Windows service and maintains the NTFS MFT + USN Journal index; the unprivileged `goz.exe` client queries it over its authenticated named pipe. TuringDesk owns the user-facing search UI and ranking.

TuringDesk builds the pinned goz source on a Windows ARM64 GitHub runner, vendors only the resulting `goz.exe`, `gozd.exe` and MIT notice, and performs a real MFT/USN query smoke test in CI. The previous Everything runtime is no longer part of the TuringDesk RuntimeBundle.

## OpenAI Codex

- Project: OpenAI Codex CLI
- Source: https://github.com/openai/codex
- Pinned TuringDesk Agent Runtime: `rust-v0.146.0`
- Component: full Windows ARM64 `codex.exe`
- License: Apache License 2.0
- TuringDesk usage: default L3 agent runtime when the configured provider supports the Responses wire protocol. TuringDesk launches `codex app-server --stdio`, supplies its own provider/model settings and native dynamic tools, and presents the product identity as Turing Intelligent Desktop / TuringDesk.

The official ARM64 Codex CLI release archive is vendored in the TuringDesk RuntimeBundle rather than downloaded on the user machine. Formal distributable packages must retain applicable Apache-2.0 license and notices.

## Microsoft WebView2 SDK

- Project: Microsoft Edge WebView2 SDK
- Package: `Microsoft.Web.WebView2`
- Pinned SDK version: `1.0.4129.50`
- Source: https://www.nuget.org/packages/Microsoft.Web.WebView2
- License/notices: retained from the official NuGet package in `runtime/arm64/webview2-sdk/`.

TuringDesk vendors the SDK headers and ARM64 static loader required to build `TuringDeskWallpaper.exe` and `TuringDeskHarness.exe`. The Microsoft Edge WebView2 Runtime itself is treated as a Windows 11 operating-system component and is not duplicated in this repository.

## Microsoft PowerToys

- Project: Microsoft PowerToys / PowerToys Run Program plugin
- Source: https://github.com/microsoft/PowerToys
- License: MIT
- Copyright: Copyright (c) Microsoft Corporation. All rights reserved.
- TuringDesk usage: the L1 application discovery architecture follows the mature PowerToys pattern of combining classic Windows program shortcuts with packaged-app identities/AUMIDs. TuringDesk keeps its own native implementation rather than embedding PowerToys.

The MIT license permits use, modification and redistribution provided the copyright and permission notice are retained in copies or substantial portions of the software.

## Flow Launcher

- Project: Flow Launcher
- Source: https://github.com/Flow-Launcher/Flow.Launcher
- License: MIT
- Copyright: Copyright (c) 2019 Flow-Launcher; Copyright (c) 2015 Wox
- TuringDesk usage: the L1 in-memory matcher is an independent compact adaptation of Flow Launcher's acronym/fuzzy-search strategy: ordered subsequence matching, contiguous-match bonuses, word-boundary bonuses and early-match weighting. Pinyin aliases remain generated locally by TuringDesk.

The MIT license permits use, modification and redistribution provided the copyright and permission notice are retained in copies or substantial portions of the software.
