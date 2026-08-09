#include "exclr8cef_app.h"

#include <atomic>
#include <cstdlib>
#include <map>
#include <mutex>
#include <set>
#include <string>
#include <vector>

#include "include/base/cef_callback.h"
#include "include/cef_browser.h"
#include "include/cef_command_line.h"
#include "include/cef_frame.h"
#include "include/cef_process_message.h"
#include "include/cef_scheme.h"
#include "include/cef_v8.h"
#include "include/views/cef_browser_view.h"
#include "include/views/cef_browser_view_delegate.h"
#include "include/views/cef_window.h"
#include "include/views/cef_window_delegate.h"
#include "include/wrapper/cef_closure_task.h"
#include "include/wrapper/cef_helpers.h"

#include "exclr8cef_client.h"

namespace exclr8cef {

namespace {

CefRefPtr<Exclr8CefApp> g_app;

constexpr char kJavascriptBridgeExtraInfoKey[] =
    "exclr8cef.enable_javascript_bridge";

// Renderer-process browser ids whose browser-process creator explicitly
// enabled the privileged JavaScript bridge. CEF transports this state as
// browser extra_info, so no user-controlled command-line switch is needed.
std::set<int> g_javascript_bridge_browsers;

// Renderer-side pending Promise registry, keyed by request id. When the
// browser process replies via CefProcessMessage("JsInvokeReply") we look
// up the context + promise and resolve / reject.
struct PendingJsInvoke {
    CefRefPtr<CefV8Context> context;
    CefRefPtr<CefV8Value> promise;
};
std::map<int, PendingJsInvoke> g_pending_invokes;
std::mutex g_pending_invokes_mu;
std::atomic<int> g_next_invoke_id{1};

// V8 handler installed in every renderer-side V8 context as
// `window.exclr8cef.invoke(method, argsJson)`. Returns a Promise that
// resolves with whatever the host replies (or rejects if the host calls
// JsInvokeArgs.ReplyError). Forwards calls to the browser process via
// CefProcessMessage("JsInvoke") with [request_id, method, argsJson].
class JsBridgeV8Handler : public CefV8Handler {
public:
    explicit JsBridgeV8Handler(CefRefPtr<CefFrame> frame) : frame_(std::move(frame)) {}

    bool Execute(const CefString& name,
                 CefRefPtr<CefV8Value> /*object*/,
                 const CefV8ValueList& args,
                 CefRefPtr<CefV8Value>& retval,
                 CefString& exception) override {
        if (name != "invoke") return false;
        if (args.empty() || !args[0]->IsString()) {
            exception = "exclr8cef.invoke(method[, argsJson]) — method required";
            return true;
        }
        std::string method = args[0]->GetStringValue().ToString();
        std::string argsJson;
        if (args.size() >= 2 && args[1]->IsString()) {
            argsJson = args[1]->GetStringValue().ToString();
        }

        int request_id = g_next_invoke_id.fetch_add(1, std::memory_order_relaxed);
        auto promise = CefV8Value::CreatePromise();
        {
            std::lock_guard<std::mutex> lock(g_pending_invokes_mu);
            g_pending_invokes[request_id] = {CefV8Context::GetCurrentContext(), promise};
        }

        auto msg = CefProcessMessage::Create("JsInvoke");
        auto a = msg->GetArgumentList();
        a->SetInt(0, request_id);
        a->SetString(1, method);
        a->SetString(2, argsJson);
        if (frame_) frame_->SendProcessMessage(PID_BROWSER, msg);

        retval = promise;
        return true;
    }

private:
    CefRefPtr<CefFrame> frame_;
    IMPLEMENT_REFCOUNTING(JsBridgeV8Handler);
};
std::vector<std::string> g_pending_urls;
bool g_context_initialized = false;
ScheduleMessagePumpWorkCallback g_schedule_pump_cb = nullptr;

// Custom schemes registered via excef_register_custom_scheme. Populated
// before init; consumed in OnRegisterCustomSchemes and OnContextInitialized.
struct CustomSchemeEntry {
    std::string name;
    int options;  // OR-combined CEF_SCHEME_OPTION_*
};
std::vector<CustomSchemeEntry> g_custom_schemes;
std::mutex g_custom_schemes_mu;

// Extra Chromium switches the host wants applied via
// OnBeforeCommandLineProcessing. Set via excef_add_command_line_switch
// before init. {name, value} — value may be empty for boolean switches.
struct ExtraSwitch { std::string name; std::string value; };
std::vector<ExtraSwitch> g_extra_switches;
std::mutex g_extra_switches_mu;

void ApplyExtraSwitches(CefRefPtr<CefCommandLine> command_line) {
    std::lock_guard<std::mutex> lock(g_extra_switches_mu);
    for (const auto& s : g_extra_switches) {
        if (command_line->HasSwitch(s.name)) continue;
        if (s.value.empty()) command_line->AppendSwitch(s.name);
        else                 command_line->AppendSwitchWithValue(s.name, s.value);
    }
}
}  // namespace

void AddExtraCommandLineSwitch(const std::string& name, const std::string& value) {
    std::lock_guard<std::mutex> lock(g_extra_switches_mu);
    g_extra_switches.push_back({name, value});
}

namespace {

class Exclr8CefWindowDelegate : public CefWindowDelegate {
public:
    Exclr8CefWindowDelegate(CefRefPtr<CefBrowserView> browser_view,
                            cef_runtime_style_t runtime_style,
                            cef_show_state_t initial_show_state)
        : browser_view_(browser_view),
          runtime_style_(runtime_style),
          initial_show_state_(initial_show_state) {}

    Exclr8CefWindowDelegate(const Exclr8CefWindowDelegate&) = delete;
    Exclr8CefWindowDelegate& operator=(const Exclr8CefWindowDelegate&) = delete;

    void OnWindowCreated(CefRefPtr<CefWindow> window) override {
        window->AddChildView(browser_view_);
        if (initial_show_state_ != CEF_SHOW_STATE_HIDDEN) {
            window->Show();
        }
    }

    void OnWindowDestroyed(CefRefPtr<CefWindow> /*window*/) override {
        browser_view_ = nullptr;
    }

    bool CanClose(CefRefPtr<CefWindow> /*window*/) override {
        if (browser_view_) {
            CefRefPtr<CefBrowser> browser = browser_view_->GetBrowser();
            if (browser) return browser->GetHost()->TryCloseBrowser();
        }
        return true;
    }

    CefSize GetPreferredSize(CefRefPtr<CefView> /*view*/) override {
        return CefSize(1024, 768);
    }

    cef_show_state_t GetInitialShowState(CefRefPtr<CefWindow> /*window*/) override {
        return initial_show_state_;
    }

    cef_runtime_style_t GetWindowRuntimeStyle() override {
        return runtime_style_;
    }

private:
    CefRefPtr<CefBrowserView> browser_view_;
    const cef_runtime_style_t runtime_style_;
    const cef_show_state_t initial_show_state_;

    IMPLEMENT_REFCOUNTING(Exclr8CefWindowDelegate);
};

class Exclr8CefBrowserViewDelegate : public CefBrowserViewDelegate {
public:
    explicit Exclr8CefBrowserViewDelegate(cef_runtime_style_t runtime_style)
        : runtime_style_(runtime_style) {}

    Exclr8CefBrowserViewDelegate(const Exclr8CefBrowserViewDelegate&) = delete;
    Exclr8CefBrowserViewDelegate& operator=(const Exclr8CefBrowserViewDelegate&) = delete;

    bool OnPopupBrowserViewCreated(CefRefPtr<CefBrowserView> /*browser_view*/,
                                   CefRefPtr<CefBrowserView> popup_browser_view,
                                   bool /*is_devtools*/) override {
        CefWindow::CreateTopLevelWindow(new Exclr8CefWindowDelegate(
            popup_browser_view, runtime_style_, CEF_SHOW_STATE_NORMAL));
        return true;
    }

    cef_runtime_style_t GetBrowserRuntimeStyle() override {
        return runtime_style_;
    }

private:
    const cef_runtime_style_t runtime_style_;

    IMPLEMENT_REFCOUNTING(Exclr8CefBrowserViewDelegate);
};

void CreateBrowserOnUI(const std::string& url) {
    CEF_REQUIRE_UI_THREAD();

    cef_runtime_style_t runtime_style = CEF_RUNTIME_STYLE_DEFAULT;

    CefRefPtr<Exclr8CefClient> client(new Exclr8CefClient());
    CefBrowserSettings browser_settings;

    CefRefPtr<CefBrowserView> browser_view = CefBrowserView::CreateBrowserView(
        client, url, browser_settings,
        CreateBrowserExtraInfo(),
        /*request_context=*/nullptr,
        new Exclr8CefBrowserViewDelegate(runtime_style));

    if (!browser_view) return;

    CefWindow::CreateTopLevelWindow(new Exclr8CefWindowDelegate(
        browser_view, runtime_style, CEF_SHOW_STATE_NORMAL));
}

}  // namespace

Exclr8CefApp::Exclr8CefApp() = default;

void Exclr8CefApp::OnBeforeCommandLineProcessing(
    const CefString& process_type,
    CefRefPtr<CefCommandLine> command_line) {
    if (process_type.empty()) {
        // command_line_args_disabled starts this object empty. Apply only
        // switches explicitly requested through the trusted host API; there
        // are no permissive shim defaults.
        ApplyExtraSwitches(command_line);
        return;
    }

    // Subprocess (renderer / GPU / utility / network). Inherit scheme
    // registrations from the env var the browser process exported, so the
    // OnRegisterCustomSchemes call that fires next has them.
    LoadSchemesFromEnv();
}

CefRefPtr<CefDictionaryValue> CreateBrowserExtraInfo() {
    if (!JavascriptBridgeEnabled()) return nullptr;

    auto extra_info = CefDictionaryValue::Create();
    extra_info->SetBool(kJavascriptBridgeExtraInfoKey, true);
    return extra_info;
}

void Exclr8CefApp::OnBrowserCreated(
    CefRefPtr<CefBrowser> browser,
    CefRefPtr<CefDictionaryValue> extra_info) {
    if (browser && extra_info &&
        extra_info->GetBool(kJavascriptBridgeExtraInfoKey)) {
        g_javascript_bridge_browsers.insert(browser->GetIdentifier());
    }
}

void Exclr8CefApp::OnBrowserDestroyed(CefRefPtr<CefBrowser> browser) {
    if (browser) {
        g_javascript_bridge_browsers.erase(browser->GetIdentifier());
    }
}

void Exclr8CefApp::OnRegisterCustomSchemes(
    CefRawPtr<CefSchemeRegistrar> registrar) {
    std::vector<CustomSchemeEntry> snapshot;
    {
        std::lock_guard<std::mutex> lock(g_custom_schemes_mu);
        snapshot = g_custom_schemes;
    }
    for (const auto& entry : snapshot) {
        registrar->AddCustomScheme(entry.name, entry.options);
    }
}

// Storage accessors for the C ABI / scheme factory side. We expose these
// so the per-process scheme-factory registration in exclr8cef.cc (which
// hooks into OnContextInitialized) can read what the host registered.
std::vector<std::string> GetRegisteredSchemeNames() {
    std::lock_guard<std::mutex> lock(g_custom_schemes_mu);
    std::vector<std::string> names;
    names.reserve(g_custom_schemes.size());
    for (const auto& e : g_custom_schemes) names.push_back(e.name);
    return names;
}

void AddCustomScheme(const std::string& name, int options) {
    {
        std::lock_guard<std::mutex> lock(g_custom_schemes_mu);
        for (auto& e : g_custom_schemes) {
            if (e.name == name) { e.options = options; return; }
        }
        g_custom_schemes.push_back({name, options});
    }

    // Also export the full list via an env var so subprocesses (spawned
    // by Chromium after init) can re-populate their own g_custom_schemes
    // and register the schemes locally. Custom cmdline switches we
    // AppendSwitchWithValue in the browser process do NOT auto-propagate
    // — Chromium filters them — so env var is the portable route.
    std::string serialized;
    {
        std::lock_guard<std::mutex> lock(g_custom_schemes_mu);
        for (size_t i = 0; i < g_custom_schemes.size(); ++i) {
            if (i) serialized += ',';
            serialized += g_custom_schemes[i].name;
            serialized += ':';
            serialized += std::to_string(g_custom_schemes[i].options);
        }
    }
#if defined(_WIN32)
    // _putenv_s is the MSVC-correct equivalent of POSIX setenv. The empty
    // string clears (vs. POSIX's separate unsetenv) but we always have a
    // non-empty serialized list when we get here.
    _putenv_s("EXCLR8CEF_SCHEMES", serialized.c_str());
#else
    setenv("EXCLR8CEF_SCHEMES", serialized.c_str(), /*overwrite=*/1);
#endif
}

// Load the scheme list from the environment (used by subprocesses, which
// don't see the host's AddCustomScheme calls but inherit the env var from
// the browser process that spawned them).
void LoadSchemesFromEnv() {
    const char* env = std::getenv("EXCLR8CEF_SCHEMES");
    if (!env || !*env) return;
    std::string val(env);
    size_t start = 0;
    for (size_t i = 0; i <= val.size(); ++i) {
        if (i == val.size() || val[i] == ',') {
            auto chunk = val.substr(start, i - start);
            auto colon = chunk.find(':');
            if (colon != std::string::npos) {
                std::string name = chunk.substr(0, colon);
                int opts = std::atoi(chunk.substr(colon + 1).c_str());
                std::lock_guard<std::mutex> lock(g_custom_schemes_mu);
                bool found = false;
                for (auto& e : g_custom_schemes) {
                    if (e.name == name) { e.options = opts; found = true; break; }
                }
                if (!found) g_custom_schemes.push_back({name, opts});
            }
            start = i + 1;
        }
    }
}

void Exclr8CefApp::OnContextInitialized() {
    CEF_REQUIRE_UI_THREAD();
    g_context_initialized = true;

    // Hook custom-scheme factories now that the global context is up.
    // OnRegisterCustomSchemes ran earlier (per-process); this binds the
    // resource-handler factory to those scheme names browser-side.
    RegisterAllSchemeFactories();

    for (const auto& url : g_pending_urls) {
        CreateBrowserOnUI(url);
    }
    g_pending_urls.clear();
}

void Exclr8CefApp::OnScheduleMessagePumpWork(int64_t delay_ms) {
    if (g_schedule_pump_cb) {
        g_schedule_pump_cb(delay_ms);
    }
}

void Exclr8CefApp::OnContextCreated(CefRefPtr<CefBrowser> browser,
                                     CefRefPtr<CefFrame> frame,
                                     CefRefPtr<CefV8Context> context) {
    if (!browser || !frame || !frame->IsMain() ||
        !g_javascript_bridge_browsers.contains(browser->GetIdentifier())) {
        return;
    }

    // Renderer side. Install the privileged bridge in the main frame only;
    // untrusted cross-origin iframes must not inherit host IPC capability.
    auto global = context->GetGlobal();
    CefRefPtr<JsBridgeV8Handler> handler = new JsBridgeV8Handler(frame);
    auto invokeFn = CefV8Value::CreateFunction("invoke", handler);
    auto ns = CefV8Value::CreateObject(nullptr, nullptr);
    ns->SetValue("invoke", invokeFn, V8_PROPERTY_ATTRIBUTE_READONLY);
    global->SetValue("exclr8cef", ns, V8_PROPERTY_ATTRIBUTE_READONLY);
}

void Exclr8CefApp::OnContextReleased(CefRefPtr<CefBrowser> /*browser*/,
                                      CefRefPtr<CefFrame> /*frame*/,
                                      CefRefPtr<CefV8Context> context) {
    // Drop pending invoke entries whose context is going away — without
    // this, every navigation with an unanswered exclr8cef.invoke() pins a
    // dead CefV8Context (and its Promise) for the renderer's lifetime.
    std::lock_guard<std::mutex> lock(g_pending_invokes_mu);
    for (auto it = g_pending_invokes.begin(); it != g_pending_invokes.end(); ) {
        if (it->second.context && it->second.context->IsSame(context)) {
            it = g_pending_invokes.erase(it);
        } else {
            ++it;
        }
    }
}

bool Exclr8CefApp::OnProcessMessageReceived(
    CefRefPtr<CefBrowser> browser,
    CefRefPtr<CefFrame> frame,
    CefProcessId source_process,
    CefRefPtr<CefProcessMessage> message) {
    // Renderer-process side: reply path for window.exclr8cef.invoke(...)
    // — look up the pending Promise by request id, resolve or reject.
    if (message->GetName() == "JsInvokeReply") {
        auto args = message->GetArgumentList();
        int request_id = args->GetInt(0);
        bool ok = args->GetBool(1);
        std::string result_json = args->GetString(2).ToString();

        PendingJsInvoke entry;
        {
            std::lock_guard<std::mutex> lock(g_pending_invokes_mu);
            auto it = g_pending_invokes.find(request_id);
            if (it == g_pending_invokes.end()) return true;
            entry = it->second;
            g_pending_invokes.erase(it);
        }
        if (!entry.context || !entry.promise) return true;
        if (!entry.context->Enter()) return true;

        // JSON.parse the reply into a V8 value. On parse failure, fall
        // through to the raw string.
        CefRefPtr<CefV8Value> value;
        auto global = entry.context->GetGlobal();
        auto json_obj = global->GetValue("JSON");
        if (json_obj && json_obj->IsObject()) {
            auto parse = json_obj->GetValue("parse");
            if (parse && parse->IsFunction()) {
                CefV8ValueList sargs;
                sargs.push_back(CefV8Value::CreateString(result_json));
                value = parse->ExecuteFunction(json_obj, sargs);
            }
        }
        if (!value) value = CefV8Value::CreateString(result_json);

        if (ok) entry.promise->ResolvePromise(value);
        else entry.promise->RejectPromise(result_json);

        entry.context->Exit();
        return true;
    }

    // Renderer-process side: handle "Eval" requests sent from the browser
    // process. Run the JS in the frame's V8 context, JSON-serialize the
    // result, and send back via "EvalResult".
    if (message->GetName() != "Eval") return false;

    auto args = message->GetArgumentList();
    int request_id = args->GetInt(0);
    CefString code = args->GetString(1);

    auto context = frame->GetV8Context();
    if (!context || !context->Enter()) {
        auto resp = CefProcessMessage::Create("EvalResult");
        auto rargs = resp->GetArgumentList();
        rargs->SetInt(0, request_id);
        rargs->SetBool(1, false);
        rargs->SetString(2, "no v8 context");
        frame->SendProcessMessage(PID_BROWSER, resp);
        return true;
    }

    CefRefPtr<CefV8Value> retval;
    CefRefPtr<CefV8Exception> exception;
    bool ok = context->Eval(code, "exclr8cef:eval", 0, retval, exception);

    std::string serialized;
    std::string error;
    if (!ok) {
        error = exception ? exception->GetMessage().ToString()
                          : std::string("eval failed");
    } else if (retval) {
        // Serialize via JSON.stringify(retval). If undefined, use 'null'.
        auto global = context->GetGlobal();
        auto json_obj = global->GetValue("JSON");
        if (json_obj && json_obj->IsObject()) {
            auto stringify = json_obj->GetValue("stringify");
            if (stringify && stringify->IsFunction()) {
                CefV8ValueList sargs;
                sargs.push_back(retval);
                auto json_result = stringify->ExecuteFunction(json_obj, sargs);
                if (json_result && json_result->IsString()) {
                    serialized = json_result->GetStringValue().ToString();
                }
            }
        }
        if (serialized.empty() && retval->IsString()) {
            serialized = "\"" + retval->GetStringValue().ToString() + "\"";
        }
        if (serialized.empty()) serialized = "null";
    } else {
        serialized = "null";
    }

    context->Exit();

    auto resp = CefProcessMessage::Create("EvalResult");
    auto rargs = resp->GetArgumentList();
    rargs->SetInt(0, request_id);
    rargs->SetBool(1, ok);
    rargs->SetString(2, ok ? serialized : error);
    frame->SendProcessMessage(PID_BROWSER, resp);
    return true;
}

CefRefPtr<Exclr8CefApp> EnsureApp() {
    if (!g_app) g_app = new Exclr8CefApp();
    return g_app;
}

void ResetApp() {
    g_app = nullptr;
    g_context_initialized = false;
    g_pending_urls.clear();
    g_javascript_bridge_browsers.clear();
}

void SetPendingBrowserUrl(const std::string& url) {
    if (!g_context_initialized) {
        g_pending_urls.push_back(url);
        return;
    }
    if (CefCurrentlyOn(TID_UI)) {
        CreateBrowserOnUI(url);
    } else {
        CefPostTask(TID_UI, base::BindOnce(&CreateBrowserOnUI, url));
    }
}

void SetSchedulePumpCallback(ScheduleMessagePumpWorkCallback cb) {
    g_schedule_pump_cb = cb;
}

}  // namespace exclr8cef
