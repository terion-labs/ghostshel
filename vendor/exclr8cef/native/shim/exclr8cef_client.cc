#include "exclr8cef_client.h"

#include <sstream>
#include <string>

#include "include/cef_app.h"
#include "include/cef_parser.h"
#include "include/wrapper/cef_helpers.h"

namespace exclr8cef {

namespace {

Exclr8CefClient* g_instance = nullptr;

std::string GetDataURI(const std::string& data, const std::string& mime_type) {
    return "data:" + mime_type + ";base64," +
           CefURIEncode(CefBase64Encode(data.data(), data.size()), false)
               .ToString();
}

}  // namespace

Exclr8CefClient::Exclr8CefClient() {
    g_instance = this;
}

Exclr8CefClient::~Exclr8CefClient() {
    g_instance = nullptr;
}

// static
Exclr8CefClient* Exclr8CefClient::GetInstance() {
    return g_instance;
}

void Exclr8CefClient::OnTitleChange(CefRefPtr<CefBrowser> /*browser*/,
                                    const CefString& /*title*/) {
    CEF_REQUIRE_UI_THREAD();
}

void Exclr8CefClient::OnAfterCreated(CefRefPtr<CefBrowser> browser) {
    CEF_REQUIRE_UI_THREAD();
    browser_list_.push_back(browser);
}

bool Exclr8CefClient::DoClose(CefRefPtr<CefBrowser> /*browser*/) {
    CEF_REQUIRE_UI_THREAD();
    if (browser_list_.size() == 1) {
        is_closing_ = true;
    }
    return false;
}

void Exclr8CefClient::OnBeforeClose(CefRefPtr<CefBrowser> browser) {
    CEF_REQUIRE_UI_THREAD();
    for (auto it = browser_list_.begin(); it != browser_list_.end(); ++it) {
        if ((*it)->IsSame(browser)) {
            browser_list_.erase(it);
            break;
        }
    }
    if (browser_list_.empty()) {
        CefQuitMessageLoop();
    }
}

void Exclr8CefClient::OnLoadError(CefRefPtr<CefBrowser> /*browser*/,
                                  CefRefPtr<CefFrame> frame,
                                  ErrorCode errorCode,
                                  const CefString& errorText,
                                  const CefString& failedUrl) {
    CEF_REQUIRE_UI_THREAD();
    if (errorCode == ERR_ABORTED) return;

    std::stringstream ss;
    ss << "<html><body bgcolor=\"white\">"
       << "<h2>Failed to load URL " << std::string(failedUrl)
       << " with error " << std::string(errorText)
       << " (" << errorCode << ").</h2>"
       << "</body></html>";
    frame->LoadURL(GetDataURI(ss.str(), "text/html"));
}

}  // namespace exclr8cef
