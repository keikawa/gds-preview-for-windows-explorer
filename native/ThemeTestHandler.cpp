// Test-only build of the real handler. Replace only the OS colour queries so
// tests cannot change the user's Windows settings. Never shipped or installed.
#define UNICODE
#define _UNICODE
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cwchar>

static int test_theme = 1; // 0 dark, 1 light, 2 high contrast, -1 read failure.
static int renderer_starts = 0;

static LSTATUS WINAPI TestRegGetValue(HKEY key, LPCWSTR path, LPCWSTR name,
        DWORD flags, LPDWORD type, PVOID data, LPDWORD bytes) {
    if (key != HKEY_CURRENT_USER || !path || !name ||
        wcscmp(path, L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize") ||
        wcscmp(name, L"AppsUseLightTheme")) return ERROR_FILE_NOT_FOUND;
    if (test_theme < 0) return ERROR_ACCESS_DENIED;
    if (flags != RRF_RT_REG_DWORD || !bytes || *bytes < sizeof(DWORD)) return ERROR_INVALID_PARAMETER;
    if (type) *type = REG_DWORD;
    *bytes = sizeof(DWORD);
    *static_cast<DWORD*>(data) = test_theme == 0 ? 0 : 1;
    return ERROR_SUCCESS;
}

static BOOL WINAPI TestSystemParametersInfo(UINT action, UINT size, PVOID data, UINT flags) {
    if (action != SPI_GETHIGHCONTRAST) return SystemParametersInfoW(action, size, data, flags);
    static_cast<HIGHCONTRASTW*>(data)->dwFlags = test_theme == 2 ? HCF_HIGHCONTRASTON : 0;
    return TRUE;
}

static DWORD WINAPI TestGetSysColor(int index) {
    if (test_theme == 2 && index == COLOR_WINDOW) return RGB(0, 0, 0);
    if (test_theme == 2 && index == COLOR_WINDOWTEXT) return RGB(255, 255, 0);
    return GetSysColor(index);
}

static BOOL WINAPI TestCreateProcess(LPCWSTR app, LPWSTR command,
        LPSECURITY_ATTRIBUTES process_security, LPSECURITY_ATTRIBUTES thread_security,
        BOOL inherit, DWORD flags, LPVOID environment, LPCWSTR directory,
        LPSTARTUPINFOW startup, LPPROCESS_INFORMATION process) {
    const BOOL started = CreateProcessW(app, command, process_security, thread_security,
        inherit, flags, environment, directory, startup, process);
    if (started) ++renderer_starts;
    return started;
}

#define RegGetValueW TestRegGetValue
#define SystemParametersInfoW TestSystemParametersInfo
#define GetSysColor TestGetSysColor
#define CreateProcessW TestCreateProcess
#include "GdsPreview.Native.cpp"
#undef RegGetValueW
#undef SystemParametersInfoW
#undef GetSysColor
#undef CreateProcessW

extern "C" __declspec(dllexport) void __stdcall GdsPreviewTestSetTheme(int theme) {
    test_theme = theme;
}

extern "C" __declspec(dllexport) int __stdcall GdsPreviewTestRendererStarts() {
    return renderer_starts;
}
