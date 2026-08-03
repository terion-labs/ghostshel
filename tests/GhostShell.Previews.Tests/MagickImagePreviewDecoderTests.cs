using GhostShell.Previews;

namespace GhostShell.Previews.Tests;

public sealed class MagickImagePreviewDecoderTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("ghostshell-image-preview").FullName;

    private readonly MagickImagePreviewDecoder _decoder = new();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("scan.tiff")]
    [InlineData("photo.HEIC")]
    [InlineData("layers.psd")]
    [InlineData("raw.cr3")]
    public void Formats_the_drawing_stack_cannot_open_are_claimed(string fileName)
    {
        Assert.True(_decoder.Claims(fileName));
    }

    [Theory]
    [InlineData("photo.png")]
    [InlineData("photo.jpg")]
    [InlineData("animation.gif")]
    [InlineData("photo.webp")]
    [InlineData("notes.txt")]
    [InlineData("")]
    public void Formats_the_drawing_stack_already_reads_are_left_alone(string fileName)
    {
        // Claiming these would make every ordinary image pay for a conversion
        // it does not need.
        Assert.False(_decoder.Claims(fileName));
    }

    [Fact]
    public async Task A_tiff_decodes_to_png_keeping_its_real_dimensions()
    {
        var path = Path.Combine(_root, "scan.tiff");
        await File.WriteAllBytesAsync(path, TestImages.Tiff(120, 90));

        var decoded = await _decoder.DecodeAsync(path, maximumPixels: 4_000_000, CancellationToken.None);

        Assert.NotNull(decoded);
        Assert.Equal(120, decoded!.Width);
        Assert.Equal(90, decoded.Height);
        Assert.Equal("Tiff", decoded.FormatName);
        Assert.True(decoded.PngBytes.Span.StartsWith(
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
    }

    [Fact]
    public async Task A_large_image_is_scaled_down_to_the_pixel_ceiling()
    {
        var path = Path.Combine(_root, "big.tiff");
        await File.WriteAllBytesAsync(path, TestImages.Tiff(400, 300));

        var decoded = await _decoder.DecodeAsync(path, maximumPixels: 10_000, CancellationToken.None);

        Assert.NotNull(decoded);
        // The reported size stays the file's own — the preview says what the
        // image is, not what it was shrunk to.
        Assert.Equal(400, decoded!.Width);
        Assert.Equal(300, decoded.Height);
        var rendered = TestImages.PngSize(decoded.PngBytes.Span);
        Assert.True(
            (long)rendered.Width * rendered.Height <= 10_000,
            $"rendered {rendered.Width}x{rendered.Height} exceeds the ceiling");
        // Proportional: a preview that reframes the picture is worse than a
        // smaller one.
        Assert.InRange(
            (double)rendered.Width / rendered.Height,
            400d / 300 * 0.9,
            400d / 300 * 1.1);
    }

    [Fact]
    public async Task A_file_that_is_not_an_image_decodes_to_nothing_rather_than_throwing()
    {
        var path = Path.Combine(_root, "broken.tiff");
        await File.WriteAllTextAsync(path, "this is not a TIFF at all");

        var decoded = await _decoder.DecodeAsync(path, maximumPixels: 4_000_000, CancellationToken.None);

        Assert.Null(decoded);
    }
}
