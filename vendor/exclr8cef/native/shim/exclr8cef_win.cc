// Windows-specific shim implementation for embedded (windowed) browsers.
//
// Mirrors exclr8cef_mac.mm but with Win32 HWNDs instead of NSViews. The
// lifecycle entry points (excef_initialize / excef_execute_process /
// excef_run_message_loop / excef_shutdown / pump variants) for Windows
// already live in exclr8cef.cc under `#if !defined(__APPLE__)` — that
// path is shared with Linux. The pieces that ARE platform-specific here:
//
//   - excef_create_embedded_host: child-window factory, returns HWND
//   - excef_attach_embedded_browser: parents CEF to that HWND
//   - excef_create_browser_view: one-shot create-and-attach
//   - excef_resize_browser_view: SetWindowPos + WasResized
//
// All four functions are also declared in exclr8cef.h with EXCEF_API so
// the managed P/Invoke entries in Generated/Excef.cs resolve cleanly on
// a Windows DLL build. Without this file the Windows DLL would be
// missing those exports and managed embedded-mode calls would throw
// EntryPointNotFoundException.

#if defined(_WIN32)

#include <windows.h>

#include <cmath>
#include <map>
#include <mutex>
#include <set>
#include <string>

#include "include/cef_app.h"
#include "include/cef_browser.h"
#include "include/cef_client.h"

#include "exclr8cef.h"
#include "exclr8cef_app.h"
#include "exclr8cef_osr.h"

namespace {

// Window class used for the host child window. Registered lazily on
// first call into excef_create_embedded_host so the DLL can be loaded
// without paying any GUI setup cost up front.
constexpr wchar_t kHostClassName[] = L"Exclr8CefHostWindow";

LRESULT CALLBACK HostWndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    // Default everything. CEF parents its browser HWND as a child of this
    // window and manages its own messages; we only need to be a valid
    // parent. WM_ERASEBKGND is short-circuited so the host doesn't paint
    // flashes between resizes and CEF's first paint.
    switch (msg) {
        case WM_ERASEBKGND:
            return 1;
        default:
            return DefWindowProcW(hwnd, msg, wp, lp);
    }
}

void EnsureHostClassRegistered() {
    static std::once_flag once;
    std::call_once(once, []() {
        WNDCLASSEXW wc{};
        wc.cbSize = sizeof(wc);
        wc.style = CS_HREDRAW | CS_VREDRAW;
        wc.lpfnWndProc = HostWndProc;
        wc.hInstance = GetModuleHandleW(nullptr);
        wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        wc.hbrBackground = (HBRUSH)(COLOR_WINDOW + 1);
        wc.lpszClassName = kHostClassName;
        RegisterClassExW(&wc);
        // Ignore failure — a second register attempt under a duplicate
        // DLL load returns ERROR_CLASS_ALREADY_EXISTS, which is fine.
    });
}

// Map host HWND → browser id, so excef_resize_browser_view can find the
// backing browser to call WasResized() on. Same purpose as the Mac side's
// g_host_to_id, plus a pending-size cache for the resize-before-creation
// race (Avalonia's ArrangeOverride can fire before CEF finishes async
// browser creation; OnAfterCreated picks up the latest pending size).
std::mutex g_host_map_mu;
std::map<HWND, int> g_host_to_id;
struct PendingSize { int width; int height; };  // DIPs, as sent by the host UI framework
std::map<HWND, PendingSize> g_pending_sizes;
// Hosts whose excef_destroy_embedded_host arrived while their browser was
// still closing. DestroyWindow is deferred to OnBeforeClose — CEF's child
// browser HWND is still parented inside the host until then, and
// destroying the parent rips the child away mid-close.
std::set<HWND> g_release_on_close;

// DIP→physical-pixel scale for a window. The managed side hands us
// Avalonia DIPs; SetWindowPos / CefRect operate in physical pixels, so
// every size that crosses this boundary must be scaled.
double DipScaleFor(HWND hwnd) {
    UINT dpi = hwnd ? GetDpiForWindow(hwnd) : 0;
    return dpi == 0 ? 1.0 : dpi / 96.0;
}
int DipToPx(int dip, double scale) {
    return static_cast<int>(std::lround(dip * scale));
}

// Subclass of the OSR handler used for embedded (windowed) browsers.
// Same handler surface (load / console / drag / permission / …) — only
// adds an OnAfterCreated hook that syncs the host HWND's child to the
// latest pending size and calls WasResized so Chromium lays out the
// page viewport correctly on first paint.
class EmbeddedOsrHandler : public exclr8cef::Exclr8CefOsrHandler {
public:
    EmbeddedOsrHandler(int id, int w, int h, HWND host_hwnd)
        : Exclr8CefOsrHandler(id, w, h, 1.0f, /*paint_cb=*/nullptr),
          host_(host_hwnd) {}

    void OnAfterCreated(CefRefPtr<CefBrowser> browser) override {
        Exclr8CefOsrHandler::OnAfterCreated(browser);
        if (!host_) return;
        // Pick the desired size — prefer a queued resize if Avalonia
        // already pushed one before browser creation completed. Pending
        // sizes are DIPs; GetClientRect is already physical pixels.
        int w, h;
        bool from_pending = false;
        {
            std::lock_guard<std::mutex> lock(g_host_map_mu);
            auto it = g_pending_sizes.find(host_);
            if (it != g_pending_sizes.end()) {
                w = it->second.width;
                h = it->second.height;
                from_pending = true;
            } else {
                RECT r;
                GetClientRect(host_, &r);
                w = r.right - r.left;
                h = r.bottom - r.top;
            }
        }
        if (from_pending) {
            double scale = DipScaleFor(host_);
            w = DipToPx(w, scale);
            h = DipToPx(h, scale);
        }
        // Resize the CEF child HWND (it's the only child of the host).
        if (HWND child = GetWindow(host_, GW_CHILD)) {
            SetWindowPos(child, nullptr, 0, 0, w, h,
                         SWP_NOZORDER | SWP_NOACTIVATE);
        }
        browser->GetHost()->WasResized();
    }

    void OnBeforeClose(CefRefPtr<CefBrowser> browser) override {
        bool destroy_host = false;
        if (host_) {
            std::lock_guard<std::mutex> lock(g_host_map_mu);
            g_host_to_id.erase(host_);
            g_pending_sizes.erase(host_);
            destroy_host = g_release_on_close.erase(host_) > 0;
        }
        if (destroy_host) {
            // The host's destroy call arrived while we were still closing;
            // now that CEF's child HWND is gone, tear down the host window.
            // Guard against the HWND having been destroyed (and possibly
            // recycled) out from under us by ancestor teardown — destroying
            // a recycled handle would kill an unrelated window.
            wchar_t cls[64] = {};
            if (IsWindow(host_) &&
                GetClassNameW(host_, cls, 64) > 0 &&
                wcscmp(cls, kHostClassName) == 0) {
                DestroyWindow(host_);
            }
        }
        Exclr8CefOsrHandler::OnBeforeClose(browser);
    }

private:
    HWND host_;
    IMPLEMENT_REFCOUNTING(EmbeddedOsrHandler);
};

HWND CreateHostWindow(HWND parent, int width, int height) {
    EnsureHostClassRegistered();
    // WS_CHILD + WS_CLIPCHILDREN: this HWND lives inside the host app's
    // window. WS_CLIPCHILDREN prevents the host from painting over CEF's
    // child HWND during its own paint cycle.
    //
    // A WS_CHILD window CANNOT be created with a null parent —
    // CreateWindowExW fails with ERROR_TLW_WITH_WSCHILD. Callers that have
    // the real parent (Avalonia's CreateNativeControlCore receives it)
    // should pass it; otherwise we fall back to HWND_MESSAGE, which
    // satisfies the parent requirement and is replaced when the UI
    // framework re-parents the returned HWND into the visual tree.
    return CreateWindowExW(
        /*dwExStyle=*/0,
        kHostClassName,
        L"",
        WS_CHILD | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
        /*x=*/0, /*y=*/0,
        width > 0 ? width : 1,
        height > 0 ? height : 1,
        parent ? parent : HWND_MESSAGE,
        /*hMenu=*/nullptr,
        GetModuleHandleW(nullptr),
        /*lpParam=*/nullptr);
}

}  // namespace

// Phase 1: create an empty host HWND that the UI framework will parent.
extern "C" void* excef_create_embedded_host(int width, int height) {
    HWND h = CreateHostWindow(nullptr, width, height);
    return reinterpret_cast<void*>(h);
}

// Parent-aware variant — the preferred entry point on Windows, where a
// WS_CHILD host cannot exist without a parent. The managed side passes
// the parent handle Avalonia provides in CreateNativeControlCore.
extern "C" void* excef_create_embedded_host_in_parent(void* parent,
                                                      int width, int height) {
    HWND h = CreateHostWindow(reinterpret_cast<HWND>(parent), width, height);
    return reinterpret_cast<void*>(h);
}

// Destroy a host HWND created by excef_create_embedded_host[_in_parent].
// If the browser attached to it is still closing (async), destruction is
// deferred to its OnBeforeClose; otherwise it happens immediately.
extern "C" void excef_destroy_embedded_host(void* host_view_ptr) {
    if (!host_view_ptr) return;
    HWND host = reinterpret_cast<HWND>(host_view_ptr);
    {
        std::lock_guard<std::mutex> lock(g_host_map_mu);
        if (g_host_to_id.count(host)) {
            g_release_on_close.insert(host);
            return;
        }
        g_pending_sizes.erase(host);
    }
    DestroyWindow(host);
}

// Phase 2: attach a CEF browser to a previously-created host HWND. Call
// this AFTER the UI framework has parented the HWND so Chromium reads
// the correct effective DPI at browser-creation time.
//
// `background_color` (ARGB) sets the colour Chromium fills the
// render target with before any HTML paints. CEF's default is opaque
// white; pass 0 to keep it. Alpha must be 0x00 or 0xFF.
extern "C" int excef_attach_embedded_browser_in_context_v2(void* host_view_ptr,
                                                             int width, int height,
                                                             const char* url,
                                                             int context_handle,
                                                             uint32_t background_color) {
    if (!host_view_ptr || !url) return 0;
    HWND host = reinterpret_cast<HWND>(host_view_ptr);

    CefWindowInfo window_info;
    // width/height arrive as DIPs from the managed layer; CefRect wants
    // physical pixels. The GetClientRect fallback is already physical.
    RECT host_rect;
    GetClientRect(host, &host_rect);
    double scale = DipScaleFor(host);
    int effective_w = width  > 0 ? DipToPx(width, scale)  : host_rect.right  - host_rect.left;
    int effective_h = height > 0 ? DipToPx(height, scale) : host_rect.bottom - host_rect.top;
    window_info.SetAsChild(host, CefRect(0, 0, effective_w, effective_h));
    window_info.runtime_style = CEF_RUNTIME_STYLE_ALLOY;

    int id = exclr8cef::AllocateBrowserId();
    CefRefPtr<EmbeddedOsrHandler> handler(
        new EmbeddedOsrHandler(id, effective_w, effective_h, host));
    exclr8cef::RegisterOsrHandler(id, handler);
    {
        std::lock_guard<std::mutex> lock(g_host_map_mu);
        g_host_to_id[host] = id;
    }

    CefRefPtr<CefRequestContext> ctx = exclr8cef::ResolveContext(context_handle);
    CefRefPtr<CefRequestContext> pass = context_handle == 0 ? nullptr : ctx;

    CefBrowserSettings browser_settings;
    if (background_color != 0) {
        browser_settings.background_color = background_color;
    }
    bool ok = CefBrowserHost::CreateBrowser(
        window_info, handler.get(), url, browser_settings,
        exclr8cef::CreateBrowserExtraInfo(), pass);

    if (!ok) {
        exclr8cef::UnregisterOsrHandler(id);
        std::lock_guard<std::mutex> lock(g_host_map_mu);
        g_host_to_id.erase(host);
        g_pending_sizes.erase(host);
        return 0;
    }
    return id;
}

extern "C" int excef_attach_embedded_browser_in_context(void* host_view_ptr,
                                                          int width, int height,
                                                          const char* url,
                                                          int context_handle) {
    return excef_attach_embedded_browser_in_context_v2(host_view_ptr, width, height, url, context_handle, 0);
}

extern "C" int excef_attach_embedded_browser(void* host_view_ptr,
                                              int width, int height,
                                              const char* url) {
    return excef_attach_embedded_browser_in_context_v2(host_view_ptr, width, height, url, 0, 0);
}

// Combined create + attach. Returns the host HWND as void* (with the
// browser id written to out_browser_id) so a single managed call can
// produce a renderable handle.
extern "C" void* excef_create_browser_view_in_context(int width, int height,
                                                       const char* url,
                                                       int* out_browser_id,
                                                       int context_handle) {
    if (out_browser_id) *out_browser_id = 0;
    if (!url) return nullptr;

    HWND host = CreateHostWindow(nullptr, width, height);
    if (!host) return nullptr;

    CefWindowInfo window_info;
    double scale = DipScaleFor(host);
    window_info.SetAsChild(host, CefRect(0, 0, DipToPx(width, scale),
                                          DipToPx(height, scale)));
    window_info.runtime_style = CEF_RUNTIME_STYLE_ALLOY;

    int id = exclr8cef::AllocateBrowserId();
    CefRefPtr<EmbeddedOsrHandler> handler(
        new EmbeddedOsrHandler(id, width, height, host));
    exclr8cef::RegisterOsrHandler(id, handler);
    {
        std::lock_guard<std::mutex> lock(g_host_map_mu);
        g_host_to_id[host] = id;
    }

    CefRefPtr<CefRequestContext> ctx = exclr8cef::ResolveContext(context_handle);
    CefRefPtr<CefRequestContext> pass = context_handle == 0 ? nullptr : ctx;

    CefBrowserSettings browser_settings;
    bool ok = CefBrowserHost::CreateBrowser(
        window_info, handler.get(), url, browser_settings,
        exclr8cef::CreateBrowserExtraInfo(), pass);

    if (!ok) {
        exclr8cef::UnregisterOsrHandler(id);
        {
            std::lock_guard<std::mutex> lock(g_host_map_mu);
            g_host_to_id.erase(host);
        }
        DestroyWindow(host);
        return nullptr;
    }

    if (out_browser_id) *out_browser_id = id;
    return reinterpret_cast<void*>(host);
}

extern "C" void* excef_create_browser_view(int width, int height,
                                           const char* url,
                                           int* out_browser_id) {
    return excef_create_browser_view_in_context(width, height, url, out_browser_id, 0);
}

// Resize the host HWND, its child CEF window, and notify Chromium so
// the page viewport relays out. Without WasResized() the page keeps
// its original viewport size and overflows when the host grows.
// Hide/show the host HWND in place. Mirrors the mac impl so callers
// (e.g. VibeCoder's tab-switch path) can hide an embedded browser
// without tearing it down. See exclr8cef_mac.mm for rationale.
extern "C" void excef_set_embedded_host_hidden(void* host_view_ptr, int hidden) {
    if (!host_view_ptr) return;
    HWND host = reinterpret_cast<HWND>(host_view_ptr);
    ShowWindow(host, hidden ? SW_HIDE : SW_SHOWNOACTIVATE);
    int browser_id = 0;
    {
        std::lock_guard<std::mutex> lock(g_host_map_mu);
        auto it = g_host_to_id.find(host);
        if (it != g_host_to_id.end()) browser_id = it->second;
    }
    if (browser_id != 0) {
        auto handler = exclr8cef::LookupOsrHandler(browser_id);
        if (handler && handler->browser()) {
            handler->browser()->GetHost()->WasHidden(hidden != 0);
        }
    }
}

extern "C" void excef_resize_browser_view(void* host_view_ptr,
                                          int width, int height) {
    if (!host_view_ptr || width <= 0 || height <= 0) return;
    HWND host = reinterpret_cast<HWND>(host_view_ptr);

    // Stash the latest desired size so OnAfterCreated can sync to it if
    // the browser isn't ready yet (Avalonia's ArrangeOverride often fires
    // before CEF finishes async browser creation).
    int browser_id = 0;
    {
        std::lock_guard<std::mutex> lock(g_host_map_mu);
        g_pending_sizes[host] = PendingSize{width, height};
        auto it = g_host_to_id.find(host);
        if (it != g_host_to_id.end()) browser_id = it->second;
    }

    // width/height are DIPs from the managed layer; SetWindowPos wants
    // physical pixels.
    double scale = DipScaleFor(host);
    int px_w = DipToPx(width, scale);
    int px_h = DipToPx(height, scale);

    // The host HWND itself is positioned/sized by Avalonia's
    // TryUpdateNativeControlPosition (in physical pixels) — resizing it
    // here too would fight that. Only CEF's child HWND is ours to manage.
    if (HWND child = GetWindow(host, GW_CHILD)) {
        SetWindowPos(child, nullptr, 0, 0, px_w, px_h,
                      SWP_NOZORDER | SWP_NOACTIVATE);
    }

    if (browser_id != 0) {
        auto handler = exclr8cef::LookupOsrHandler(browser_id);
        if (handler && handler->browser()) {
            handler->browser()->GetHost()->WasResized();
        }
    }
}

#endif  // _WIN32
