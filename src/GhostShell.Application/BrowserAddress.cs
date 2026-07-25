using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace GhostShell.Application;

/// <summary>
/// An absolute address accepted by the desktop browser boundary.
/// </summary>
/// <remarks>
/// Embedded credentials are rejected so browser state, history, and audit
/// projections cannot accidentally carry URL user information.
/// </remarks>
public sealed record BrowserAddress
{
    public const int MaximumLength = 8_192;

    private static readonly Uri BlankUri = new("about:blank", UriKind.Absolute);

    [JsonConstructor]
    public BrowserAddress(Uri value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!IsSupported(value))
        {
            throw new ArgumentException(
                "A browser address must be an absolute HTTP(S) URL without embedded credentials, or about:blank.",
                nameof(value));
        }

        if (value.AbsoluteUri.Length > MaximumLength)
        {
            throw new ArgumentException(
                $"A browser address cannot exceed {MaximumLength} characters.",
                nameof(value));
        }

        Value = value;
    }

    public Uri Value { get; }

    public static BrowserAddress Blank { get; } = new(BlankUri);

    public static bool TryParse(
        string? text,
        [NotNullWhen(true)] out BrowserAddress? address)
    {
        address = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var candidate = text.Trim();
        if (candidate.Length > MaximumLength
            || !Uri.TryCreate(candidate, UriKind.Absolute, out var value)
            || !IsSupported(value)
            || value.AbsoluteUri.Length > MaximumLength)
        {
            return false;
        }

        address = new BrowserAddress(value);
        return true;
    }

    public override string ToString() => Value.AbsoluteUri;

    private static bool IsSupported(Uri value)
    {
        if (!value.IsAbsoluteUri)
        {
            return false;
        }

        if (value.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || value.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(value.Host)
                && string.IsNullOrEmpty(value.UserInfo);
        }

        return value.AbsoluteUri.Equals(
            BlankUri.AbsoluteUri,
            StringComparison.OrdinalIgnoreCase);
    }
}
