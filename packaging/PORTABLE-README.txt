TuringDesk v0.2 Portable Developer Preview
==========================================

Target: Windows 11 x64

HOW TO RUN
1. Extract the whole archive to a normal folder.
2. Double-click Start-TuringDesk.cmd.
3. Close the TuringDesk window when finished. The bundled local Runtime is stopped automatically.

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
- Deleting this folder removes the portable preview. Model metadata remains in LocalAppData and the API key remains in Windows Credential Manager until changed/cleared through the model settings UI.

WHAT IT CAN DO IN v0.2
- Run the TuringDesk desktop shell.
- Start a loopback-only Windows Capability Server on 127.0.0.1:4318.
- Launch Chrome, VS Code and Windows Terminal when available.
- List/find/focus/move/resize/tile ordinary top-level Windows windows.
- Route no-key desktop commands through Runtime -> Capability API -> Win32.
- Provide a TuringDesk Windows MCP Server for DeepSeek Harness.
- Use a configured DeepSeek/OpenAI-compatible/local model for normal chat.

TRY THIS
- 图灵桌面，打开 Chrome
- 图灵桌面 → 打开 Chrome 和 VS Code，左右排列
- 列出窗口
- 模型 → DeepSeek → 粘贴 API Key → 测试连接

KNOWN LIMITATIONS
- This is an unsigned developer preview. Windows SmartScreen may show a warning.
- Full DeepSeek Harness + MCP mode still requires a configured Harness Runtime command.
- Speech quality/language availability depends on the Windows speech recognizers installed on the machine.
- Interactive microphone and live Harness behavior still need validation on a normal Windows 11 desktop session.

If anything behaves unexpectedly, close TuringDesk. Explorer and the normal Windows desktop remain independent in v0.2.
