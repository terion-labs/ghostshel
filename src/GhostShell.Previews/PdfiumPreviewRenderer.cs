using System.Runtime.Versioning;
using GhostShell.Application;
using PDFtoImage;
using SkiaSharp;

namespace GhostShell.Previews;

/// <summary>
/// Renders PDF pages with PDFium, the engine browsers use, so a document looks
/// the way its author expects rather than the way a reimplementation guesses.
///
/// PDFium ships native binaries per platform; the three GhostSHELL runs on are
/// declared here so the analyzer can hold callers to the same promise.
/// </summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("macOS")]
[SupportedOSPlatform("linux")]
public sealed class PdfiumPreviewRenderer : IPdfPreviewRenderer
{
    public bool Claims(string fileName) =>
        !string.IsNullOrWhiteSpace(fileName)
        && Path.GetExtension(fileName.Trim())
            .Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public ValueTask<int> CountPagesAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new ValueTask<int>(Task.Run(
            () =>
            {
                try
                {
                    using var stream = OpenRead(path);
                    return Conversion.GetPageCount(stream, leaveOpen: true);
                }
                catch (Exception exception) when (IsDocumentFailure(exception))
                {
                    // A file that will not open is a preview that cannot be
                    // shown, not a crash.
                    return 0;
                }
            },
            cancellationToken));
    }

    public ValueTask<PdfPageImage?> RenderPageAsync(
        string path,
        int pageIndex,
        int targetWidth,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetWidth);
        return new ValueTask<PdfPageImage?>(Task.Run<PdfPageImage?>(
            () =>
            {
                try
                {
                    using var stream = OpenRead(path);
                    // Left open deliberately: the default closes the stream,
                    // and the page render below needs the same file again.
                    var pageCount = Conversion.GetPageCount(stream, leaveOpen: true);
                    if (pageIndex >= pageCount)
                    {
                        return null;
                    }

                    stream.Position = 0;
                    cancellationToken.ThrowIfCancellationRequested();
                    using var bitmap = Conversion.ToImage(
                        stream,
                        leaveOpen: true,
                        password: null,
                        page: pageIndex,
                        options: new RenderOptions(Width: targetWidth));
                    using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                    return new PdfPageImage(encoded.ToArray(), pageIndex + 1, pageCount);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (IsDocumentFailure(exception))
                {
                    return null;
                }
            },
            cancellationToken));
    }

    /// <summary>
    /// Opened read-only and shared: a preview must never be the reason a user
    /// cannot open their own document elsewhere.
    /// </summary>
    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    /// <summary>
    /// A corrupt, encrypted, or simply not-a-PDF file fails inside PDFium, and
    /// every one of those is an ordinary outcome for a preview. The engine
    /// raises its own exception types, so the net is cast by what must not be
    /// swallowed — cancellation — rather than by listing what may be.
    /// </summary>
    private static bool IsDocumentFailure(Exception exception) =>
        exception is not OperationCanceledException;
}
