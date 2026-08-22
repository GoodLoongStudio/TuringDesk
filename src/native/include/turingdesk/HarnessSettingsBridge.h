#pragma once
#include <windows.h>
#include <string>

namespace turingdesk {

struct HarnessSettingsBridgeState {
    bool configured{};
    bool hasApiKey{};
    std::wstring providerId;
    std::wstring model;
    std::wstring baseUrl;
    std::wstring protocol;
    std::wstring dshHome;
    std::wstring apiKey;
    std::wstring error;
};

HarnessSettingsBridgeState PrepareHarnessSettingsBridge();
bool HarnessSettingsBridgeSelfTest();

} // namespace turingdesk
