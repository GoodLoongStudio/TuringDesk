# DeepSeek Harness + TuringDesk Windows MCP

TuringDesk v0.2 keeps Windows control outside DeepSeek Harness and exposes it through MCP.

```text
DeepSeek Harness
      |
      | @deepseek-ai/dsh-mcp-client (stdio)
      v
runtime/dist/windows-mcp-server.js
      |
      | HTTP loopback only
      v
TuringDesk Capability Server :4318
      |
      v
Win32 / Windows apps
```

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

Harness sees the MCP-qualified tool names:

```text
mcp__turingdesk__app_launch
mcp__turingdesk__window_list
mcp__turingdesk__window_find
mcp__turingdesk__window_focus
mcp__turingdesk__window_move
mcp__turingdesk__window_resize
mcp__turingdesk__window_tile
```

## Harness Cordis entry

Build the TuringDesk Runtime first:

```powershell
cd runtime
corepack enable
pnpm install
pnpm build
```

Then add an MCP client instance to the Harness composition that serves the JSON-RPC runtime:

```yaml
- id: turingdesk-windows
  name: '@deepseek-ai/dsh-mcp-client'
  config:
    serverName: turingdesk
    transport: stdio
    command: node
    args:
      - 'C:\\path\\to\\TuringDesk\\runtime\\dist\\windows-mcp-server.js'
    env:
      TURINGDESK_CAPABILITY_URL: 'http://127.0.0.1:4318'
    failOnStartupError: true
```

## v0.2 safety boundary

- The capability endpoint binds only to `127.0.0.1`.
- `app.launch` is allow-listed to Chrome, VS Code, and Terminal.
- TuringDesk refuses to manage its own HWND.
- Move/resize operations are clamped to the Windows work area.
- No close/delete/install/power/admin capability is exposed yet.

## Model entry points

The desktop model settings surface supports:

- Mock mode (no key)
- DeepSeek API
- Ollama local OpenAI-compatible endpoint
- LM Studio local OpenAI-compatible endpoint
- custom OpenAI-compatible endpoints / gateways
- advanced DeepSeek Harness mode

Secrets are stored by the Windows desktop in Windows Credential Manager. The local Runtime receives the active credential only when the user applies the model configuration.
