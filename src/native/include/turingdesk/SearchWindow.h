#pragma once
#include "turingdesk/AppSearch.h"
#include "turingdesk/EverythingSearch.h"
#include "turingdesk/L3Agent.h"
#include "turingdesk/SearchTypes.h"
#include <windows.h>
#include <CommCtrl.h>
#include <d2d1.h>
#include <dwrite.h>
#include <wrl/client.h>
#include <string>
#include <vector>

namespace turingdesk {

class SearchWindow {
public:
    explicit SearchWindow(HINSTANCE instance);
    ~SearchWindow();

    bool Create();
    void ShowAndFocus();
    int RunMessageLoop();
    bool SelfTest();

private:
    static LRESULT CALLBACK WndProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam);
    static LRESULT CALLBACK EditProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam);
    LRESULT HandleMessage(UINT message, WPARAM wParam, LPARAM lParam);
    void OnQueryChanged();
    void MergeResults();
    void ExecuteSelected(bool forceL3);
    void StartL3(const std::wstring& prompt);
    void OpenModelSettings();
    void Draw();
    void ResizeRenderTarget(UINT width, UINT height);
    void PositionWindow();
    void SetStatus(std::wstring title, std::wstring subtitle = {});

    HINSTANCE instance_{};
    HWND hwnd_{};
    HWND edit_{};
    HWND settingsButton_{};
    WNDPROC oldEditProc_{};
    AppSearch apps_;
    EverythingSearch files_;
    L3Agent l3_;
    std::vector<SearchResult> appResults_;
    std::vector<SearchResult> fileResults_;
    std::vector<SearchResult> results_;
    int selected_{-1};
    bool fileSearchAvailable_{false};
    bool fileSearchQueryFailed_{false};
    std::wstring currentQuery_;
    std::wstring streamingText_;
    std::wstring lastL3Prompt_;

    Microsoft::WRL::ComPtr<ID2D1Factory> d2dFactory_;
    Microsoft::WRL::ComPtr<ID2D1HwndRenderTarget> renderTarget_;
    Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> textBrush_;
    Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> secondaryBrush_;
    Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> selectionBrush_;
    Microsoft::WRL::ComPtr<IDWriteFactory> writeFactory_;
    Microsoft::WRL::ComPtr<IDWriteTextFormat> titleFormat_;
    Microsoft::WRL::ComPtr<IDWriteTextFormat> subtitleFormat_;
};

} // namespace turingdesk
