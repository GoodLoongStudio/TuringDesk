add_executable(TuringDesk WIN32
    src/main.cpp
    src/SearchWindow.cpp
    src/ModelSettingsWindow.cpp
    src/L3CliWindow.cpp
    src/CodexRuntime.cpp
    src/DirectAgentRuntimeV2.cpp
    src/NativeTools.cpp
    src/AppSearch.cpp
    src/EverythingSearch.cpp
    src/L3Agent.cpp
)

add_executable(TuringDeskWallpaper WIN32
    src/WallpaperEngine.cpp
    src/VideoWallpaperPlayer.cpp
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

if (MSVC)
    target_compile_options(TuringDesk PRIVATE /W4 /permissive- /utf-8 /EHsc)
    target_compile_options(TuringDeskWallpaper PRIVATE /W4 /permissive- /utf-8 /EHsc)
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
    mf
    mfplat
    mfplay
    mfuuid
)
