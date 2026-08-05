using System.Globalization;
using Avalonia.Data.Converters;

namespace GhostShell.App.Converters;

/// <summary>
/// States a backdrop blur radius, including the end of the scale where it stops
/// being a radius at all. Zero is not "0px" of blur — it is the translucency
/// switched off, and the label should say the thing that happens.
/// </summary>
public sealed class BlurRadiusConverter : IValueConverter
{
    public static BlurRadiusConverter Instance { get; } = new();

    public object? Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) =>
        value switch
        {
            double radius => radius < 1 ? "None" : $"{radius:0}px",
            int radius => radius < 1 ? "None" : $"{radius}px",
            _ => string.Empty,
        };

    public object? ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) =>
        throw new NotSupportedException("Blur radius labels are display-only.");
}
