// CefClient implementation. Handles browser lifecycle and display events.
// Pure CEF C++; not part of the public C ABI.

#ifndef EXCLR8CEF_CLIENT_H_
#define EXCLR8CEF_CLIENT_H_

#include <list>

#include "include/cef_client.h"

namespace exclr8cef {

class Exclr8CefClient : public CefClient,
                        public CefDisplayHandler,
                        public CefLifeSpanHandler,
                        public CefLoadHandler {
public:
    Exclr8CefClient();
    ~Exclr8CefClient() override;

    static Exclr8CefClient* GetInstance();

    CefRefPtr<CefDisplayHandler> GetDisplayHandler() override { return this; }
    CefRefPtr<CefLifeSpanHandler> GetLifeSpanHandler() override { return this; }
    CefRefPtr<CefLoadHandler> GetLoadHandler() override { return this; }

    void OnTitleChange(CefRefPtr<CefBrowser> browser,
                       const CefString& title) override;

    void OnAfterCreated(CefRefPtr<CefBrowser> browser) override;
    bool DoClose(CefRefPtr<CefBrowser> browser) override;
    void OnBeforeClose(CefRefPtr<CefBrowser> browser) override;

    void OnLoadError(CefRefPtr<CefBrowser> browser,
                     CefRefPtr<CefFrame> frame,
                     ErrorCode errorCode,
                     const CefString& errorText,
                     const CefString& failedUrl) override;

    bool IsClosing() const { return is_closing_; }

private:
    typedef std::list<CefRefPtr<CefBrowser>> BrowserList;
    BrowserList browser_list_;
    bool is_closing_ = false;

    IMPLEMENT_REFCOUNTING(Exclr8CefClient);
};

}  // namespace exclr8cef

#endif  // EXCLR8CEF_CLIENT_H_
