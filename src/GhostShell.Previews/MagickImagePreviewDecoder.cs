using GhostShell.Application;
using ImageMagick;

namespace GhostShell.Previews;

/// <summary>
/// Decodes the image formats the drawing stack cannot, using ImageMagick.
///
/// The drawing stack already reads PNG, JPEG, GIF, BMP, and WebP, and does it
/// with less memory than a conversion would, so this deliberately claims only
/// what would otherwise fail to open at all.
/// </summary>
public sealed class MagickImagePreviewDecoder : IImagePreviewDecoder
{
    /// <summary>
    /// Formats worth claiming: camera, scanner, and document images a user will
    /// plausibly browse. The list is explicit rather than "everything Magick
    /// can read" because ImageMagick will happily open a PDF or a video frame,
    /// and those belong to their own previews.
    /// </summary>
    private static readonly HashSet<string> ClaimedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tif", ".tiff", ".heic", ".heif", ".avif", ".jxl",
        ".psd", ".xcf", ".ico", ".icns", ".tga", ".pcx", ".ppm", ".pgm", ".pbm", ".pnm",
        ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".raf", ".rw2", ".srw", ".pef",
        ".jp2", ".j2k", ".jpf", ".exr", ".hdr", ".dds", ".xpm", ".fits", ".fit",
    };

    /// <summary>
    /// A decode is bounded work on a hostile input, so it runs with an explicit
    /// pixel and memory ceiling rather than trusting the file's own header.
    /// </summary>
    private const int MaximumMegabytes = 256;

    public bool Claims(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var extension = Path.GetExtension(fileName.Trim());
        return !string.IsNullOrEmpty(extension) && ClaimedExtensions.Contains(extension);
    }

    public async ValueTask<DecodedImage?> DecodeAsync(
        FilePreviewContent content,
        long maximumPixels,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPixels);
        try
        {
            return await Task.Run(
                    () => Decode(content, maximumPixels, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is MagickException or IOException
            or UnauthorizedAccessException or OutOfMemoryException)
        {
            // A file that will not decode is a preview that cannot be shown,
            // never a reason to take the panel down with it.
            return null;
        }
    }

    private static DecodedImage? Decode(
        FilePreviewContent content,
        long maximumPixels,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = new MagickReadSettings
        {
            // A malformed or hostile image must not be able to spend the
            // machine's memory before it is even shown.
            Density = new Density(72),
        };
        ResourceLimits.Memory = (ulong)MaximumMegabytes * 1024 * 1024;

        using var source = content.OpenRead();
        using var image = new MagickImage(source, settings);
        var width = (int)image.Width;
        var height = (int)image.Height;
        var format = image.Format.ToString();

        cancellationToken.ThrowIfCancellationRequested();
        var pixels = (long)width * height;
        if (pixels > maximumPixels)
        {
            // Proportional, so the preview keeps the picture's shape; a preview
            // that reframes the image is worse than one that is merely smaller.
            var scale = Math.Sqrt((double)maximumPixels / pixels);
            image.Resize(
                (uint)Math.Max(1, (int)(width * scale)),
                (uint)Math.Max(1, (int)(height * scale)));
        }

        // Layered formats present as one flattened page, which is what a
        // preview of a document image means.
        image.Alpha(AlphaOption.Set);
        image.Format = MagickFormat.Png;
        cancellationToken.ThrowIfCancellationRequested();
        return new DecodedImage(image.ToByteArray(), width, height, format);
    }
}
