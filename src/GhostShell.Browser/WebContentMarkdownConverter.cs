using System.Text;
using ReverseMarkdown;
using SmartReader;

namespace GhostShell.Browser;

internal sealed class WebContentMarkdownConverter
{
    private const int MaximumReadabilityElements = 100_000;

    public async ValueTask<(string Title, string Markdown)> ConvertArticleAsync(
        Uri address,
        string renderedHtml,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(renderedHtml);
        using var reader = new Reader(address.AbsoluteUri, renderedHtml)
        {
            ContinueIfNotReadable = true,
            KeepClasses = false,
            MaxElemsToParse = MaximumReadabilityElements,
        };
        var article = await reader.GetArticleAsync(cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!article.Completed || string.IsNullOrWhiteSpace(article.Content))
        {
            throw new InvalidOperationException("Readability did not produce an article.");
        }

        return (article.Title ?? string.Empty, Convert(article.Content));
    }

    public string ConvertDocument(string renderedHtml)
    {
        ArgumentNullException.ThrowIfNull(renderedHtml);
        return Convert(renderedHtml);
    }

    private static string Convert(string html)
    {
        var config = new Config
        {
            Flavor = Config.MarkdownFlavor.Default,
            Formatting = { RemoveComments = true },
            Links = { SmartHref = true },
            Tags = { Unknown = Config.UnknownTagsOption.Bypass },
        };
        config.Preprocess
            .RemoveScripts()
            .RemoveStyles()
            .Remove("iframe, object, embed, form, input, button, dialog, template")
            .Remove("nav, footer, [hidden], [aria-hidden='true']")
            .Unwrap("span, font");
        return new Converter(config).Convert(html).Trim();
    }
}

internal static class BoundedWebText
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string Truncate(string value, int maximumBytes, out bool truncated)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (StrictUtf8.GetByteCount(value) <= maximumBytes)
        {
            truncated = false;
            return value;
        }

        var low = 0;
        var high = value.Length;
        while (low < high)
        {
            var middle = low + ((high - low + 1) / 2);
            if (char.IsHighSurrogate(value[middle - 1])
                && middle < value.Length
                && char.IsLowSurrogate(value[middle]))
            {
                middle--;
            }

            if (StrictUtf8.GetByteCount(value.AsSpan(0, middle)) <= maximumBytes)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        truncated = true;
        return value[..low];
    }
}
