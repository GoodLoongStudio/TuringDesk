include(FetchContent)

set(TURINGDESK_WEBVIEW2_VERSION "1.0.4129.50")

if (CMAKE_VS_PLATFORM_NAME STREQUAL "ARM64")
    # ARM64 release builds are intentionally network-free. The pinned WebView2
    # headers and static loader live inside the repository RuntimeBundle.
    set(WEBVIEW2_SDK_ROOT "${CMAKE_SOURCE_DIR}/runtime/arm64/webview2-sdk")
    set(WEBVIEW2_NATIVE_ARCH "arm64")
else()
    # x64 validation/release builds use the same pinned SDK version. Keeping this
    # path separate avoids weakening the repository-owned ARM64 offline bundle.
    FetchContent_Declare(
        webview2sdk
        URL "https://www.nuget.org/api/v2/package/Microsoft.Web.WebView2/${TURINGDESK_WEBVIEW2_VERSION}"
        DOWNLOAD_EXTRACT_TIMESTAMP TRUE
    )
    FetchContent_MakeAvailable(webview2sdk)
    set(WEBVIEW2_SDK_ROOT "${webview2sdk_SOURCE_DIR}")
    if (CMAKE_VS_PLATFORM_NAME STREQUAL "Win32")
        set(WEBVIEW2_NATIVE_ARCH "x86")
    else()
        set(WEBVIEW2_NATIVE_ARCH "x64")
    endif()
endif()

set(WEBVIEW2_INCLUDE_DIR "${WEBVIEW2_SDK_ROOT}/build/native/include")
set(WEBVIEW2_LOADER_STATIC "${WEBVIEW2_SDK_ROOT}/build/native/${WEBVIEW2_NATIVE_ARCH}/WebView2LoaderStatic.lib")
if (NOT EXISTS "${WEBVIEW2_INCLUDE_DIR}/WebView2.h" OR NOT EXISTS "${WEBVIEW2_LOADER_STATIC}")
    message(FATAL_ERROR
        "WebView2 SDK ${TURINGDESK_WEBVIEW2_VERSION} is incomplete at ${WEBVIEW2_SDK_ROOT} for ${WEBVIEW2_NATIVE_ARCH}.")
endif()

add_executable(TuringDesk WIN32
    src/main.cpp
    src/SearchWindow.cpp
    src/SettingsCenterWindow.cpp
    src/ModelSettingsWindow.cpp
    src/L3CliWindow.cpp
    src/CodexRuntime.cpp
    src/DirectAgentRuntimeV2.cpp
    src/NativeTools.cpp
    src/WallpaperPackage.cpp
    src/AppSearch.cpp
    src/EverythingSearch.cpp
    src/L3Agent.cpp
    src/L3PersistenceSelfTest.cpp
    src/HarnessProcessManager.cpp
    src/HarnessSettingsBridge.cpp
)

add_executable(TuringDeskWallpaper WIN32
    src/WallpaperEntry.cpp
    src/WallpaperEngine.cpp
    src/WallpaperMonitorLayout.cpp
    src/WallpaperMonitorAssignments.cpp
    src/WallpaperIndependentLayout.cpp
    src/IndependentWallpaperHost.cpp
    src/WebWallpaperHost.cpp
    src/WallpaperWebRuntimeCoordinator.cpp
    src/WallpaperScaling.cpp
    src/WallpaperPerformancePolicy.cpp
    src/WallpaperApplicationRules.cpp
    src/WallpaperApplicationRulesWindow.cpp
    src/WallpaperAutomation.cpp
    src/WallpaperAutomationWindow.cpp
    src/WallpaperLibrary.cpp
    src/WallpaperLibraryWeb.cpp
    src/WallpaperLibraryWindow.cpp
    src/WallpaperPackage.cpp
    src/VideoWallpaperPlayer.cpp
    src/VideoWallpaperSet.cpp
)

set_source_files_properties(src/WallpaperEngine.cpp PROPERTIES
    COMPILE_DEFINITIONS "wWinMain=TuringDeskWallpaperMain"
)

add_executable(TuringDeskHarness WIN32
    src/HarnessHost.cpp
    src/HarnessProcessManager.cpp
    src/HarnessSettingsBridge.cpp
    src/HarnessBundledRuntimeBootstrap.cpp
)

target_include_directories(TuringDesk PRIVATE include)
target_compile_features(TuringDesk PRIVATE cxx_std_23)
target_compile_definitions(TuringDesk PRIVATE
    UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN
    WINVER=0x0A00 _WIN32_WINNT=0x0A00
)

target_include_directories(TuringDeskWallpaper PRIVATE include)
target_include_directories(TuringDeskWallpaper SYSTEM PRIVATE "${WEBVIEW2_INCLUDE_DIR}")
target_compile_features(TuringDeskWallpaper PRIVATE cxx_std_23)
target_compile_definitions(TuringDeskWallpaper PRIVATE
    UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN
    WINVER=0x0A00 _WIN32_WINNT=0x0A00
)

target_include_directories(TuringDeskHarness PRIVATE include)
target_include_directories(TuringDeskHarness SYSTEM PRIVATE "${WEBVIEW2_INCLUDE_DIR}")
target_compile_features(TuringDeskHarness PRIVATE cxx_std_23)
target_compile_definitions(TuringDeskHarness PRIVATE
    UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN
    WINVER=0x0A00 _WIN32_WINNT=0x0A00
)

if (MSVC)
    target_compile_options(TuringDesk PRIVATE /W4 /permissive- /utf-8 /EHsc)
    target_compile_options(TuringDeskWallpaper PRIVATE /W4 /permissive- /utf-8 /EHsc)
    target_compile_options(TuringDeskHarness PRIVATE /W4 /permissive- /utf-8 /EHsc)
endif()

target_link_libraries(TuringDesk PRIVATE
    user32
    gdi32
    shell32
    ole32
    oleaut32
    advapi32
    comctl32
    d2d1
    dwrite
    dwmapi
    winhttp
)

target_link_libraries(TuringDeskWallpaper PRIVATE
    user32
    gdi32
    shell32
    ole32
    d2d1
    windowscodecs
    comdlg32
    wtsapi32
    mf
    mfplat
    mfplay
    mfuuid
    shlwapi
    version
    "${WEBVIEW2_LOADER_STATIC}"
)

target_link_libraries(TuringDeskHarness PRIVATE
    user32
    gdi32
    shell32
    ole32
    advapi32
    winhttp
    shlwapi
    version
    "${WEBVIEW2_LOADER_STATIC}"
)
