// CefApp implementation. Implements both browser-process and
// render-process roles so JS-eval IPC can flow through the same instance
// in either process type.

#ifndef EXCLR8CEF_APP_H_
#define EXCLR8CEF_APP_H_

#include <cstdint>
#include <string>
#include <vector>

#include "include/cef_app.h"
#include "include/cef_render_process_handler.h"

namespace exclr8cef {

class Exclr8CefApp : public CefApp,
                     public CefBrowserProcessHandler,
                     public CefRenderProcessHandler {
public:
    Exclr8CefApp();

    // CefApp
    CefRefPtr<CefBrowserProcessHandler> GetBrowserProcessHandler() override {
        return this;
    }
    CefRefPtr<CefRenderProcessHandler> GetRenderProcessHandler() override {
        return this;
    }

    void OnBeforeCommandLineProcessing(
        const CefString& process_type,
        CefRefPtr<CefCommandLine> command_line) override;

    void OnRegisterCustomSchemes(
        CefRawPtr<CefSchemeRegistrar> registrar) override;

    void OnBrowserCreated(CefRefPtr<CefBrowser> browser,
                          CefRefPtr<CefDictionaryValue> extra_info) override;

    void OnBrowserDestroyed(CefRefPtr<CefBrowser> browser) override;

    // CefBrowserProcessHandler
    void OnContextInitialized() override;
    void OnScheduleMessagePumpWork(int64_t delay_ms) override;

    // CefRenderProcessHandler — used in the renderer subprocess.
    bool OnProcessMessageReceived(
        CefRefPtr<CefBrowser> browser,
        CefRefPtr<CefFrame> frame,
        CefProcessId source_process,
        CefRefPtr<CefProcessMessage> message) override;

    void OnContextCreated(CefRefPtr<CefBrowser> browser,
                          CefRefPtr<CefFrame> frame,
                          CefRefPtr<CefV8Context> context) override;

    void OnContextReleased(CefRefPtr<CefBrowser> browser,
                           CefRefPtr<CefFrame> frame,
                           CefRefPtr<CefV8Context> context) override;

private:
    IMPLEMENT_REFCOUNTING(Exclr8CefApp);
    DISALLOW_COPY_AND_ASSIGN(Exclr8CefApp);
};

CefRefPtr<Exclr8CefApp> EnsureApp();
void ResetApp();

void SetPendingBrowserUrl(const std::string& url);

typedef void (*ScheduleMessagePumpWorkCallback)(int64_t delay_ms);
void SetSchedulePumpCallback(ScheduleMessagePumpWorkCallback cb);

// Overlay host-provided init settings onto a CefSettings instance. The
// host sets these via excef_set_init_settings (in exclr8cef.cc) before
// calling any init function. Each init path (Windows/Linux + macOS) calls
// this helper after setting the shim-controlled fields.
void ApplyHostInitSettings(CefSettings& settings);

// True only when the host explicitly opts into the privileged page-JS bridge
// through CefSettings.EnableJavascriptBridge.
bool JavascriptBridgeEnabled();

// Per-browser metadata propagated by CEF into the renderer process. Returning
// nullptr is intentional for the default bridge-disabled case.
CefRefPtr<CefDictionaryValue> CreateBrowserExtraInfo();

// Custom-scheme registry shared with exclr8cef.cc (where the C ABI lives).
void AddCustomScheme(const std::string& name, int options);
std::vector<std::string> GetRegisteredSchemeNames();
// Extra Chromium switches set by the host via excef_add_command_line_switch.
void AddExtraCommandLineSwitch(const std::string& name, const std::string& value);
// Hydrate the scheme registry from the EXCLR8CEF_SCHEMES env var. The
// browser process exports the env var (via AddCustomScheme); subprocesses
// (renderer/GPU/utility/network) inherit it on spawn and call this helper
// during OnBeforeCommandLineProcessing — Chromium otherwise filters custom
// cmdline switches before passing them to children.
void LoadSchemesFromEnv();
// Implemented in exclr8cef_osr.cc; called from OnContextInitialized to hook
// our resource-handler factory to every registered custom scheme.
void RegisterAllSchemeFactories();

}  // namespace exclr8cef

#endif  // EXCLR8CEF_APP_H_
