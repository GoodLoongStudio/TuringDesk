#pragma once
#include "turingdesk/L3Agent.h"
#include <windows.h>

namespace turingdesk {

// Opens or activates the modeless TuringDesk settings center.
// The center is the primary entry for desktop/wallpaper settings, AI model
// configuration, and the independent DeepSeek Harness L4 workspace.
bool ShowSettingsCenterWindow(HINSTANCE instance, HWND owner, L3Agent& agent);

} // namespace turingdesk
