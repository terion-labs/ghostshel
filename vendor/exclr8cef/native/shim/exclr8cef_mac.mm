// macOS-specific shim implementation:
// - CefScopedLibraryLoader to load the CEF framework dylib at runtime
// - NSApplication subclass conforming to CefAppProtocol (CEF requirement)
// - excef_execute_process / excef_initialize / excef_run_message_loop / excef_shutdown

#import <Cocoa/Cocoa.h>
#import <CoreVideo/CoreVideo.h>
#import <IOSurface/IOSurface.h>
#import <Metal/Metal.h>

#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <memory>
#include <mutex>

#include "include/cef_app.h"
#include "include/cef_application_mac.h"
#include "include/cef_browser.h"
#include "include/cef_client.h"
#if defined(CEF_USE_SANDBOX)
#include "include/cef_sandbox_mac.h"
#endif
#include "include/wrapper/cef_library_loader.h"

#include "exclr8cef.h"
#include "exclr8cef_app.h"
#include "exclr8cef_client.h"
#include "exclr8cef_osr.h"

#include <map>
#include <mutex>
#include <set>

namespace {
std::unique_ptr<CefScopedLibraryLoader> g_library_loader;

std::once_flag g_accelerated_copy_once;
id<MTLDevice> g_accelerated_copy_device;
id<MTLCommandQueue> g_accelerated_copy_queue;

struct ExternalBeginFrameClock {
    int browser_id = 0;
    CGDirectDisplayID display_id = 0;
    CVDisplayLinkRef display_link = nullptr;
    bool diagnostics = false;
    uint64_t diagnostic_ticks = 0;
    std::chrono::steady_clock::time_point diagnostic_started_at;
};

std::mutex g_external_begin_frame_clocks_mu;
std::map<int, std::unique_ptr<ExternalBeginFrameClock>>
    g_external_begin_frame_clocks;

void EnsureAcceleratedCopyDevice() {
    std::call_once(g_accelerated_copy_once, [] {
        g_accelerated_copy_device = MTLCreateSystemDefaultDevice();
        g_accelerated_copy_queue =
            [g_accelerated_copy_device newCommandQueue];
    });
}

CGDirectDisplayID DisplayIdForWindow(void* window_handle) {
    NSWindow* window = (__bridge NSWindow*)window_handle;
    NSNumber* screen_number =
        window.screen.deviceDescription[@"NSScreenNumber"];
    return screen_number == nil
        ? CGMainDisplayID()
        : screen_number.unsignedIntValue;
}

CVReturn ExternalBeginFrameDisplayLinkCallback(
    CVDisplayLinkRef,
    const CVTimeStamp*,
    const CVTimeStamp*,
    CVOptionFlags,
    CVOptionFlags*,
    void* context) {
    auto* clock = static_cast<ExternalBeginFrameClock*>(context);
    auto browser = exclr8cef::GetOsrBrowser(clock->browser_id);
    if (browser) {
        browser->GetHost()->SendExternalBeginFrame();
    }

    if (clock->diagnostics) {
        ++clock->diagnostic_ticks;
        const auto now = std::chrono::steady_clock::now();
        const auto elapsed = now - clock->diagnostic_started_at;
        if (elapsed >= std::chrono::seconds(5)) {
            const double seconds =
                std::chrono::duration<double>(elapsed).count();
            std::fprintf(
                stderr,
                "[exclr8cef] CoreVideo display-link begin frames: %.1f/s.\n",
                clock->diagnostic_ticks / seconds);
            clock->diagnostic_ticks = 0;
            clock->diagnostic_started_at = now;
        }
    }
    return kCVReturnSuccess;
}

void StopExternalBeginFrameClock(
    std::unique_ptr<ExternalBeginFrameClock> clock) {
    if (!clock) {
        return;
    }
    CVDisplayLinkStop(clock->display_link);
    CVDisplayLinkRelease(clock->display_link);
}
}

@interface Exclr8CefApplication : NSApplication <CefAppProtocol> {
@private
    BOOL handlingSendEvent_;
}
@end

@implementation Exclr8CefApplication

- (BOOL)isHandlingSendEvent {
    return handlingSendEvent_;
}

- (void)setHandlingSendEvent:(BOOL)handlingSendEvent {
    handlingSendEvent_ = handlingSendEvent;
}

- (void)sendEvent:(NSEvent*)event {
    CefScopedSendingEvent sendingEventScoper;
    [super sendEvent:event];
}

- (void)terminate:(id)sender {
    CefQuitMessageLoop();
}

@end

extern "C" int excef_start_external_begin_frame_clock(
    int browser_id,
    void* window_handle) {
    const CGDirectDisplayID display_id = DisplayIdForWindow(window_handle);
    std::unique_ptr<ExternalBeginFrameClock> previous;
    {
        std::lock_guard<std::mutex> lock(g_external_begin_frame_clocks_mu);
        auto it = g_external_begin_frame_clocks.find(browser_id);
        if (it != g_external_begin_frame_clocks.end()) {
            if (it->second->display_id == display_id) {
                return 1;
            }
            previous = std::move(it->second);
            g_external_begin_frame_clocks.erase(it);
        }
    }
    StopExternalBeginFrameClock(std::move(previous));

    auto clock = std::make_unique<ExternalBeginFrameClock>();
    clock->browser_id = browser_id;
    clock->display_id = display_id;
    clock->diagnostics = std::getenv(
        "EXCLR8CEF_ACCELERATION_DIAGNOSTICS") != nullptr;
    clock->diagnostic_started_at = std::chrono::steady_clock::now();
    if (CVDisplayLinkCreateWithCGDisplay(
            display_id,
            &clock->display_link) != kCVReturnSuccess
        || clock->display_link == nullptr) {
        return 0;
    }
    if (CVDisplayLinkSetOutputCallback(
            clock->display_link,
            ExternalBeginFrameDisplayLinkCallback,
            clock.get()) != kCVReturnSuccess) {
        CVDisplayLinkRelease(clock->display_link);
        return 0;
    }
    if (CVDisplayLinkStart(clock->display_link) != kCVReturnSuccess) {
        CVDisplayLinkRelease(clock->display_link);
        return 0;
    }

    {
        std::lock_guard<std::mutex> lock(g_external_begin_frame_clocks_mu);
        g_external_begin_frame_clocks.emplace(browser_id, std::move(clock));
    }
    return 1;
}

extern "C" void excef_stop_external_begin_frame_clock(int browser_id) {
    std::unique_ptr<ExternalBeginFrameClock> clock;
    {
        std::lock_guard<std::mutex> lock(g_external_begin_frame_clocks_mu);
        auto it = g_external_begin_frame_clocks.find(browser_id);
        if (it == g_external_begin_frame_clocks.end()) {
            return;
        }
        clock = std::move(it->second);
        g_external_begin_frame_clocks.erase(it);
    }
    StopExternalBeginFrameClock(std::move(clock));
}

extern "C" int excef_execute_process(int argc, char** argv) {
    @autoreleasepool {
#if defined(CEF_USE_SANDBOX)
        // Must initialize the helper sandbox before loading the CEF framework.
        CefScopedSandboxContext sandbox_context;
        if (!sandbox_context.Initialize(argc, argv)) return 1;
#endif
        CefScopedLibraryLoader loader;
        if (!loader.LoadInHelper()) return 1;

        CefMainArgs main_args(argc, argv);
        // Pass our app to the helper subprocess so the renderer can
        // handle "Eval" IPC messages via CefRenderProcessHandler.
        CefRefPtr<exclr8cef::Exclr8CefApp> app = exclr8cef::EnsureApp();
        return CefExecuteProcess(main_args, app.get(), nullptr);
    }
}

extern "C" int excef_initialize(int argc, char** argv,
                                const char* subprocess_path) {
    @autoreleasepool {
        g_library_loader = std::make_unique<CefScopedLibraryLoader>();
        if (!g_library_loader->LoadInMain()) return 1;

        [Exclr8CefApplication sharedApplication];

        CefMainArgs main_args(argc, argv);
        CefSettings settings;
        if (subprocess_path && *subprocess_path) {
            CefString(&settings.browser_subprocess_path)
                .FromString(subprocess_path);  // UTF-8 — install dirs can be non-ASCII
        }
        exclr8cef::ApplyHostInitSettings(settings);

        CefRefPtr<exclr8cef::Exclr8CefApp> app = exclr8cef::EnsureApp();
        if (!CefInitialize(main_args, settings, app.get(), nullptr)) return 2;
        return 0;
    }
}

extern "C" void excef_run_message_loop(void) {
    @autoreleasepool {
        CefRunMessageLoop();
    }
}

extern "C" void excef_shutdown(void) {
    @autoreleasepool {
        CefShutdown();
        exclr8cef::ResetApp();
        exclr8cef::SetSchedulePumpCallback(nullptr);
        g_library_loader.reset();
    }
}

extern "C" int excef_copy_macos_accelerated_frame(
    const void* source_io_surface,
    int width,
    int height,
    int format,
    excef_macos_accelerated_frame* out_frame) {
    if (!source_io_surface || !out_frame || width <= 0 || height <= 0 ||
        (format != 0 && format != 1)) {
        return 0;
    }

    @autoreleasepool {
        *out_frame = {};
        EnsureAcceleratedCopyDevice();
        if (!g_accelerated_copy_device || !g_accelerated_copy_queue) {
            return 0;
        }

        IOSurfaceRef source =
            static_cast<IOSurfaceRef>(const_cast<void*>(source_io_surface));
        const size_t source_width = IOSurfaceGetWidth(source);
        const size_t source_height = IOSurfaceGetHeight(source);
        if (source_width < static_cast<size_t>(width) ||
            source_height < static_cast<size_t>(height)) {
            return 0;
        }

        const OSType surface_format = format == 0
            ? kCVPixelFormatType_32RGBA
            : kCVPixelFormatType_32BGRA;
        const MTLPixelFormat metal_format = format == 0
            ? MTLPixelFormatRGBA8Unorm
            : MTLPixelFormatBGRA8Unorm;
        const size_t bytes_per_row = IOSurfaceAlignProperty(
            kIOSurfaceBytesPerRow,
            static_cast<size_t>(width) * 4);
        NSDictionary* properties = @{
            (__bridge NSString*)kIOSurfaceWidth: @(width),
            (__bridge NSString*)kIOSurfaceHeight: @(height),
            (__bridge NSString*)kIOSurfaceBytesPerElement: @4,
            (__bridge NSString*)kIOSurfaceBytesPerRow: @(bytes_per_row),
            (__bridge NSString*)kIOSurfaceAllocSize:
                @(bytes_per_row * static_cast<size_t>(height)),
            (__bridge NSString*)kIOSurfacePixelFormat: @(surface_format),
        };
        IOSurfaceRef destination = IOSurfaceCreate(
            (__bridge CFDictionaryRef)properties);
        if (!destination) {
            return 0;
        }

        MTLTextureDescriptor* source_descriptor =
            [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:metal_format
                                                               width:width
                                                              height:height
                                                           mipmapped:NO];
        source_descriptor.usage = MTLTextureUsageShaderRead;
        MTLTextureDescriptor* destination_descriptor =
            [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:metal_format
                                                               width:width
                                                              height:height
                                                           mipmapped:NO];
        destination_descriptor.usage = MTLTextureUsageShaderRead;
        id<MTLTexture> source_texture =
            [g_accelerated_copy_device newTextureWithDescriptor:source_descriptor
                                                       iosurface:source
                                                           plane:0];
        id<MTLTexture> destination_texture =
            [g_accelerated_copy_device newTextureWithDescriptor:destination_descriptor
                                                       iosurface:destination
                                                           plane:0];
        if (!source_texture || !destination_texture) {
            CFRelease(destination);
            return 0;
        }

        id<MTLCommandBuffer> command_buffer =
            [g_accelerated_copy_queue commandBuffer];
        id<MTLBlitCommandEncoder> blit = [command_buffer blitCommandEncoder];
        if (!command_buffer || !blit) {
            CFRelease(destination);
            return 0;
        }

        [blit copyFromTexture:source_texture
                  sourceSlice:0
                  sourceLevel:0
                 sourceOrigin:MTLOriginMake(0, 0, 0)
                   sourceSize:MTLSizeMake(width, height, 1)
                    toTexture:destination_texture
             destinationSlice:0
             destinationLevel:0
            destinationOrigin:MTLOriginMake(0, 0, 0)];
        [blit endEncoding];
        [command_buffer commit];
        [command_buffer waitUntilCompleted];
        if (command_buffer.status != MTLCommandBufferStatusCompleted) {
            CFRelease(destination);
            return 0;
        }

        id<MTLSharedEvent> ready_event =
            [g_accelerated_copy_device newSharedEvent];
        if (!ready_event) {
            CFRelease(destination);
            return 0;
        }
        ready_event.signaledValue = 1;

        out_frame->io_surface = destination;
        out_frame->ready_event = (__bridge_retained void*)ready_event;
        out_frame->ready_value = 1;
        out_frame->width = width;
        out_frame->height = height;
        out_frame->format = format;
        return 1;
    }
}

extern "C" void excef_release_macos_accelerated_frame(
    excef_macos_accelerated_frame* frame) {
    if (!frame) {
        return;
    }
    if (frame->io_surface) {
        CFRelease(frame->io_surface);
    }
    if (frame->ready_event) {
        CFBridgingRelease(frame->ready_event);
    }
    *frame = {};
}

// ---- External message pump variant ---------------------------------------

// Common implementation for both external-pump variants. Set
// `windowless_rendering_enabled = enable_osr`.
static int initialize_with_pump_impl(int argc, char** argv,
                                     const char* subprocess_path,
                                     excef_schedule_pump_work_t cb,
                                     bool enable_osr) {
    @autoreleasepool {
        g_library_loader = std::make_unique<CefScopedLibraryLoader>();
        if (!g_library_loader->LoadInMain()) return 1;

        [Exclr8CefApplication sharedApplication];

        exclr8cef::SetSchedulePumpCallback(
            reinterpret_cast<exclr8cef::ScheduleMessagePumpWorkCallback>(cb));

        CefMainArgs main_args(argc, argv);
        CefSettings settings;
        settings.external_message_pump = true;
        settings.windowless_rendering_enabled = enable_osr ? 1 : 0;
        if (subprocess_path && *subprocess_path) {
            CefString(&settings.browser_subprocess_path).FromString(subprocess_path);  // UTF-8 — install dirs can be non-ASCII
        }
        exclr8cef::ApplyHostInitSettings(settings);

        CefRefPtr<exclr8cef::Exclr8CefApp> app = exclr8cef::EnsureApp();
        if (!CefInitialize(main_args, settings, app.get(), nullptr)) return 2;
        return 0;
    }
}

extern "C" int excef_initialize_offscreen(int argc, char** argv,
                                          const char* subprocess_path,
                                          excef_schedule_pump_work_t cb) {
    return initialize_with_pump_impl(argc, argv, subprocess_path, cb,
                                     /*enable_osr=*/true);
}

extern "C" int excef_initialize_external_pump(int argc, char** argv,
                                              const char* subprocess_path,
                                              excef_schedule_pump_work_t cb) {
    @autoreleasepool {
        g_library_loader = std::make_unique<CefScopedLibraryLoader>();
        if (!g_library_loader->LoadInMain()) return 1;

        [Exclr8CefApplication sharedApplication];

        // Register the schedule callback with the app handler so CEF's
        // OnScheduleMessagePumpWork forwards into managed code.
        exclr8cef::SetSchedulePumpCallback(
            reinterpret_cast<exclr8cef::ScheduleMessagePumpWorkCallback>(cb));

        CefMainArgs main_args(argc, argv);
        CefSettings settings;
        settings.external_message_pump = true;
        if (subprocess_path && *subprocess_path) {
            CefString(&settings.browser_subprocess_path).FromString(subprocess_path);  // UTF-8 — install dirs can be non-ASCII
        }
        exclr8cef::ApplyHostInitSettings(settings);

        CefRefPtr<exclr8cef::Exclr8CefApp> app = exclr8cef::EnsureApp();
        if (!CefInitialize(main_args, settings, app.get(), nullptr)) return 2;
        return 0;
    }
}

extern "C" void excef_do_message_loop_work(void) {
    @autoreleasepool {
        CefDoMessageLoopWork();
    }
}

// ---- Embedded browser ----------------------------------------------------

namespace {

// Guards the three host-view maps below. Under the external pump the CEF
// UI thread is the host's main thread, so these are single-threaded in
// practice — but the win.cc twin locks its equivalents and keeping the
// same discipline here costs nothing and survives pump-model changes.
std::mutex g_host_map_mu;
// Latest size requested for each host NSView. Avalonia may call
// ArrangeOverride before the browser is fully created (OnAfterCreated
// fires async on TID_UI); we record the desired size here so OnAfterCreated
// can sync to the actual final layout instead of the host's stale frame.
std::map<void*, NSSize> g_pending_sizes;
// Map host NSView pointer → browser id, for embedded-side resize lookup.
std::map<void*, int> g_host_to_id;
// Hosts whose excef_destroy_embedded_host arrived while their browser was
// still closing. The CFBridgingRelease is deferred to OnBeforeClose — CEF's
// child NSView is still parented inside the host until then, and releasing
// the host out from under it crashes the next paint.
std::set<void*> g_release_on_close;

// Thin subclass of the OSR handler used for embedded (windowed) browsers.
// Same handler surface (load / console / drag / permission / …) — only
// adds an OnAfterCreated hook that syncs the host NSView's subviews to
// the latest pending size and calls WasResized so Chromium lays out the
// page viewport correctly on first paint.
class EmbeddedOsrHandler : public exclr8cef::Exclr8CefOsrHandler {
public:
    EmbeddedOsrHandler(int id, int w, int h, void* host_view)
        : Exclr8CefOsrHandler(id, w, h, 1.0f, /*paint_cb=*/nullptr),
          host_view_(host_view) {}
    void OnAfterCreated(CefRefPtr<CefBrowser> browser) override {
        Exclr8CefOsrHandler::OnAfterCreated(browser);
        if (host_view_) {
            @autoreleasepool {
                NSView* host = (__bridge NSView*)host_view_;
                NSSize size = [host frame].size;
                {
                    std::lock_guard<std::mutex> lock(g_host_map_mu);
                    auto it = g_pending_sizes.find(host_view_);
                    if (it != g_pending_sizes.end()) size = it->second;
                }
                for (NSView* sub in [host subviews]) {
                    [sub setFrame:NSMakeRect(0, 0, size.width, size.height)];
                }
            }
            browser->GetHost()->WasResized();
        }
    }
    void OnBeforeClose(CefRefPtr<CefBrowser> browser) override {
        bool release_host = false;
        if (host_view_) {
            std::lock_guard<std::mutex> lock(g_host_map_mu);
            g_host_to_id.erase(host_view_);
            g_pending_sizes.erase(host_view_);
            release_host = g_release_on_close.erase(host_view_) > 0;
        }
        if (release_host) {
            // The host's destroy call arrived while we were still closing;
            // now that CEF's child NSView is gone, balance the
            // CFBridgingRetain from excef_create_embedded_host.
            CFBridgingRelease(host_view_);
        }
        Exclr8CefOsrHandler::OnBeforeClose(browser);
    }
private:
    void* host_view_;
    IMPLEMENT_REFCOUNTING(EmbeddedOsrHandler);
};

}  // namespace

// Flipped NSView subclass for the embedded host. macOS NSViews default
// to bottom-left coordinate origin, while Avalonia (and every other
// modern UI framework) uses top-left. When Avalonia's NativeControlHost
// repositions this NSView during layout, it has to flip Y against the
// parent's height — and any layout pass that repaints during a drag
// (e.g. dragging a GridSplitter that recomputes column widths) can
// accumulate a 1-pixel Y rounding error per pass, which manifests as
// "the browser content slowly drifts upward as I resize the splitter."
// Returning YES from -isFlipped makes the view's coordinate system
// top-left, eliminating the flip math in the host entirely.
@interface Exclr8FlippedHostView : NSView
@end
@implementation Exclr8FlippedHostView
- (BOOL)isFlipped { return YES; }
@end

// Phase 1: create an empty host NSView that the UI framework will parent.
// Do NOT set autoresizingMask — Avalonia's NativeControlHost manages the
// NSView's frame explicitly via TryUpdateNativeControlPosition, and an
// autoresizing mask makes the NSView stretch over the rest of the window.
extern "C" void* excef_create_embedded_host(int width, int height) {
    @autoreleasepool {
        NSView* host = [[Exclr8FlippedHostView alloc]
            initWithFrame:NSMakeRect(0, 0, width, height)];
        return (void*)CFBridgingRetain(host);
    }
}

// Parent-aware variant. On macOS the parent is unused — Avalonia's
// NativeControlHost parents NSViews itself — but the export must exist on
// every platform so the managed call site is uniform. On Windows the
// parent HWND is REQUIRED (a WS_CHILD window cannot be created parentless).
extern "C" void* excef_create_embedded_host_in_parent(void* /*parent*/,
                                                      int width, int height) {
    return excef_create_embedded_host(width, height);
}

// Balance the CFBridgingRetain from excef_create_embedded_host. If the
// browser attached to this host is still closing (async), the release is
// deferred to its OnBeforeClose; otherwise it happens immediately.
extern "C" void excef_destroy_embedded_host(void* host_view_ptr) {
    if (!host_view_ptr) return;
    {
        std::lock_guard<std::mutex> lock(g_host_map_mu);
        if (g_host_to_id.count(host_view_ptr)) {
            g_release_on_close.insert(host_view_ptr);
            return;
        }
        g_pending_sizes.erase(host_view_ptr);
    }
    CFBridgingRelease(host_view_ptr);
}

// Phase 2: attach a CEF browser to a previously-created host NSView.
// Call this AFTER the UI framework has parented the NSView, so Chromium
// can read the correct backingScaleFactor at browser-creation time.
//
// `background_color` (ARGB) sets the colour Chromium fills the
// render target with before any HTML paints. CEF's default is opaque
// white, which appears as a visible "white flash" on the embedded
// NSView in the gap between attach and first page paint. Pass 0 to
// keep CEF's default. Alpha must be either 0x00 (transparent) or
// 0xFF (opaque) — Chromium ignores in-between values.
extern "C" int excef_attach_embedded_browser_in_context_v2(void* host_view_ptr,
                                                             int width, int height,
                                                             const char* url,
                                                             int context_handle,
                                                             uint32_t background_color) {
    if (!host_view_ptr || !url) return 0;
    @autoreleasepool {
        NSView* host = (__bridge NSView*)host_view_ptr;

        CefWindowInfo window_info;
        window_info.SetAsChild((__bridge void*)host,
                               CefRect(0, 0, width, height));
        window_info.runtime_style = CEF_RUNTIME_STYLE_ALLOY;

        int id = exclr8cef::AllocateBrowserId();
        CefRefPtr<EmbeddedOsrHandler> handler(
            new EmbeddedOsrHandler(id, width, height, host_view_ptr));
        exclr8cef::RegisterOsrHandler(id, handler);
        {
            std::lock_guard<std::mutex> lock(g_host_map_mu);
            g_host_to_id[host_view_ptr] = id;
        }

        CefRefPtr<CefRequestContext> ctx = exclr8cef::ResolveContext(context_handle);
        // Treat handle=0 → global as "no explicit context" so CEF picks
        // its default — same shape as request_context=nullptr.
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
            g_host_to_id.erase(host_view_ptr);
            g_pending_sizes.erase(host_view_ptr);
            return 0;
        }
        return id;
    }
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

extern "C" void* excef_create_browser_view_in_context(int width, int height,
                                                       const char* url,
                                                       int* out_browser_id,
                                                       int context_handle) {
    if (out_browser_id) *out_browser_id = 0;
    if (!url) return nullptr;

    @autoreleasepool {
        // Same flipped subclass as excef_create_embedded_host so both
        // embedded paths behave identically during host-driven resizes.
        NSView* host = [[Exclr8FlippedHostView alloc]
            initWithFrame:NSMakeRect(0, 0, width, height)];
        [host setAutoresizingMask:NSViewWidthSizable | NSViewHeightSizable];

        CefWindowInfo window_info;
        window_info.SetAsChild((__bridge void*)host,
                               CefRect(0, 0, width, height));
        window_info.runtime_style = CEF_RUNTIME_STYLE_ALLOY;

        // Reuse Exclr8CefOsrHandler with paint_cb=null so all the per-
        // browser event surface (load / console / drag / permission / …)
        // fires through the same trampolines as OSR — but CEF won't OSR-
        // render because GetRenderHandler returns null when paint_cb_ is
        // null. The handler is registered keyed by id so existing event
        // trampolines find it.
        int id = exclr8cef::AllocateBrowserId();
        CefRefPtr<EmbeddedOsrHandler> handler(
            new EmbeddedOsrHandler(id, width, height, (__bridge void*)host));
        exclr8cef::RegisterOsrHandler(id, handler);
        {
            std::lock_guard<std::mutex> lock(g_host_map_mu);
            g_host_to_id[(__bridge void*)host] = id;
        }

        CefRefPtr<CefRequestContext> ctx = exclr8cef::ResolveContext(context_handle);
        CefRefPtr<CefRequestContext> pass = context_handle == 0 ? nullptr : ctx;

        CefBrowserSettings browser_settings;
        bool ok = CefBrowserHost::CreateBrowser(
            window_info, handler.get(), url, browser_settings,
            exclr8cef::CreateBrowserExtraInfo(), pass);

        if (!ok) {
            exclr8cef::UnregisterOsrHandler(id);
            std::lock_guard<std::mutex> lock(g_host_map_mu);
            g_host_to_id.erase((__bridge void*)host);
            return nullptr;
        }

        if (out_browser_id) *out_browser_id = id;
        return (void*)CFBridgingRetain(host);
    }
}

extern "C" void* excef_create_browser_view(int width, int height,
                                           const char* url,
                                           int* out_browser_id) {
    return excef_create_browser_view_in_context(width, height, url, out_browser_id, 0);
}

// Resize a previously-created embedded browser view. Sets the host's
// frame, then walks its direct subviews (CEF inserts the browser NSView
// there) so they track, then calls CefBrowserHost::WasResized() on the
// backing browser so Chromium re-lays out the page viewport. Without
// WasResized(), the page renders at its original viewport and overflows
// when the window grows.
// Hide/show the host NSView in place. Hosts that need to keep the
// CefBrowser alive — but stop the NSView painting on top of sibling
// Avalonia content (e.g. tab-switch scenarios where the embedded
// browser would otherwise leak through the new foreground tab)
// flip this. CEF's WasHidden() is also called to let Chromium throttle
// rendering work while we're hidden.
extern "C" void excef_set_embedded_host_hidden(void* host_view_ptr, int hidden) {
    if (!host_view_ptr) return;
    @autoreleasepool {
        NSView* host = (__bridge NSView*)host_view_ptr;
        BOOL hide = (hidden != 0);
        if ([host isHidden] != hide) [host setHidden:hide];
        int browser_id = 0;
        {
            std::lock_guard<std::mutex> lock(g_host_map_mu);
            auto it = g_host_to_id.find(host_view_ptr);
            if (it != g_host_to_id.end()) browser_id = it->second;
        }
        if (browser_id != 0) {
            auto handler = exclr8cef::LookupOsrHandler(browser_id);
            if (handler && handler->browser()) {
                handler->browser()->GetHost()->WasHidden(hide);
            }
        }
    }
}

extern "C" void excef_resize_browser_view(void* host_view_ptr,
                                          int width, int height) {
    if (!host_view_ptr || width <= 0 || height <= 0) return;
    @autoreleasepool {
        // Always record the latest desired size so OnAfterCreated can sync
        // to it if the browser isn't ready yet (Avalonia's ArrangeOverride
        // often fires before CEF finishes async browser creation).
        int browser_id = 0;
        {
            std::lock_guard<std::mutex> lock(g_host_map_mu);
            g_pending_sizes[host_view_ptr] = NSMakeSize(width, height);
            auto it = g_host_to_id.find(host_view_ptr);
            if (it != g_host_to_id.end()) browser_id = it->second;
        }

        // Look up the embedder's browser FIRST so we can flush CEF's
        // origin cache BEFORE resizing the host. There's a documented
        // CEF macOS bug — content drifts upward when the container
        // NSView is resized directly (as opposed to via a window
        // resize), because CEF's internal scroll-view origin tracking
        // goes stale. NotifyMoveOrResizeStarted() clears that cache;
        // calling it before setFrameSize and WasResized() afterwards
        // gives CEF a clean origin to compose from.
        //   ref: https://magpcss.org/ceforum/viewtopic.php?f=6&t=16341
        CefRefPtr<exclr8cef::Exclr8CefOsrHandler> handler;
        if (browser_id != 0) {
            handler = exclr8cef::LookupOsrHandler(browser_id);
        }
        if (handler && handler->browser()) {
            handler->browser()->GetHost()->NotifyMoveOrResizeStarted();
        }

        NSView* host = (__bridge NSView*)host_view_ptr;
        NSRect hostFrame = [host frame];
        if ((int)hostFrame.size.width != width || (int)hostFrame.size.height != height) {
            [host setFrameSize:NSMakeSize(width, height)];
        }
        for (NSView* sub in [host subviews]) {
            [sub setFrame:NSMakeRect(0, 0, width, height)];
        }
        if (handler && handler->browser()) {
            handler->browser()->GetHost()->WasResized();
        }
    }
}
