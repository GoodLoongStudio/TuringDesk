# DeepSeek Harness + TuringDesk Windows MCP

TuringDesk v0.2 embeds DeepSeek Harness as its Agent Kernel. Users do not install Harness, provide a Harness command, or edit Cordis configuration.

```text
TuringDesk Desktop
      |
      v
TuringDesk Runtime :4317
      |
      | supervises bundled JSON-RPC runtime
      v
DeepSeek Harness (`dsh-jsonrpc-agent`)
      |
      | TuringDesk-owned Cordis profile
      v
@deepseek-ai/dsh-mcp-client (stdio)
      |
      v
runtime/app/windows-mcp-server.js
      |
      | HTTP loopback only
      v
TuringDesk Capability Server :4318
      |
      v
Win32 / Windows apps
```

## Embedded runtime

TuringDesk pins the published DeepSeek Harness runtime family to `0.1.0-rc.6` for v0.2. The integration profile is owned by TuringDesk at:

```text
runtime/harness/turingdesk.cordis.yml
```

At runtime TuringDesk automatically:

1. Locates the bundled `dsh-jsonrpc-agent` entry.
2. Starts it with the TuringDesk Cordis profile.
3. Verifies the JSON-RPC server identifies itself as `deepseek-harness-sdk-runtime`.
4. Starts the TuringDesk Windows MCP server through Harness.
5. Routes the selected model through Harness.
6. Supervises the Harness child process and retries bounded restarts after an unexpected exit.

`TURINGDESK_HARNESS_COMMAND` and related variables are retained only as developer overrides; they are not required by the portable product.

## Model routing

Mock mode is the only non-Harness execution mode and exists for safe no-key desktop testing.

Every real model provider is mediated by DeepSeek Harness:

```text
DeepSeek API -----------------> dsh-llm-deepseek -----\
Ollama -----------------------\                        |
LM Studio ---------------------+-> dsh-llm-pi-ai ------+-> Harness Agent
OpenAI-compatible gateway ----/                        |
                                                        v
                                                  TuringDesk MCP
```

The desktop UI remains beginner-friendly: choose a provider, enter a model when needed, paste an API key when required, then Save/Test. Secrets are stored in Windows Credential Manager and are passed to the local Runtime only when the configuration is applied.

## Windows tools

The stable TuringDesk capability names are:

```text
app.launch
window.list
window.find
window.focus
window.move
window.resize
window.tile
```

Harness sees the MCP-qualified names:

```text
mcp__turingdesk__app_launch
mcp__turingdesk__window_list
mcp__turingdesk__window_find
mcp__turingdesk__window_focus
mcp__turingdesk__window_move
mcp__turingdesk__window_resize
mcp__turingdesk__window_tile
```

## Safety boundary

The Harness core is embedded, but TuringDesk intentionally does **not** compose unrestricted Bash, PowerShell, filesystem mutation, install, delete, power, or administrator tools into its v0.2 Agent profile.

- Capability endpoint binds only to `127.0.0.1`.
- `app.launch` is allow-listed to Chrome, VS Code, and Terminal.
- TuringDesk refuses to manage its own HWND.
- Move/resize operations are clamped to the Windows work area.
- No close/delete/install/power/admin capability is exposed yet.
- Harness can act on Windows only through the reviewed TuringDesk MCP capability surface in this version.

## CI acceptance gate

A v0.2 portable package is uploaded only after all of the following pass:

- Runtime install/typecheck/build
- MCP protocol smoke test
- real bundled `dsh-jsonrpc-agent` boot
- TuringDesk Cordis profile load
- Harness JSON-RPC `initialize` identity check
- Windows desktop Release build
- a second Harness boot test from the **final portable directory** using the embedded Windows `node.exe` and packaged `node_modules`
