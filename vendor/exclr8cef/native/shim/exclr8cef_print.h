// Exclr8CEF — optional PrintToPDF extension.
//
// This header is OPT-IN. The core `exclr8cef.h` exposes the basic
// `excef_print_to_pdf(browser_id, path, callback)` for hosts that just
// want a default-styled PDF. Hosts that need full control over Chromium's
// PDF print settings (header/footer HTML templates, paper size, margins,
// scale, page ranges, ...) include THIS header alongside the core one.
//
// Implementation lives in `exclr8cef_print.cc` and is compiled into the
// same `libexclr8cef` shared library as the core shim — separation is
// at the API surface, not the binary. Apps that don't reference the
// matching managed `Exclr8Cef.Print` package never see this struct.

#ifndef EXCLR8CEF_PRINT_H_
#define EXCLR8CEF_PRINT_H_

#include "exclr8cef.h"  // for EXCEF_API + excef_pdf_done_callback_t

#ifdef __cplusplus
extern "C" {
#endif

// Mirrors CefPdfPrintSettings.
//
// Doubles default to 0.0 which CEF interprets as "use Chromium's defaults"
// (Letter paper, scale 1.0) — EXCEPT the margins, which use a negative
// sentinel for "not set" so that an explicit 0 can mean full bleed. Pointers may be NULL to
// indicate "no template / not set". Booleans are 0/1.
//
// header_template / footer_template are HTML strings rendered in the page
// margins. Special CSS classes inside them are auto-replaced at print time
// by Chromium:
//   <span class="pageNumber"></span>  — current page number
//   <span class="totalPages"></span>  — total page count
//   <span class="title"></span>       — document <title>
//   <span class="date"></span>        — formatted print date
//   <span class="url"></span>         — document URL
// display_header_footer must be 1 for the templates to render.
typedef struct excef_pdf_settings {
    int landscape;              // 0/1
    int print_background;       // 0/1
    int display_header_footer;  // 0/1
    int prefer_css_page_size;   // 0/1
    int generate_tagged_pdf;    // 0/1
    int generate_document_outline; // 0/1
    double scale;               // 0.0 = default (1.0)
    double paper_width;         // inches; 0.0 = default (Letter, 8.5)
    double paper_height;        // inches; 0.0 = default (Letter, 11)
    double margin_top;          // inches; < 0 = Chromium default (~0.4); >= 0 = custom (0 = full bleed)
    double margin_bottom;       // inches; same sentinel; all four must be >= 0 for custom margins to apply
    double margin_left;         // inches
    double margin_right;        // inches
    const char* page_ranges;    // e.g. "1-5,8" — NULL = all
    const char* header_template;// HTML — NULL = none
    const char* footer_template;// HTML — NULL = none
} excef_pdf_settings;

// Like excef_print_to_pdf but accepts the full Chromium PDF settings.
// Pass NULL `settings` for default behaviour (equivalent to the core
// `excef_print_to_pdf`).
EXCEF_API int excef_print_to_pdf_with_settings(
        int browser_id,
        const char* path,
        const excef_pdf_settings* settings,
        excef_pdf_done_callback_t callback);

#ifdef __cplusplus
}  // extern "C"
#endif

#endif  // EXCLR8CEF_PRINT_H_
