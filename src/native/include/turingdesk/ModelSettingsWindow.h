#pragma once
#include "turingdesk/L3Agent.h"
#include <windows.h>

namespace turingdesk {

// Opens a small native modal window for the shared L3 model configuration.
// API keys are never read back into the UI; an empty key field keeps the existing credential.
bool ShowModelSettingsWindow(HINSTANCE instance, HWND owner, L3Agent& agent);

} // namespace turingdesk
