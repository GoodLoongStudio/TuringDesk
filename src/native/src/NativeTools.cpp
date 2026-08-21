#include "turingdesk/NativeTools.h"
#include <windows.h>
#include <shlobj.h>
#include <shellapi.h>
#include <oleauto.h>
#include <algorithm>
#include <cctype>
#include <cwchar>
#include <cwctype>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <sstream>
#include <string>
#include <vector>

namespace fs = std::filesystem;

namespace turingdesk {
namespace {

std::wstring Utf8ToWide(std::string_view value) {
    if (value.empty()) return {};
    const int count = MultiByteToWideChar(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0);
    if (count <= 0) return {};
    std::wstring out(static_cast<std::size_t>(count), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), out.data(), count);
    return out;
}

std::string WideToUtf8(std::wstring_view value) {
    if (value.empty()) return {};
    const int count = WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (count <= 0) return {};
    std::string out(static_cast<std::size_t>(count), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), out.data(), count, nullptr, nullptr);
    return out;
}

int Hex(char ch) {
    if (ch >= '0' && ch <= '9') return ch - '0';
    if (ch >= 'a' && ch <= 'f') return ch - 'a' + 10;
    if (ch >= 'A' && ch <= 'F') return ch - 'A' + 10;
    return -1;
}

void AppendCodepoint(std::string& out, unsigned cp) {
    if (cp <= 0x7f) out.push_back(static_cast<char>(cp));
    else if (cp <= 0x7ff) {
        out.push_back(static_cast<char>(0xc0 | (cp >> 6)));
        out.push_back(static_cast<char>(0x80 | (cp & 0x3f)));
    } else if (cp <= 0xffff) {
        out.push_back(static_cast<char>(0xe0 | (cp >> 12)));
        out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3f)));
        out.push_back(static_cast<char>(0x80 | (cp & 0x3f)));
    } else {
        out.push_back(static_cast<char>(0xf0 | (cp >> 18)));
        out.push_back(static_cast<char>(0x80 | ((cp >> 12) & 0x3f)));
        out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3f)));
        out.push_back(static_cast<char>(0x80 | (cp & 0x3f)));
    }
}

std::string ExtractJsonString(std::string_view json, std::string_view key) {
    auto pos = json.find('"' + std::string(key) + '"');
    if (pos == std::string_view::npos) return {};
    pos = json.find(':', pos + key.size() + 2);
    if (pos == std::string_view::npos) return {};
    ++pos;
    while (pos < json.size() && std::isspace(static_cast<unsigned char>(json[pos]))) ++pos;
    if (pos >= json.size() || json[pos] != '"') return {};
    ++pos;

    std::string out;
    while (pos < json.size()) {
        const char ch = json[pos++];
        if (ch == '"') break;
        if (ch != '\\') { out.push_back(ch); continue; }
        if (pos >= json.size()) break;
        const char esc = json[pos++];
        switch (esc) {
        case '"': out.push_back('"'); break;
        case '\\': out.push_back('\\'); break;
        case '/': out.push_back('/'); break;
        case 'b': out.push_back('\b'); break;
        case 'f': out.push_back('\f'); break;
        case 'n': out.push_back('\n'); break;
        case 'r': out.push_back('\r'); break;
        case 't': out.push_back('\t'); break;
        case 'u': {
            if (pos + 4 > json.size()) break;
            unsigned cp = 0;
            bool valid = true;
            for (int i = 0; i < 4; ++i) {
                const int value = Hex(json[pos + static_cast<std::size_t>(i)]);
                if (value < 0) { valid = false; break; }
                cp = (cp << 4) | static_cast<unsigned>(value);
            }
            if (!valid) break;
            pos += 4;
            if (cp >= 0xd800 && cp <= 0xdbff && pos + 6 <= json.size() && json[pos] == '\\' && json[pos + 1] == 'u') {
                unsigned low = 0;
                bool lowValid = true;
                for (int i = 0; i < 4; ++i) {
                    const int value = Hex(json[pos + 2 + static_cast<std::size_t>(i)]);
                    if (value < 0) { lowValid = false; break; }
                    low = (low << 4) | static_cast<unsigned>(value);
                }
                if (lowValid && low >= 0xdc00 && low <= 0xdfff) {
                    cp = 0x10000 + ((cp - 0xd800) << 10) + (low - 0xdc00);
                    pos += 6;
                }
            }
            AppendCodepoint(out, cp);
            break;
        }
        default: out.push_back(esc); break;
        }
    }
    return out;
}

bool ExtractJsonBool(std::string_view json, std::string_view key, bool fallback) {
    auto pos = json.find('"' + std::string(key) + '"');
    if (pos == std::string_view::npos) return fallback;
    pos = json.find(':', pos + key.size() + 2);
    if (pos == std::string_view::npos) return fallback;
    ++pos;
    while (pos < json.size() && std::isspace(static_cast<unsigned char>(json[pos]))) ++pos;
    if (json.substr(pos, 4) == "true") return true;
    if (json.substr(pos, 5) == "false") return false;
    return fallback;
}

fs::path KnownFolder(REFKNOWNFOLDERID id) {
    PWSTR raw = nullptr;
    if (FAILED(SHGetKnownFolderPath(id, KF_FLAG_DEFAULT, nullptr, &raw)) || !raw) return {};
    fs::path path(raw);
    CoTaskMemFree(raw);
    return path;
}

fs::path ResolveLocation(std::wstring location) {
    std::transform(location.begin(), location.end(), location.begin(), [](wchar_t ch) {
        return static_cast<wchar_t>(std::towlower(ch));
    });
    if (location == L"desktop" || location == L"桌面" || location.empty()) return KnownFolder(FOLDERID_Desktop);
    if (location == L"documents" || location == L"document" || location == L"文档") return KnownFolder(FOLDERID_Documents);
    if (location == L"downloads" || location == L"download" || location == L"下载") return KnownFolder(FOLDERID_Downloads);
    return {};
}

std::wstring SanitizeFileName(std::wstring name, std::wstring fallback) {
    if (name.empty()) name = std::move(fallback);
    constexpr std::wstring_view invalid = L"<>:\"/\\|?*";
    for (auto& ch : name) {
        if (invalid.find(ch) != std::wstring_view::npos || ch < 32) ch = L'_';
    }
    while (!name.empty() && (name.back() == L'.' || name.back() == L' ')) name.pop_back();
    if (name.empty()) name = L"TuringDesk";
    if (name.size() > 120) name.resize(120);
    return name;
}

std::wstring HResultText(HRESULT hr) {
    wchar_t buffer[512]{};
    const DWORD length = FormatMessageW(FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
                                        nullptr, static_cast<DWORD>(hr), 0, buffer,
                                        static_cast<DWORD>(std::size(buffer)), nullptr);
    if (length > 0) {
        std::wstring text(buffer, length);
        while (!text.empty() && (text.back() == L'\r' || text.back() == L'\n')) text.pop_back();
        return text;
    }
    wchar_t code[32]{};
    swprintf_s(code, L"0x%08X", static_cast<unsigned>(hr));
    return code;
}

class ComApartment {
public:
    ComApartment() : hr_(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED)), uninitialize_(SUCCEEDED(hr_)) {}
    ~ComApartment() { if (uninitialize_) CoUninitialize(); }
    HRESULT Result() const { return hr_; }
    bool Ready() const { return SUCCEEDED(hr_) || hr_ == RPC_E_CHANGED_MODE; }
private:
    HRESULT hr_{};
    bool uninitialize_{};
};

class DispatchPtr {
public:
    DispatchPtr() = default;
    explicit DispatchPtr(IDispatch* value) : value_(value) {}
    ~DispatchPtr() { if (value_) value_->Release(); }
    DispatchPtr(const DispatchPtr&) = delete;
    DispatchPtr& operator=(const DispatchPtr&) = delete;
    DispatchPtr(DispatchPtr&& other) noexcept : value_(other.value_) { other.value_ = nullptr; }
    DispatchPtr& operator=(DispatchPtr&& other) noexcept {
        if (this != &other) {
            if (value_) value_->Release();
            value_ = other.value_;
            other.value_ = nullptr;
        }
        return *this;
    }
    IDispatch* get() const { return value_; }
    explicit operator bool() const { return value_ != nullptr; }
private:
    IDispatch* value_{};
};

HRESULT DispId(IDispatch* object, const wchar_t* name, DISPID& id) {
    if (!object) return E_POINTER;
    LPOLESTR mutableName = const_cast<LPOLESTR>(name);
    return object->GetIDsOfNames(IID_NULL, &mutableName, 1, LOCALE_USER_DEFAULT, &id);
}

HRESULT Invoke(IDispatch* object, const wchar_t* name, WORD flags, VARIANT* args, UINT count, VARIANT* result) {
    DISPID id{};
    HRESULT hr = DispId(object, name, id);
    if (FAILED(hr)) return hr;
    DISPPARAMS params{};
    params.rgvarg = args;
    params.cArgs = count;
    DISPID putId = DISPID_PROPERTYPUT;
    if (flags & DISPATCH_PROPERTYPUT) {
        params.rgdispidNamedArgs = &putId;
        params.cNamedArgs = 1;
    }
    EXCEPINFO exception{};
    UINT argError = 0;
    return object->Invoke(id, IID_NULL, LOCALE_USER_DEFAULT, flags, &params, result, &exception, &argError);
}

HRESULT GetDispatch(IDispatch* object, const wchar_t* name, DispatchPtr& out) {
    VARIANT result;
    VariantInit(&result);
    const HRESULT hr = Invoke(object, name, DISPATCH_PROPERTYGET, nullptr, 0, &result);
    if (SUCCEEDED(hr) && result.vt == VT_DISPATCH && result.pdispVal) {
        out = DispatchPtr(result.pdispVal);
        result.vt = VT_EMPTY;
    }
    VariantClear(&result);
    return out ? S_OK : (SUCCEEDED(hr) ? E_NOINTERFACE : hr);
}

HRESULT CallDispatch(IDispatch* object, const wchar_t* name, VARIANT* args, UINT count, DispatchPtr& out) {
    VARIANT result;
    VariantInit(&result);
    const HRESULT hr = Invoke(object, name, DISPATCH_METHOD | DISPATCH_PROPERTYGET, args, count, &result);
    if (SUCCEEDED(hr) && result.vt == VT_DISPATCH && result.pdispVal) {
        out = DispatchPtr(result.pdispVal);
        result.vt = VT_EMPTY;
    }
    VariantClear(&result);
    return out ? S_OK : (SUCCEEDED(hr) ? E_NOINTERFACE : hr);
}

HRESULT PutText(IDispatch* object, const std::wstring& text) {
    VARIANT arg;
    VariantInit(&arg);
    arg.vt = VT_BSTR;
    arg.bstrVal = SysAllocStringLen(text.data(), static_cast<UINT>(text.size()));
    if (!arg.bstrVal && !text.empty()) return E_OUTOFMEMORY;
    const HRESULT hr = Invoke(object, L"Text", DISPATCH_PROPERTYPUT, &arg, 1, nullptr);
    VariantClear(&arg);
    return hr;
}

HRESULT SetShapeText(IDispatch* shape, const std::wstring& text) {
    DispatchPtr frame;
    HRESULT hr = GetDispatch(shape, L"TextFrame", frame);
    if (FAILED(hr)) return hr;
    DispatchPtr range;
    hr = GetDispatch(frame.get(), L"TextRange", range);
    if (FAILED(hr)) return hr;
    return PutText(range.get(), text);
}

HRESULT GetPlaceholder(IDispatch* shapes, long index, DispatchPtr& shape) {
    DispatchPtr placeholders;
    HRESULT hr = GetDispatch(shapes, L"Placeholders", placeholders);
    if (FAILED(hr)) return hr;
    VARIANT arg;
    VariantInit(&arg);
    arg.vt = VT_I4;
    arg.lVal = index;
    return CallDispatch(placeholders.get(), L"Item", &arg, 1, shape);
}

struct SlideContent {
    std::wstring title;
    std::wstring body;
};

std::vector<SlideContent> ParseSlides(std::wstring markdown) {
    markdown.erase(std::remove(markdown.begin(), markdown.end(), L'\r'), markdown.end());
    std::wistringstream stream(markdown);
    std::vector<SlideContent> slides;
    SlideContent current;
    std::wstring line;
    auto flush = [&]() {
        if (current.title.empty() && current.body.empty()) return;
        if (current.title.empty()) current.title = L"内容";
        while (!current.body.empty() && current.body.back() == L'\n') current.body.pop_back();
        slides.push_back(std::move(current));
        current = {};
    };

    while (std::getline(stream, line) && slides.size() < 29) {
        while (!line.empty() && (line.back() == L' ' || line.back() == L'\t')) line.pop_back();
        std::size_t first = 0;
        while (first < line.size() && (line[first] == L' ' || line[first] == L'\t')) ++first;
        line.erase(0, first);
        if (line.empty()) continue;
        if (line[0] == L'#') {
            flush();
            std::size_t pos = 0;
            while (pos < line.size() && line[pos] == L'#') ++pos;
            while (pos < line.size() && line[pos] == L' ') ++pos;
            current.title = line.substr(pos);
            continue;
        }
        if (line.rfind(L"- ", 0) == 0 || line.rfind(L"* ", 0) == 0) line.erase(0, 2);
        if (!current.body.empty()) current.body += L"\r\n";
        current.body += L"• " + line;
        if (current.body.size() > 5000) current.body.resize(5000);
    }
    flush();
    return slides;
}

NativeToolResult CreatePowerPoint(std::string_view arguments) {
    const std::wstring fileNameRaw = Utf8ToWide(ExtractJsonString(arguments, "file_name"));
    const std::wstring title = Utf8ToWide(ExtractJsonString(arguments, "title"));
    const std::wstring subtitle = Utf8ToWide(ExtractJsonString(arguments, "subtitle"));
    const std::wstring outline = Utf8ToWide(ExtractJsonString(arguments, "slides_markdown"));
    const bool openAfter = ExtractJsonBool(arguments, "open_after_create", true);

    auto desktop = KnownFolder(FOLDERID_Desktop);
    if (desktop.empty()) return {false, L"无法定位桌面目录。"};
    std::wstring fileName = SanitizeFileName(fileNameRaw, title.empty() ? L"TuringDesk演示.pptx" : title + L".pptx");
    if (fileName.size() < 5 || _wcsicmp(fileName.c_str() + fileName.size() - 5, L".pptx") != 0) fileName += L".pptx";
    const auto output = desktop / fileName;

    ComApartment apartment;
    if (!apartment.Ready()) return {false, L"初始化 Office COM 失败：" + HResultText(apartment.Result())};

    CLSID clsid{};
    HRESULT hr = CLSIDFromProgID(L"PowerPoint.Application", &clsid);
    if (FAILED(hr)) {
        return {false, L"没有检测到 Microsoft PowerPoint。当前 ppt.create V1 需要本机安装 PowerPoint。"};
    }

    IDispatch* rawApp = nullptr;
    hr = CoCreateInstance(clsid, nullptr, CLSCTX_LOCAL_SERVER, IID_IDispatch, reinterpret_cast<void**>(&rawApp));
    DispatchPtr app(rawApp);
    if (FAILED(hr) || !app) {
        return {false, L"启动 PowerPoint 失败：" + HResultText(hr)};
    }

    DispatchPtr presentations;
    DispatchPtr presentation;
    hr = GetDispatch(app.get(), L"Presentations", presentations);
    if (SUCCEEDED(hr)) hr = CallDispatch(presentations.get(), L"Add", nullptr, 0, presentation);
    if (FAILED(hr)) {
        Invoke(app.get(), L"Quit", DISPATCH_METHOD, nullptr, 0, nullptr);
        return {false, L"创建 PowerPoint 演示文稿失败：" + HResultText(hr)};
    }

    DispatchPtr slides;
    hr = GetDispatch(presentation.get(), L"Slides", slides);
    long slideIndex = 1;
    auto addSlide = [&](long layout, const std::wstring& slideTitle, const std::wstring& body, bool titleSlide) -> HRESULT {
        VARIANT args[2];
        VariantInit(&args[0]);
        VariantInit(&args[1]);
        args[0].vt = VT_I4;
        args[0].lVal = layout;
        args[1].vt = VT_I4;
        args[1].lVal = slideIndex++;
        DispatchPtr slide;
        HRESULT local = CallDispatch(slides.get(), L"Add", args, 2, slide);
        if (FAILED(local)) return local;
        DispatchPtr shapes;
        local = GetDispatch(slide.get(), L"Shapes", shapes);
        if (FAILED(local)) return local;
        DispatchPtr titleShape;
        local = GetDispatch(shapes.get(), L"Title", titleShape);
        if (SUCCEEDED(local) && titleShape) local = SetShapeText(titleShape.get(), slideTitle);
        if (FAILED(local)) return local;
        if (!body.empty()) {
            DispatchPtr bodyShape;
            local = GetPlaceholder(shapes.get(), 2, bodyShape);
            if (SUCCEEDED(local) && bodyShape) local = SetShapeText(bodyShape.get(), body);
        }
        return local;
    };

    if (SUCCEEDED(hr)) hr = addSlide(1, title.empty() ? L"TuringDesk 演示" : title, subtitle, true);
    if (SUCCEEDED(hr)) {
        for (const auto& slide : ParseSlides(outline)) {
            hr = addSlide(2, slide.title, slide.body, false);
            if (FAILED(hr)) break;
        }
    }

    if (SUCCEEDED(hr)) {
        VARIANT saveArgs[2];
        VariantInit(&saveArgs[0]);
        VariantInit(&saveArgs[1]);
        saveArgs[0].vt = VT_I4;
        saveArgs[0].lVal = 24; // ppSaveAsOpenXMLPresentation
        saveArgs[1].vt = VT_BSTR;
        const auto outputText = output.wstring();
        saveArgs[1].bstrVal = SysAllocStringLen(outputText.data(), static_cast<UINT>(outputText.size()));
        hr = Invoke(presentation.get(), L"SaveAs", DISPATCH_METHOD, saveArgs, 2, nullptr);
        VariantClear(&saveArgs[1]);
    }

    Invoke(app.get(), L"Quit", DISPATCH_METHOD, nullptr, 0, nullptr);

    if (FAILED(hr)) return {false, L"生成 PPT 失败：" + HResultText(hr)};
    if (openAfter) ShellExecuteW(nullptr, L"open", output.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
    return {true, L"PPT 已生成：" + output.wstring()};
}

NativeToolResult CreateFile(std::string_view arguments) {
    const auto location = Utf8ToWide(ExtractJsonString(arguments, "location"));
    auto folder = ResolveLocation(location);
    if (folder.empty()) return {false, L"只允许在 desktop / documents / downloads 创建文件。"};
    std::error_code ec;
    fs::create_directories(folder, ec);
    if (ec) return {false, L"无法访问目标目录。"};

    auto name = SanitizeFileName(Utf8ToWide(ExtractJsonString(arguments, "file_name")), L"TuringDesk.txt");
    const auto content = ExtractJsonString(arguments, "content");
    const auto path = folder / name;
    std::ofstream stream(path, std::ios::binary | std::ios::trunc);
    if (!stream) return {false, L"无法创建文件：" + path.wstring()};
    stream.write(content.data(), static_cast<std::streamsize>(content.size()));
    if (!stream) return {false, L"写入文件失败：" + path.wstring()};
    return {true, L"文件已创建：" + path.wstring()};
}

NativeToolResult ListFolder(std::string_view arguments) {
    auto folder = ResolveLocation(Utf8ToWide(ExtractJsonString(arguments, "location")));
    if (folder.empty()) return {false, L"只允许读取 desktop / documents / downloads。"};
    std::error_code ec;
    if (!fs::exists(folder, ec)) return {false, L"目录不存在：" + folder.wstring()};

    std::wstring output = folder.wstring() + L"\r\n";
    std::size_t count = 0;
    for (fs::directory_iterator it(folder, fs::directory_options::skip_permission_denied, ec), end; it != end && !ec && count < 100; it.increment(ec)) {
        const bool directory = it->is_directory(ec);
        output += directory ? L"[目录] " : L"[文件] ";
        output += it->path().filename().wstring();
        output += L"\r\n";
        ++count;
    }
    if (count == 0) output += L"（空目录）";
    return {true, std::move(output)};
}

NativeToolResult OpenFile(std::string_view arguments) {
    auto folder = ResolveLocation(Utf8ToWide(ExtractJsonString(arguments, "location")));
    if (folder.empty()) return {false, L"只允许从 desktop / documents / downloads 打开文件。"};
    const auto rawName = Utf8ToWide(ExtractJsonString(arguments, "file_name"));
    if (rawName.empty()) return {false, L"缺少 file_name。"};
    const auto name = SanitizeFileName(rawName, L"");
    const auto path = folder / name;
    std::error_code ec;
    if (!fs::exists(path, ec)) return {false, L"文件不存在：" + path.wstring()};
    const auto result = reinterpret_cast<INT_PTR>(ShellExecuteW(nullptr, L"open", path.c_str(), nullptr, nullptr, SW_SHOWNORMAL));
    if (result <= 32) return {false, L"打开失败：" + path.wstring()};
    return {true, L"已打开：" + path.wstring()};
}

} // namespace

std::string NativeToolDefinitionsJson() {
    return R"JSON([
{"type":"function","name":"ppt_create","description":"Create a real .pptx PowerPoint presentation on the Windows desktop. Use this instead of merely writing a presentation outline when the user asks for a PPT or presentation.","inputSchema":{"type":"object","properties":{"file_name":{"type":"string","description":"Output filename, preferably ending in .pptx"},"title":{"type":"string"},"subtitle":{"type":"string"},"slides_markdown":{"type":"string","description":"Content slides. Start each slide with '# Slide title'; following lines are bullet points."},"open_after_create":{"type":"boolean","description":"Open the generated presentation after saving"}},"required":["file_name","title","slides_markdown"],"additionalProperties":false}},
{"type":"function","name":"file_create","description":"Create a UTF-8 text file in one of the user's safe folders.","inputSchema":{"type":"object","properties":{"location":{"type":"string","enum":["desktop","documents","downloads"]},"file_name":{"type":"string"},"content":{"type":"string"}},"required":["location","file_name","content"],"additionalProperties":false}},
{"type":"function","name":"folder_list","description":"List files and folders from Desktop, Documents, or Downloads.","inputSchema":{"type":"object","properties":{"location":{"type":"string","enum":["desktop","documents","downloads"]}},"required":["location"],"additionalProperties":false}},
{"type":"function","name":"file_open","description":"Open an existing file from Desktop, Documents, or Downloads with its registered Windows application. No command-line arguments are allowed.","inputSchema":{"type":"object","properties":{"location":{"type":"string","enum":["desktop","documents","downloads"]},"file_name":{"type":"string"}},"required":["location","file_name"],"additionalProperties":false}}
])JSON";
}

NativeToolResult ExecuteNativeTool(std::string_view toolName, std::string_view argumentsJson) {
    if (toolName == "ppt_create") return CreatePowerPoint(argumentsJson);
    if (toolName == "file_create") return CreateFile(argumentsJson);
    if (toolName == "folder_list") return ListFolder(argumentsJson);
    if (toolName == "file_open") return OpenFile(argumentsJson);
    return {false, L"未知 Native Tool：" + Utf8ToWide(toolName)};
}

} // namespace turingdesk
