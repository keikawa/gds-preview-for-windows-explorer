#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shobjidl.h>
#include <propsys.h>
#include <objbase.h>
#include <fstream>
#include <vector>
#include <cwchar>

static const CLSID CLSID_GdsPreview =
{0x87f8a6bb, 0x6b13, 0x4a41, {0x9d, 0x54, 0xee, 0xb3, 0x9d, 0xbd, 0x1d, 0x6e}};

using DllGetClassObjectFunction = HRESULT (__stdcall*)(REFCLSID, REFIID, void**);
using SetTestThemeFunction = void (__stdcall*)(int);
using RendererStartsFunction = int (__stdcall*)();

static COLORREF SystemBackground() {
    HIGHCONTRASTW contrast{sizeof(contrast)};
    if (SystemParametersInfoW(SPI_GETHIGHCONTRAST, sizeof(contrast), &contrast, 0) &&
        (contrast.dwFlags & HCF_HIGHCONTRASTON)) return GetSysColor(COLOR_WINDOW);
    DWORD light = 1, bytes = sizeof(light);
    if (RegGetValueW(HKEY_CURRENT_USER,
        L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize", L"AppsUseLightTheme",
        RRF_RT_REG_DWORD, nullptr, &light, &bytes) != ERROR_SUCCESS) light = 1;
    return light ? RGB(255, 255, 255) : RGB(24, 27, 32);
}

static BOOL CALLBACK NotifyTheme(HWND window, LPARAM) {
    // Only this test's top-level windows, just like the Windows notification
    // target set. Never send a global broadcast or notify the preview child.
    SendMessageW(window, WM_SETTINGCHANGE, 0, reinterpret_cast<LPARAM>(L"ImmersiveColorSet"));
    return TRUE;
}

static BOOL CALLBACK CountThemeWindows(HWND window, LPARAM count) {
    wchar_t name[128]{};
    GetClassNameW(window, name, ARRAYSIZE(name));
    if (!wcscmp(name, L"GdsPreview.Native.Window.87F8A6BB")) ++*reinterpret_cast<int*>(count);
    return TRUE;
}

static bool SaveWindowBitmap(HWND window, const wchar_t* path, COLORREF background) {
    RECT rectangle{};
    GetClientRect(window, &rectangle);
    const int width = rectangle.right;
    const int height = rectangle.bottom;
    HDC source = GetDC(window);
    HDC memory = CreateCompatibleDC(source);
    HBITMAP bitmap = CreateCompatibleBitmap(source, width, height);
    const auto old_bitmap = SelectObject(memory, bitmap);
    PrintWindow(window, memory, PW_CLIENTONLY);

    BITMAPINFO info{};
    info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    info.bmiHeader.biWidth = width;
    info.bmiHeader.biHeight = height;
    info.bmiHeader.biPlanes = 1;
    info.bmiHeader.biBitCount = 32;
    info.bmiHeader.biCompression = BI_RGB;
    const DWORD image_size = static_cast<DWORD>(width) * height * 4;
    std::vector<BYTE> pixels(image_size);
    GetDIBits(memory, bitmap, 0, height, pixels.data(), &info, DIB_RGB_COLORS);

    BITMAPFILEHEADER file_header{};
    file_header.bfType = 0x4D42;
    file_header.bfOffBits = sizeof(BITMAPFILEHEADER) + sizeof(BITMAPINFOHEADER);
    file_header.bfSize = file_header.bfOffBits + image_size;
    std::ofstream output(path, std::ios::binary);
    output.write(reinterpret_cast<const char*>(&file_header), sizeof(file_header));
    output.write(reinterpret_cast<const char*>(&info.bmiHeader), sizeof(info.bmiHeader));
    output.write(reinterpret_cast<const char*>(pixels.data()), pixels.size());

    SelectObject(memory, old_bitmap);
    DeleteObject(bitmap);
    DeleteDC(memory);
    ReleaseDC(window, source);
    // The smoke window is off-screen: validate captured pixels, not GetDC's
    // screen visibility. The margin must match Windows, not the host's colours.
    return output.good() && pixels.size() >= 3 && pixels[0] == GetBValue(background) &&
        pixels[1] == GetGValue(background) && pixels[2] == GetRValue(background);
}

int wmain(int argument_count, wchar_t** arguments) {
    const bool initial_resize_mode = argument_count >= 2 && wcscmp(arguments[1], L"--initial-resize") == 0;
    const bool resize_mode = argument_count >= 2 && wcscmp(arguments[1], L"--resize") == 0;
    const bool theme_to_light = argument_count >= 2 && wcscmp(arguments[1], L"--theme-change-light") == 0;
    const bool dark_mode = theme_to_light || (argument_count >= 2 && wcscmp(arguments[1], L"--dark") == 0);
    const bool theme_change = theme_to_light || (argument_count >= 2 && wcscmp(arguments[1], L"--theme-change") == 0);
    const bool light_mode = argument_count >= 2 && wcscmp(arguments[1], L"--light") == 0;
    const bool high_contrast = argument_count >= 2 && wcscmp(arguments[1], L"--high-contrast") == 0;
    const bool missing_theme = argument_count >= 2 && wcscmp(arguments[1], L"--missing-theme") == 0;
    const bool forced_theme = dark_mode || theme_change || light_mode || high_contrast || missing_theme;
    const bool option_mode = initial_resize_mode || resize_mode || forced_theme;
    if ((!option_mode && argument_count != 4 && argument_count != 5) ||
        (option_mode && argument_count != 5 && argument_count != 6)) return 2;
    const bool registered_mode = wcscmp(arguments[1], L"--registered") == 0;
    const wchar_t* library_path = option_mode ? arguments[2] : arguments[1];
    const wchar_t* file_path = option_mode ? arguments[3] : arguments[2];
    const wchar_t* output_path = option_mode ? arguments[4] : arguments[3];
    const int wait_index = option_mode ? 5 : 4;
    const DWORD wait_time = argument_count > wait_index ? static_cast<DWORD>(_wtoi(arguments[wait_index])) : 4000;
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    HMODULE library = nullptr;
    IPreviewHandler* preview = nullptr;
    HRESULT result = E_FAIL;
    if (registered_mode) {
        result = CoCreateInstance(CLSID_GdsPreview, nullptr, CLSCTX_LOCAL_SERVER,
            IID_IPreviewHandler, reinterpret_cast<void**>(&preview));
    } else {
        library = LoadLibraryW(library_path);
        if (!library) return 3;
        const auto get_class_object = reinterpret_cast<DllGetClassObjectFunction>(
            GetProcAddress(library, "DllGetClassObject"));
        if (!get_class_object) return 4;
        IClassFactory* factory = nullptr;
        result = get_class_object(CLSID_GdsPreview, IID_IClassFactory,
            reinterpret_cast<void**>(&factory));
        if (FAILED(result)) return 5;
        result = factory->CreateInstance(nullptr, IID_IPreviewHandler,
            reinterpret_cast<void**>(&preview));
        factory->Release();
    }
    if (FAILED(result)) return 6;
    const auto set_test_theme = library ? reinterpret_cast<SetTestThemeFunction>(
        GetProcAddress(library, "GdsPreviewTestSetTheme")) : nullptr;
    const auto renderer_starts = library ? reinterpret_cast<RendererStartsFunction>(
        GetProcAddress(library, "GdsPreviewTestRendererStarts")) : nullptr;
    // Forced modes must exercise simulated OS queries, not force host colours.
    // The production DLL deliberately has no override/export for this.
    if (forced_theme && (!set_test_theme || !renderer_starts)) return 11;
    if (forced_theme) set_test_theme(missing_theme ? -1 : high_contrast ? 2 : dark_mode ? 0 : 1);

    IInitializeWithFile* initialize = nullptr;
    IOleWindow* ole_window = nullptr;
    preview->QueryInterface(IID_IInitializeWithFile, reinterpret_cast<void**>(&initialize));
    preview->QueryInterface(IID_IOleWindow, reinterpret_cast<void**>(&ole_window));
    IPreviewHandlerVisuals* visuals = nullptr;
    if (FAILED(preview->QueryInterface(IID_IPreviewHandlerVisuals, reinterpret_cast<void**>(&visuals)))) return 10;
    COLORREF background = !forced_theme ? SystemBackground() : high_contrast ? RGB(0, 0, 0)
        : dark_mode ? RGB(24, 27, 32) : RGB(255, 255, 255);
    visuals->SetBackgroundColor(background == RGB(255, 255, 255) ? RGB(0, 0, 0) : RGB(255, 255, 255));
    visuals->SetTextColor(background == RGB(255, 255, 255) ? RGB(255, 255, 255) : RGB(0, 0, 0));
    const int parent_width = initial_resize_mode ? 420 : 900;
    const int parent_height = initial_resize_mode ? 780 : 600;
    HWND parent = CreateWindowExW(WS_EX_TOOLWINDOW, L"STATIC", L"Native Preview Smoke",
        WS_OVERLAPPEDWINDOW | WS_VISIBLE, -30000, -30000, parent_width, parent_height,
        nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    RECT rectangle{};
    GetClientRect(parent, &rectangle);
    RECT initial_rectangle = rectangle;
    if (initial_resize_mode) initial_rectangle = RECT{0, 0, 1, 1};
    result = initialize->Initialize(file_path, STGM_READ);
    if (SUCCEEDED(result)) result = preview->SetWindow(parent, &initial_rectangle);
    if (SUCCEEDED(result)) result = preview->DoPreview();
    if (SUCCEEDED(result) && initial_resize_mode) result = preview->SetRect(&rectangle);
    if (FAILED(result)) return 7;
    int listeners = 0;
    if (!registered_mode) {
        EnumThreadWindows(GetCurrentThreadId(), CountThemeWindows, reinterpret_cast<LPARAM>(&listeners));
        if (listeners != 1) return 12;
    }

    const DWORD started = GetTickCount();
    int resize_step = 0;
    bool theme_changed = false;
    while (GetTickCount() - started < wait_time) {
        if (theme_change && GetTickCount() - started >= 1500) {
            if (!theme_changed) set_test_theme(theme_to_light ? 1 : 0);
            theme_changed = true;
            background = theme_to_light ? RGB(255, 255, 255) : RGB(24, 27, 32);
            EnumThreadWindows(GetCurrentThreadId(), NotifyTheme, 0);
            // Repeated notifications must not keep restarting the renderer;
            // unchanged SetRect must not cancel the pending theme update.
            preview->SetRect(&rectangle);
        }
        if (GetTickCount() - started >= 1500) {
            // Conflicting host colours both before and after DoPreview.
            visuals->SetBackgroundColor(background == RGB(255, 255, 255) ? RGB(0, 0, 0) : RGB(255, 255, 255));
            visuals->SetTextColor(background == RGB(255, 255, 255) ? RGB(255, 255, 255) : RGB(0, 0, 0));
        }
        if (resize_mode) {
            const DWORD elapsed = GetTickCount() - started;
            const DWORD times[] = {40, 80, 120, 1500};
            if (resize_step < 4 && elapsed >= times[resize_step]) {
                const LONG widths[] = {420, 500, 460, 404};
                const RECT next{0, 0, widths[resize_step], 294};
                if (FAILED(preview->SetRect(&next))) return 9;
                ++resize_step;
            }
        }
        MSG message{};
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        Sleep(10);
    }

    HWND child = nullptr;
    ole_window->GetWindow(&child);
    UpdateWindow(child);
    const bool saved = child && SaveWindowBitmap(child, output_path, background);
    const bool starts_ok = !forced_theme || renderer_starts() == (theme_change ? 2 : 1);
    preview->Unload();
    listeners = 0;
    EnumThreadWindows(GetCurrentThreadId(), CountThemeWindows, reinterpret_cast<LPARAM>(&listeners));
    if (listeners != 0) return 13; // No theme receiver may outlive Unload.
    visuals->Release();
    ole_window->Release();
    initialize->Release();
    preview->Release();
    DestroyWindow(parent);
    if (library) FreeLibrary(library);
    CoUninitialize();
    if (!forced_theme && SystemBackground() != background) return 14; // OS changed during test: rerun.
    if (!starts_ok) return 15; // Duplicate notifications must not repeat parsing/rendering.
    return saved ? 0 : 8;
}
