using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Exclr8Cef.Print;

/// <summary>
/// Optional Print/PDF extension for Exclr8Cef. Exposes the full
/// <c>CefPdfPrintSettings</c> surface: header / footer HTML templates,
/// paper size, margins, scale, page ranges, etc.
///
/// <para>Hosts that only need a default-styled PDF should call
/// <c>browser.PrintToPdfAsync(path)</c> on the <see cref="CefBrowser"/>
/// directly and don't need to reference this package.</para>
/// </summary>
public static class CefPrint
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ExcefPdfSettings
    {
        public int landscape;
        public int print_background;
        public int display_header_footer;
        public int prefer_css_page_size;
        public int generate_tagged_pdf;
        public int generate_document_outline;
        public double scale;
        public double paper_width;
        public double paper_height;
        public double margin_top;
        public double margin_bottom;
        public double margin_left;
        public double margin_right;
        public IntPtr page_ranges;
        public IntPtr header_template;
        public IntPtr footer_template;
    }

    [DllImport("exclr8cef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern unsafe int excef_print_to_pdf_with_settings(
        int browser_id,
        sbyte* path,
        ExcefPdfSettings* settings,
        delegate* unmanaged[Cdecl]<int, int, void> callback);

    // The done-callback infrastructure is private to Cef in the core library
    // (per-browser PdfQueue, PdfDoneTrampoline). Rather than duplicate that
    // machinery here, we mirror the same pattern: a per-browser FIFO of
    // completions. The native callback carries only the browser id, and
    // Chromium completes a browser's print jobs in submission order — so a
    // FIFO (NOT a single slot: a second concurrent print would overwrite
    // the first's completion and cross-resolve the tasks) maps each done
    // signal to the right caller.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, List<Action<int, int>>> s_pdfQueues = new();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void PdfDoneTrampoline(int browserId, int success)
    {
        Action<int, int>? cb = null;
        if (s_pdfQueues.TryGetValue(browserId, out var queue))
        {
            lock (queue)
            {
                if (queue.Count > 0)
                {
                    cb = queue[0];
                    queue.RemoveAt(0);
                }
            }
        }
        try { cb?.Invoke(browserId, success); }
        catch { }  // an escaping exception in [UnmanagedCallersOnly] fail-fasts the process
    }

    /// <summary>
    /// Render the browser's current page as a PDF at <paramref name="path"/>
    /// with explicit print settings. Returns a task that completes with
    /// <c>true</c> on success.
    /// </summary>
    public static Task<bool> PrintToPdfAsync(CefBrowser browser, string path, PdfPrintOptions options)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(options);

        int browserId = browser.Id;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<int, int> completion = (_, ok) => tcs.TrySetResult(ok != 0);
        var queue = s_pdfQueues.GetOrAdd(browserId, _ => new List<Action<int, int>>());
        // If the browser closes mid-print the native done-callback never
        // fires; remove our completion SYNCHRONOUSLY (a ContinueWith would
        // run later — the TCS uses RunContinuationsAsynchronously, which
        // overrides ExecuteSynchronously — leaving a dead head entry that a
        // concurrent print's done signal would consume) and fail the task.
        EventHandler closedHandler = (_, _) =>
        {
            lock (queue) { queue.Remove(completion); }
            s_pdfQueues.TryRemove(browserId, out _);  // ids are never reused
            tcs.TrySetResult(false);
        };
        browser.Closed += closedHandler;
        tcs.Task.ContinueWith(t => browser.Closed -= closedHandler,
            TaskContinuationOptions.ExecuteSynchronously);

        // Auto-enable header/footer rendering if either template is non-empty
        // unless the caller explicitly opted out.
        bool dhf = options.DisplayHeaderFooter
            ?? (!string.IsNullOrEmpty(options.HeaderTemplate)
                || !string.IsNullOrEmpty(options.FooterTemplate));

        unsafe
        {
            sbyte* pathPtr  = (sbyte*)Marshal.StringToCoTaskMemUTF8(path);
            IntPtr ranges   = options.PageRanges     is null ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(options.PageRanges);
            IntPtr header   = options.HeaderTemplate is null ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(options.HeaderTemplate);
            IntPtr footer   = options.FooterTemplate is null ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(options.FooterTemplate);
            try
            {
                var s = new ExcefPdfSettings
                {
                    landscape                 = options.Landscape ? 1 : 0,
                    print_background          = options.PrintBackground ? 1 : 0,
                    display_header_footer     = dhf ? 1 : 0,
                    prefer_css_page_size      = options.PreferCssPageSize ? 1 : 0,
                    generate_tagged_pdf       = options.GenerateTaggedPdf ? 1 : 0,
                    generate_document_outline = options.GenerateDocumentOutline ? 1 : 0,
                    scale         = options.Scale,
                    paper_width   = options.PaperWidthIn,
                    paper_height  = options.PaperHeightIn,
                    margin_top    = options.MarginTopIn,
                    margin_bottom = options.MarginBottomIn,
                    margin_left   = options.MarginLeftIn,
                    margin_right  = options.MarginRightIn,
                    page_ranges     = ranges,
                    header_template = header,
                    footer_template = footer,
                };

                delegate* unmanaged[Cdecl]<int, int, void> trampoline = &PdfDoneTrampoline;
                // Enqueue and submit under one lock so two concurrent
                // PrintToPdfAsync calls can't enqueue in the opposite order
                // of their native submissions (the FIFO maps done signals
                // to callers by position). The native call just posts the
                // print job — cheap enough to hold the lock across.
                int scheduled;
                lock (queue)
                {
                    queue.Add(completion);
                    scheduled = excef_print_to_pdf_with_settings(browserId, pathPtr, &s, trampoline);
                    if (scheduled == 0) queue.Remove(completion);
                }
                if (scheduled == 0)
                {
                    tcs.TrySetResult(false);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem((IntPtr)pathPtr);
                if (ranges != IntPtr.Zero) Marshal.FreeCoTaskMem(ranges);
                if (header != IntPtr.Zero) Marshal.FreeCoTaskMem(header);
                if (footer != IntPtr.Zero) Marshal.FreeCoTaskMem(footer);
            }
        }
        return tcs.Task;
    }
}

/// <summary>
/// PDF print options. Mirrors Chromium's <c>CefPdfPrintSettings</c>. All
/// fields are optional; <c>null</c> / 0.0 means "use Chromium's default".
///
/// <para><b>Header / footer templates.</b> When <see cref="DisplayHeaderFooter"/>
/// is <c>true</c> (auto-set if either template is non-empty), the HTML
/// strings render in the top / bottom page margins. Special CSS classes
/// inside the templates are auto-replaced at print time:
/// <list type="bullet">
///   <item><c>&lt;span class="pageNumber"&gt;&lt;/span&gt;</c> — current page</item>
///   <item><c>&lt;span class="totalPages"&gt;&lt;/span&gt;</c> — total pages</item>
///   <item><c>&lt;span class="title"&gt;&lt;/span&gt;</c> — document <c>&lt;title&gt;</c></item>
///   <item><c>&lt;span class="date"&gt;&lt;/span&gt;</c> — formatted print date</item>
///   <item><c>&lt;span class="url"&gt;&lt;/span&gt;</c> — document URL</item>
/// </list>
/// Use a wrapping <c>&lt;div&gt;</c> with <c>width:100%</c> + <c>text-align</c>
/// for alignment. Chromium's default font size for templates is ~8pt — set
/// inline styles for fidelity.</para>
/// </summary>
public sealed record PdfPrintOptions
{
    public bool Landscape { get; init; }
    public bool PrintBackground { get; init; }
    /// <summary>Render header/footer templates. Auto-true if either template is set.</summary>
    public bool? DisplayHeaderFooter { get; init; }
    /// <summary>Use the document's <c>@page</c> CSS rules instead of these dimensions.</summary>
    public bool PreferCssPageSize { get; init; }
    public bool GenerateTaggedPdf { get; init; }
    public bool GenerateDocumentOutline { get; init; }
    /// <summary>Scale factor (e.g. 0.8 for 80%). 0 = default 1.0.</summary>
    public double Scale { get; init; }
    /// <summary>Paper width in inches. 0 = default (Letter, 8.5).</summary>
    public double PaperWidthIn { get; init; }
    /// <summary>Paper height in inches. 0 = default (Letter, 11).</summary>
    public double PaperHeightIn { get; init; }
    /// <summary>Top margin in inches. 0 = Chromium default (~0.4).</summary>
    /// <summary>
    /// Margins in inches. Negative (the default) = keep Chromium's ~1cm
    /// defaults; any value >= 0 — including 0 for full bleed — is passed
    /// through as an explicit custom margin (all four must be set).
    /// </summary>
    public double MarginTopIn { get; init; } = -1.0;
    public double MarginBottomIn { get; init; } = -1.0;
    public double MarginLeftIn { get; init; } = -1.0;
    public double MarginRightIn { get; init; } = -1.0;
    /// <summary>Page ranges, e.g. "1-5,8". Empty/null = all pages.</summary>
    public string? PageRanges { get; init; }
    /// <summary>HTML template rendered above each page. See class summary.</summary>
    public string? HeaderTemplate { get; init; }
    /// <summary>HTML template rendered below each page. See class summary.</summary>
    public string? FooterTemplate { get; init; }

    /// <summary>A4 portrait, 8.27 × 11.69 inches.</summary>
    public static PdfPrintOptions A4 => new() { PaperWidthIn = 8.27, PaperHeightIn = 11.69 };
    /// <summary>US Letter portrait, 8.5 × 11 inches.</summary>
    public static PdfPrintOptions Letter => new() { PaperWidthIn = 8.5, PaperHeightIn = 11.0 };
}
