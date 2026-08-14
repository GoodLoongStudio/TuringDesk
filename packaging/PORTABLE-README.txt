TuringDesk v0.2 Portable Developer Preview
==========================================

Target: Windows 11 x64

HOW TO RUN
1. Extract the whole archive to a normal folder.
2. Double-click Start-TuringDesk.cmd.
3. Close the TuringDesk window when finished. The bundled local Runtime and its Agent processes are stopped automatically.

VOICE
- TuringDesk starts Windows Desktop Speech recognition when an installed recognizer is available.
- Say “图灵桌面” and then a command, or say the wake phrase and command in one sentence.
- The microphone can be paused/resumed from the top bar or command bar.
- If the required Windows speech language/engine is not installed, keyboard input still works normally.

MODEL SETUP
- Click “模型” in the top bar.
- Choose DeepSeek, Ollama, LM Studio, an OpenAI-compatible endpoint, or Mock.
- For cloud APIs, paste the API key and click Test / Save.
- The active API key is stored in Windows Credential Manager rather than the JSON settings file.
- Local OpenAI-compatible models can normally leave the API key empty.
- DeepSeek Harness is already bundled. There is no Harness command, npm package, profile, or MCP configuration for the user to install manually.
- Every real model provider is routed through the embedded Harness Agent Kernel; Mock is the safe no-key test mode.

EMBEDDED AGENT KERNEL
- DeepSeek Harness runtime family: 0.1.0-rc.6.
- TuringDesk automatically starts the bundled dsh-jsonrpc-agent when a real model is applied.
- TuringDesk automatically loads its own safe Cordis profile and Windows MCP bridge.
- The Runtime verifies Harness identifies itself as deepseek-harness-sdk-runtime before accepting the model configuration.
- Unexpected Harness exits use bounded automatic restart attempts.

SAFETY BOUNDARY
- No installer.
- No administrator privileges requested.
- No Explorer/shell replacement.
- No registry shell changes.
- No driver installation.
- No startup task/service installation.
- The Windows capability endpoint binds only to 127.0.0.1.
- app.launch is allow-listed to Chrome, VS Code and Terminal.
- TuringDesk refuses to target its own window with the window capability API.
- Destructive capabilities such as window.close, file.delete, install and power operations are intentionally not exposed yet.
- The embedded Harness profile does not expose unrestricted Bash/PowerShell/admin execution to the Agent.
- Deleting this folder removes the portable preview. Model metadata remains in LocalAppData and the API key remains in Windows Credential Manager until changed/cleared through the model settings UI.

WHAT IT CAN DO IN v0.2
- Run the TuringDesk desktop shell.
- Start a loopback-only Windows Capability Server on 127.0.0.1:4318.
- Run DeepSeek Harness as the embedded Agent Kernel for real models.
- Automatically expose TuringDesk Windows tools to Harness through MCP.
- Launch Chrome, VS Code and Windows Terminal when available.
- List/find/focus/move/resize/tile ordinary top-level Windows windows.
- Use DeepSeek, Ollama, LM Studio or a custom OpenAI-compatible model through Harness.
- Keep Mock mode available for safe no-key desktop testing.

TRY THIS
- 图灵桌面，打开 Chrome
- 图灵桌面 → 打开 Chrome 和 VS Code，左右排列
- 列出窗口
- 模型 → DeepSeek → 粘贴 API Key → 测试连接

KNOWN LIMITATIONS
- This is an unsigned developer preview. Windows SmartScreen may show a warning.
- Speech quality/language availability depends on the Windows speech recognizers installed on the machine.
- Live model inference still requires the chosen provider/local server to be reachable and correctly configured.
- Interactive microphone quality and real-world Agent behavior should still be exercised on a normal Windows 11 desktop session.

If anything behaves unexpectedly, close TuringDesk. Explorer and the normal Windows desktop remain independent in v0.2.
