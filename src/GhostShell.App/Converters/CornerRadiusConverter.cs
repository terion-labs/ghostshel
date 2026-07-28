using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace GhostShell.App.Converters;

/// <summary>
/// Turns a slider's number into a <see cref="CornerRadius"/> so the corner-radius
/// setting can show the shape it will produce while it is being dragged.
/// </summary>
public sealed class CornerRadiusConverter : IValueConverter
{
    public static CornerRadiusConverter Instance { get; } = new();

    public object? Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) =>
        value is double radius && double.IsFinite(radius) && radius >= 0
            ? new CornerRadius(radius)
            : new CornerRadius(0);

    public object? ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) =>
        throw new NotSupportedException("The radius preview is one-way; bind the slider value.");
}
