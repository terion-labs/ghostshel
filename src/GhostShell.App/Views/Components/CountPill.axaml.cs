using Avalonia;
using Avalonia.Controls;

namespace GhostShell.App.Views.Components;

public sealed partial class CountPill : UserControl
{
    public static readonly StyledProperty<object?> ValueProperty =
        AvaloniaProperty.Register<CountPill, object?>(nameof(Value));

    public CountPill()
    {
        InitializeComponent();
    }

    public object? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }
}
