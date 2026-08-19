#define UNICODE
#define _UNICODE
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shobjidl.h>
#include <propsys.h>
#include <ocidl.h>
#include <atomic>
#include <algorithm>
#include <new>
#include <string>
#include <vector>

static HMODULE g_module = nullptr;
static std::atomic<long> g_object_count{0};
static std::atomic<long> g_lock_count{0};

static const CLSID CLSID_GdsPreview =
{0x87f8a6bb, 0x6b13, 0x4a41, {0x9d, 0x54, 0xee, 0xb3, 0x9d, 0xbd, 0x1d, 0x6e}};

static constexpr UINT WM_PREVIEW_COMPLETE = WM_APP + 0x4A1;
static constexpr DWORD PREVIEW_TIMEOUT_MS = 6000;
static constexpr size_t SHARED_HEADER_SIZE = 1024;
static constexpr LONG SHARED_MAGIC = 0x56504447;

#pragma pack(push, 4)
struct SharedPreviewHeader {
    LONG magic;
    volatile LONG status;
    LONG width;
    LONG height;
    LONG stride;
    BYTE reserved[12];
    wchar_t message[256];
};
#pragma pack(pop)

class PreviewHandler final : public IPreviewHandler,
                             public IInitializeWithFile,
                             public IObjectWithSite,
                             public IOleWindow,
                             public IPreviewHandlerVisuals {
public:
    PreviewHandler() { g_object_count.fetch_add(1); SetRectEmpty(&rect_); }

    ~PreviewHandler() {
        StopJob();
        if (site_) site_->Release();
        g_object_count.fetch_sub(1);
    }

    IFACEMETHODIMP QueryInterface(REFIID iid, void** value) override {
        if (!value) return E_POINTER;
        *value = nullptr;
        if (IsEqualIID(iid, IID_IUnknown) || IsEqualIID(iid, IID_IPreviewHandler))
            *value = static_cast<IPreviewHandler*>(this);
        else if (IsEqualIID(iid, IID_IInitializeWithFile))
            *value = static_cast<IInitializeWithFile*>(this);
        else if (IsEqualIID(iid, IID_IObjectWithSite))
            *value = static_cast<IObjectWithSite*>(this);
        else if (IsEqualIID(iid, IID_IOleWindow))
            *value = static_cast<IOleWindow*>(this);
        else if (IsEqualIID(iid, IID_IPreviewHandlerVisuals))
            *value = static_cast<IPreviewHandlerVisuals*>(this);
        else
            return E_NOINTERFACE;
        AddRef();
        return S_OK;
    }

    IFACEMETHODIMP_(ULONG) AddRef() override { return static_cast<ULONG>(++references_); }
    IFACEMETHODIMP_(ULONG) Release() override {
        const auto remaining = static_cast<ULONG>(--references_);
        if (!remaining) delete this;
        return remaining;
    }

    IFACEMETHODIMP Initialize(LPCWSTR file_path, DWORD) override {
        if (!file_path) return E_INVALIDARG;
        if (!file_path_.empty()) return HRESULT_FROM_WIN32(ERROR_ALREADY_INITIALIZED);
        file_path_ = file_path;
        return S_OK;
    }

    IFACEMETHODIMP SetWindow(HWND parent, const RECT* rect) override {
        if (!parent || !rect) return E_INVALIDARG;
        parent_ = parent;
        rect_ = *rect;
        if (window_) {
            SetParent(window_, parent_);
            ResizeWindow();
        }
        return S_OK;
    }

    IFACEMETHODIMP SetRect(const RECT* rect) override {
        if (!rect) return E_INVALIDARG;
        rect_ = *rect;
        ResizeWindow();
        return S_OK;
    }

    IFACEMETHODIMP DoPreview() override {
        if (file_path_.empty() || !parent_) return E_UNEXPECTED;
        StopJob();
        stopping_.store(false);
        if (!CreatePreviewWindow()) return HRESULT_FROM_WIN32(GetLastError());
        StartRenderer();
        return S_OK;
    }

    IFACEMETHODIMP Unload() override {
        StopJob();
        file_path_.clear();
        return S_OK;
    }

    IFACEMETHODIMP SetFocus() override {
        if (!window_) return S_FALSE;
        ::SetFocus(window_);
        return S_OK;
    }

    IFACEMETHODIMP QueryFocus(HWND* window) override {
        if (!window) return E_POINTER;
        *window = ::GetFocus();
        return *window ? S_OK : S_FALSE;
    }

    IFACEMETHODIMP TranslateAccelerator(MSG*) override { return S_FALSE; }

    IFACEMETHODIMP SetSite(IUnknown* site) override {
        if (site) site->AddRef();
        if (site_) site_->Release();
        site_ = site;
        return S_OK;
    }

    IFACEMETHODIMP GetSite(REFIID iid, void** site) override {
        if (!site) return E_POINTER;
        *site = nullptr;
        return site_ ? site_->QueryInterface(iid, site) : E_FAIL;
    }

    IFACEMETHODIMP GetWindow(HWND* window) override {
        if (!window) return E_POINTER;
        *window = window_ ? window_ : parent_;
        return *window ? S_OK : E_FAIL;
    }

    IFACEMETHODIMP ContextSensitiveHelp(BOOL) override { return E_NOTIMPL; }

    IFACEMETHODIMP SetBackgroundColor(COLORREF color) override {
        background_ = color;
        if (window_) InvalidateRect(window_, nullptr, TRUE);
        return S_OK;
    }

    IFACEMETHODIMP SetFont(const LOGFONTW* font) override {
        if (font) font_ = *font;
        return S_OK;
    }

    IFACEMETHODIMP SetTextColor(COLORREF color) override {
        text_ = color;
        if (window_) InvalidateRect(window_, nullptr, TRUE);
        return S_OK;
    }

private:
    std::atomic<ULONG> references_{1};
    std::wstring file_path_;
    HWND parent_ = nullptr;
    HWND window_ = nullptr;
    RECT rect_{};
    IUnknown* site_ = nullptr;
    COLORREF background_ = RGB(24, 27, 32);
    COLORREF text_ = RGB(225, 230, 238);
    LOGFONTW font_{};
    HANDLE mapping_ = nullptr;
    HANDLE process_ = nullptr;
    HANDLE worker_ = nullptr;
    DWORD worker_id_ = 0;
    SharedPreviewHeader* shared_ = nullptr;
    SIZE_T mapping_size_ = 0;
    std::atomic<bool> stopping_{false};
    std::wstring native_error_;

    static LRESULT CALLBACK WindowProcedure(HWND window, UINT message, WPARAM wparam, LPARAM lparam) {
        auto* handler = reinterpret_cast<PreviewHandler*>(GetWindowLongPtrW(window, GWLP_USERDATA));
        if (message == WM_NCCREATE) {
            auto* create = reinterpret_cast<CREATESTRUCTW*>(lparam);
            handler = static_cast<PreviewHandler*>(create->lpCreateParams);
            SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(handler));
        }
        if (!handler) return DefWindowProcW(window, message, wparam, lparam);
        switch (message) {
        case WM_ERASEBKGND:
            return 1;
        case WM_PREVIEW_COMPLETE:
            InvalidateRect(window, nullptr, FALSE);
            return 0;
        case WM_PAINT:
            handler->Paint(window);
            return 0;
        default:
            return DefWindowProcW(window, message, wparam, lparam);
        }
    }

    bool CreatePreviewWindow() {
        WNDCLASSEXW wc{};
        wc.cbSize = sizeof(wc);
        wc.hInstance = g_module;
        wc.lpfnWndProc = WindowProcedure;
        wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        wc.lpszClassName = L"GdsPreview.Native.Window.87F8A6BB";
        RegisterClassExW(&wc);
        window_ = CreateWindowExW(0, wc.lpszClassName, L"", WS_CHILD | WS_VISIBLE,
            rect_.left, rect_.top,
            std::max(1L, rect_.right - rect_.left),
            std::max(1L, rect_.bottom - rect_.top),
            parent_, nullptr, g_module, this);
        return window_ != nullptr;
    }

    void ResizeWindow() {
        if (!window_) return;
        MoveWindow(window_, rect_.left, rect_.top,
            std::max(1L, rect_.right - rect_.left), std::max(1L, rect_.bottom - rect_.top), TRUE);
    }

    void StartRenderer() {
        native_error_.clear();
        LONG requested_width = rect_.right - rect_.left;
        LONG requested_height = rect_.bottom - rect_.top;
        RECT parent_client{};
        if ((requested_width < 64 || requested_height < 64) && parent_ && GetClientRect(parent_, &parent_client)) {
            if (requested_width < 64) requested_width = parent_client.right - parent_client.left;
            if (requested_height < 64) requested_height = parent_client.bottom - parent_client.top;
        }
        // Explorer can call DoPreview while its first layout rectangle is still tiny.  Scale a
        // useful minimum canvas uniformly so its aspect ratio survives the later SetRect call.
        requested_width = std::max(1L, requested_width);
        requested_height = std::max(1L, requested_height);
        const double minimum_scale = std::max(640.0 / requested_width, 480.0 / requested_height);
        const double maximum_scale = std::min(1600.0 / requested_width, 1200.0 / requested_height);
        double render_scale = 1.0;
        if (requested_width < 640 || requested_height < 480)
            render_scale = std::min(minimum_scale, maximum_scale);
        else if (requested_width > 1600 || requested_height > 1200)
            render_scale = maximum_scale;
        const LONG width = std::clamp(static_cast<LONG>(requested_width * render_scale + 0.5), 1L, 1600L);
        const LONG height = std::clamp(static_cast<LONG>(requested_height * render_scale + 0.5), 1L, 1200L);
        mapping_size_ = SHARED_HEADER_SIZE + static_cast<SIZE_T>(width) * height * 4;
        SECURITY_ATTRIBUTES security{sizeof(security), nullptr, TRUE};
        mapping_ = CreateFileMappingW(INVALID_HANDLE_VALUE, &security, PAGE_READWRITE,
            static_cast<DWORD>(mapping_size_ >> 32), static_cast<DWORD>(mapping_size_), nullptr);
        if (!mapping_) return SetNativeError(L"Unable to allocate preview memory.");
        shared_ = static_cast<SharedPreviewHeader*>(MapViewOfFile(mapping_, FILE_MAP_ALL_ACCESS, 0, 0, mapping_size_));
        if (!shared_) return SetNativeError(L"Unable to map preview memory.");
        ZeroMemory(shared_, mapping_size_);
        shared_->magic = SHARED_MAGIC;
        shared_->width = width;
        shared_->height = height;
        shared_->stride = width * 4;

        wchar_t module_path[32768];
        const DWORD path_length = GetModuleFileNameW(g_module, module_path, ARRAYSIZE(module_path));
        if (!path_length || path_length == ARRAYSIZE(module_path))
            return SetNativeError(L"Unable to locate the preview renderer.");
        std::wstring directory(module_path, path_length);
        const auto slash = directory.find_last_of(L"\\/");
        if (slash != std::wstring::npos) directory.resize(slash);
        const std::wstring renderer = directory + L"\\GdsPreview.Renderer.exe";
        std::wstring command = L"\"" + renderer + L"\" --mapping " +
            std::to_wstring(reinterpret_cast<unsigned long long>(mapping_)) +
            L" --width " + std::to_wstring(width) + L" --height " + std::to_wstring(height) +
            L" --file \"" + file_path_ + L"\"";
        std::vector<wchar_t> mutable_command(command.begin(), command.end());
        mutable_command.push_back(L'\0');

        STARTUPINFOW startup{};
        startup.cb = sizeof(startup);
        PROCESS_INFORMATION process_info{};
        if (!CreateProcessW(renderer.c_str(), mutable_command.data(), nullptr, nullptr, TRUE,
                CREATE_NO_WINDOW, nullptr, directory.c_str(), &startup, &process_info))
            return SetNativeError(L"Unable to start the isolated preview renderer.");
        CloseHandle(process_info.hThread);
        process_ = process_info.hProcess;
        SetHandleInformation(mapping_, HANDLE_FLAG_INHERIT, 0);
        AddRef();
        worker_ = CreateThread(nullptr, 0, WorkerEntry, this, 0, &worker_id_);
        if (!worker_) {
            Release();
            TerminateProcess(process_, 2);
            SetNativeError(L"Unable to monitor the preview renderer.");
        }
    }

    static DWORD WINAPI WorkerEntry(void* context) {
        auto* handler = static_cast<PreviewHandler*>(context);
        const DWORD wait = WaitForSingleObject(handler->process_, PREVIEW_TIMEOUT_MS);
        if (wait == WAIT_TIMEOUT) {
            TerminateProcess(handler->process_, 3);
            handler->SetNativeError(L"Preview stopped after 6 seconds to protect Explorer.");
        } else if (handler->shared_ && handler->shared_->status == 0) {
            handler->SetNativeError(L"The isolated preview renderer exited unexpectedly.");
        }
        if (!handler->stopping_.load() && handler->window_)
            PostMessageW(handler->window_, WM_PREVIEW_COMPLETE, 0, 0);
        handler->Release();
        return 0;
    }

    void SetNativeError(const wchar_t* message) {
        native_error_ = message;
        if (!shared_) {
            if (window_) InvalidateRect(window_, nullptr, FALSE);
            return;
        }
        wcsncpy(shared_->message, message, ARRAYSIZE(shared_->message) - 1);
        shared_->message[ARRAYSIZE(shared_->message) - 1] = L'\0';
        InterlockedExchange(&shared_->status, 2);
        if (window_) InvalidateRect(window_, nullptr, FALSE);
    }

    void StopJob() {
        stopping_.store(true);
        if (process_ && WaitForSingleObject(process_, 0) == WAIT_TIMEOUT)
            TerminateProcess(process_, 4);
        if (worker_ && GetCurrentThreadId() != worker_id_) WaitForSingleObject(worker_, 2000);
        if (worker_) { CloseHandle(worker_); worker_ = nullptr; }
        worker_id_ = 0;
        if (process_) { CloseHandle(process_); process_ = nullptr; }
        if (shared_) { UnmapViewOfFile(shared_); shared_ = nullptr; }
        if (mapping_) { CloseHandle(mapping_); mapping_ = nullptr; }
        if (window_) { DestroyWindow(window_); window_ = nullptr; }
    }

    void Paint(HWND window) {
        PAINTSTRUCT paint{};
        HDC dc = BeginPaint(window, &paint);
        RECT client{};
        GetClientRect(window, &client);
        HBRUSH background = CreateSolidBrush(background_);
        FillRect(dc, &client, background);
        DeleteObject(background);

        const LONG status = shared_ ? shared_->status : (native_error_.empty() ? 0 : 2);
        if (status == 1 && shared_->magic == SHARED_MAGIC) {
            BITMAPINFO bitmap{};
            bitmap.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
            bitmap.bmiHeader.biWidth = shared_->width;
            bitmap.bmiHeader.biHeight = -shared_->height;
            bitmap.bmiHeader.biPlanes = 1;
            bitmap.bmiHeader.biBitCount = 32;
            bitmap.bmiHeader.biCompression = BI_RGB;
            const BYTE* pixels = reinterpret_cast<const BYTE*>(shared_) + SHARED_HEADER_SIZE;
            const LONG client_width = std::max(1L, client.right - client.left);
            const LONG client_height = std::max(1L, client.bottom - client.top);
            LONG destination_width = client_width;
            LONG destination_height = MulDiv(shared_->height, client_width, shared_->width);
            if (destination_height > client_height) {
                destination_height = client_height;
                destination_width = MulDiv(shared_->width, client_height, shared_->height);
            }
            destination_width = std::max(1L, destination_width);
            destination_height = std::max(1L, destination_height);
            const LONG destination_x = (client_width - destination_width) / 2;
            const LONG destination_y = (client_height - destination_height) / 2;
            SetStretchBltMode(dc, HALFTONE);
            StretchDIBits(dc, destination_x, destination_y, destination_width, destination_height, 0, 0,
                shared_->width, shared_->height, pixels, &bitmap, DIB_RGB_COLORS, SRCCOPY);
        } else {
            SetBkMode(dc, TRANSPARENT);
            ::SetTextColor(dc, status == 2 ? RGB(255, 150, 150) : text_);
            HFONT font = static_cast<HFONT>(GetStockObject(DEFAULT_GUI_FONT));
            const auto old_font = SelectObject(dc, font);
            RECT text_rect = client;
            InflateRect(&text_rect, -24, -24);
            const wchar_t* message = status == 2
                ? (shared_ ? shared_->message : native_error_.c_str())
                : L"Loading GDSII in an isolated process...";
            DrawTextW(dc, message, -1, &text_rect, DT_CENTER | DT_VCENTER | DT_WORDBREAK | DT_NOPREFIX);
            SelectObject(dc, old_font);
        }
        EndPaint(window, &paint);
    }
};

class ClassFactory final : public IClassFactory {
public:
    IFACEMETHODIMP QueryInterface(REFIID iid, void** value) override {
        if (!value) return E_POINTER;
        *value = nullptr;
        if (!IsEqualIID(iid, IID_IUnknown) && !IsEqualIID(iid, IID_IClassFactory)) return E_NOINTERFACE;
        *value = static_cast<IClassFactory*>(this);
        AddRef();
        return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return static_cast<ULONG>(++references_); }
    IFACEMETHODIMP_(ULONG) Release() override {
        const auto remaining = static_cast<ULONG>(--references_);
        if (!remaining) delete this;
        return remaining;
    }
    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID iid, void** value) override {
        if (outer) return CLASS_E_NOAGGREGATION;
        auto* handler = new (std::nothrow) PreviewHandler();
        if (!handler) return E_OUTOFMEMORY;
        const HRESULT result = handler->QueryInterface(iid, value);
        handler->Release();
        return result;
    }
    IFACEMETHODIMP LockServer(BOOL lock) override {
        if (lock) g_lock_count.fetch_add(1); else g_lock_count.fetch_sub(1);
        return S_OK;
    }
private:
    std::atomic<ULONG> references_{1};
};

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID iid, void** value) {
    if (!IsEqualCLSID(clsid, CLSID_GdsPreview)) return CLASS_E_CLASSNOTAVAILABLE;
    auto* factory = new (std::nothrow) ClassFactory();
    if (!factory) return E_OUTOFMEMORY;
    const HRESULT result = factory->QueryInterface(iid, value);
    factory->Release();
    return result;
}

extern "C" HRESULT __stdcall DllCanUnloadNow() {
    return g_object_count.load() == 0 && g_lock_count.load() == 0 ? S_OK : S_FALSE;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, void*) {
    if (reason == DLL_PROCESS_ATTACH) {
        g_module = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}
