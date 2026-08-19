#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <string>

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    int argument_count = 0;
    LPWSTR* arguments = CommandLineToArgvW(GetCommandLineW(), &argument_count);

    const std::wstring instructions =
        L"GDS Preview for Windows Explorer is installed.\n\n"
        L"1. Open File Explorer.\n"
        L"2. Enable the preview pane with Alt+P.\n"
        L"3. Select a .gds or .gdsii file.\n\n"
        L"The preview is rendered in an isolated process to protect Explorer.";

    if (arguments && argument_count > 1) {
        std::wstring message = instructions;
        message += L"\n\nSelected file:\n";
        message += arguments[1];
        MessageBoxW(nullptr, message.c_str(), L"GDS Preview for Windows Explorer",
            MB_OK | MB_ICONINFORMATION | MB_SETFOREGROUND);
    } else {
        const std::wstring message = instructions +
            L"\n\nWould you like to copy the included demo GDSII file to your local app-data "
            L"folder and select it in Explorer?";
        if (MessageBoxW(nullptr, message.c_str(), L"GDS Preview for Windows Explorer",
                MB_YESNO | MB_ICONINFORMATION | MB_SETFOREGROUND) == IDYES) {
            wchar_t module_path[32768];
            wchar_t local_app_data[MAX_PATH];
            const DWORD module_length = GetModuleFileNameW(nullptr, module_path, ARRAYSIZE(module_path));
            if (module_length && module_length < ARRAYSIZE(module_path) &&
                SUCCEEDED(SHGetFolderPathW(nullptr, CSIDL_LOCAL_APPDATA | CSIDL_FLAG_CREATE,
                    nullptr, SHGFP_TYPE_CURRENT, local_app_data))) {
                std::wstring source(module_path, module_length);
                const auto slash = source.find_last_of(L"\\/");
                if (slash != std::wstring::npos) source.resize(slash + 1);
                source += L"Samples\\demo.gds";

                std::wstring destination_directory(local_app_data);
                destination_directory += L"\\GdsPreview";
                CreateDirectoryW(destination_directory.c_str(), nullptr);
                const std::wstring destination = destination_directory + L"\\demo.gds";
                if (CopyFileW(source.c_str(), destination.c_str(), FALSE)) {
                    const std::wstring explorer_arguments = L"/select,\"" + destination + L"\"";
                    ShellExecuteW(nullptr, L"open", L"explorer.exe", explorer_arguments.c_str(),
                        nullptr, SW_SHOWNORMAL);
                } else {
                    MessageBoxW(nullptr, L"The demo file could not be copied.",
                        L"GDS Preview for Windows Explorer", MB_OK | MB_ICONERROR);
                }
            }
        }
    }
    if (arguments) LocalFree(arguments);
    return 0;
}
