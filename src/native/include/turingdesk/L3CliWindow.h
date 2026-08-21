#pragma once
#include "turingdesk/L3Agent.h"
#include <windows.h>
#include <string>

namespace turingdesk {

bool ShowL3CliWindow(HINSTANCE instance, HWND owner, L3Agent& agent, const std::wstring& initialPrompt);

} // namespace turingdesk
