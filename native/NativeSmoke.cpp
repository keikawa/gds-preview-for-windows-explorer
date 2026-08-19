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

static bool SaveWindowBitmap(HWND window, const wchar_t* path) {
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
    return output.good();
}

int wmain(int argument_count, wchar_t** arguments) {
    const bool initial_resize_mode = argument_count >= 2 && wcscmp(arguments[1], L"--initial-resize") == 0;
    if ((!initial_resize_mode && argument_count != 4 && argument_count != 5) ||
        (initial_resize_mode && argument_count != 5 && argument_count != 6)) return 2;
    const bool registered_mode = wcscmp(arguments[1], L"--registered") == 0;
    const wchar_t* library_path = initial_resize_mode ? arguments[2] : arguments[1];
    const wchar_t* file_path = initial_resize_mode ? arguments[3] : arguments[2];
    const wchar_t* output_path = initial_resize_mode ? arguments[4] : arguments[3];
    const int wait_index = initial_resize_mode ? 5 : 4;
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

    IInitializeWithFile* initialize = nullptr;
    IOleWindow* ole_window = nullptr;
    preview->QueryInterface(IID_IInitializeWithFile, reinterpret_cast<void**>(&initialize));
    preview->QueryInterface(IID_IOleWindow, reinterpret_cast<void**>(&ole_window));
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

    const DWORD started = GetTickCount();
    while (GetTickCount() - started < wait_time) {
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
    const bool saved = child && SaveWindowBitmap(child, output_path);
    preview->Unload();
    ole_window->Release();
    initialize->Release();
    preview->Release();
    DestroyWindow(parent);
    if (library) FreeLibrary(library);
    CoUninitialize();
    return saved ? 0 : 8;
}
