include(FetchContent)

set(TURINGDESK_WEBVIEW2_VERSION "1.0.4129.50" CACHE STRING "Pinned Microsoft.Web.WebView2 SDK version")
set(TURINGDESK_WEBVIEW2_SDK_DIR "" CACHE PATH "Optional pre-downloaded Microsoft.Web.WebView2 NuGet package directory")

if (TURINGDESK_WEBVIEW2_SDK_DIR)
    set(WEBVIEW2_SDK_ROOT "${TURINGDESK_WEBVIEW2_SDK_DIR}")
else()
    FetchContent_Declare(
        webview2sdk
        URL "https://www.nuget.org/api/v2/package/Microsoft.Web.WebView2/${TURINGDESK_WEBVIEW2_VERSION}"
        DOWNLOAD_EXTRACT_TIMESTAMP TRUE
    )
    FetchContent_MakeAvailable(webview2sdk)
    set(WEBVIEW2_SDK_ROOT "${webview2sdk_SOURCE_DIR}")
endif()

if (CMAKE_VS_PLATFORM_NAME STREQUAL "ARM64")
    set(WEBVIEW2_NATIVE_ARCH "arm64")
elseif (CMAKE_VS_PLATFORM_NAME STREQUAL "Win32")
    set(WEBVIEW2_NATIVE_ARCH "x86")
else()
    set(WEBVIEW2_NATIVE_ARCH "x64")
endif()

set(WEBVIEW2_INCLUDE_DIR "${WEBVIEW2_SDK_ROOT}/build/native/include")
set(WEBVIEW2_LOADER_STATIC "${WEBVIEW2_SDK_ROOT}/build/native/${WEBVIEW2_NATIVE_ARCH}/WebView2LoaderStatic.lib")
if (NOT EXISTS "${WEBVIEW2_INCLUDE_DIR}/WebView2.h" OR NOT EXISTS "${WEBVIEW2_LOADER_STATIC}")
    message(FATAL_ERROR "WebView2 SDK ${TURINGDESK_WEBVIEW2_VERSION} is incomplete at ${WEBVIEW2_SDK_ROOT}")
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
    src/AppSearch.cpp
    src/EverythingSearch.cpp
    src/L3Agent.cpp
    src/L3PersistenceSelfTest.cpp
    src/HarnessProcessManager.cpp
)

add_executable(TuringDeskWallpaper WIN32
    src/WallpaperEngine.cpp
    src/WallpaperMonitorLayout.cpp
    src/WallpaperMonitorAssignments.cpp
    src/WallpaperIndependentLayout.cpp
    src/IndependentWallpaperHost.cpp
    src/WallpaperScaling.cpp
    src/WallpaperPerformancePolicy.cpp
    src/WallpaperAutomation.cpp
    src/WallpaperLibrary.cpp
    src/WallpaperLibraryWindow.cpp
    src/VideoWallpaperPlayer.cpp
    src/VideoWallpaperSet.cpp
)

add_executable(TuringDeskHarness WIN32
    src/HarnessHost.cpp
    src/HarnessProcessManager.cpp
)

target_include_directories(TuringDesk PRIVATE include)
target_compile_features(TuringDesk PRIVATE cxx_std_23)
target_compile_definitions(TuringDesk PRIVATE
    UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN
    WINVER=0x0A00 _WIN32_WINNT=0x0A00
)

target_include_directories(TuringDeskWallpaper PRIVATE include)
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
)

target_link_libraries(TuringDeskHarness PRIVATE
    user32
    gdi32
    shell32
    ole32
    winhttp
    shlwapi
    version
    "${WEBVIEW2_LOADER_STATIC}"
)
