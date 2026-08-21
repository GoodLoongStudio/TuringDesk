#pragma once
#include "turingdesk/L3Agent.h"
#include <windows.h>

namespace turingdesk {

// Opens the native L3 model configuration window.
// Existing API keys are represented only as ********; the real credential is
// never read back into UI text and remains in Windows Credential Manager.
bool ShowModelSettingsWindow(HINSTANCE instance, HWND owner, L3Agent& agent);

} // namespace turingdesk
