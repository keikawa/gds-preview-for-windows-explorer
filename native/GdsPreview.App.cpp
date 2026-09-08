#define WIN32_LEAN_AND_MEAN
#include <windows.h>

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    MessageBoxW(nullptr,
        L"GDS Preview for Windows Explorer\n\n"
        L"Select a .gds or .gdsii file in Explorer.\n"
        L"Enable the preview pane with Alt+P.\n\n"
        L"\u00A9 2026 GDS Preview for Windows Explorer contributors\n"
        L"MIT License\n\n"
        L"https://github.com/keikawa/gds-preview-for-windows-explorer",
        L"About GDS Preview for Windows Explorer",
        MB_OK | MB_ICONINFORMATION | MB_SETFOREGROUND);
    return 0;
}
