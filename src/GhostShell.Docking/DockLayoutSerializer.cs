using System.Text.Json.Serialization.Metadata;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Serializer.SystemTextJson;

[assembly: DockJsonSourceGeneration]
[assembly: DockJsonSerializable(typeof(Dock.Model.Inpc.Controls.RootDock))]
[assembly: DockJsonSerializable(typeof(Dock.Model.Inpc.Controls.ProportionalDock))]
[assembly: DockJsonSerializable(typeof(Dock.Model.Inpc.Controls.ProportionalDockSplitter))]
[assembly: DockJsonSerializable(typeof(Dock.Model.Inpc.Controls.DocumentDock))]
[assembly: DockJsonSerializable(typeof(Dock.Model.Inpc.Controls.Document))]
[assembly: DockJsonSerializable(typeof(Dock.Model.Inpc.Core.DockWindow))]

namespace GhostShell.Docking;

/// <summary>
/// Provides Dock's generated serializer without installing its generator into
/// the UI assembly, where GhostShell has unrelated System.Text.Json contexts.
/// </summary>
public static class DockLayoutSerializer
{
    public static DockSerializer Create() =>
        new(JsonTypeInfoResolver.WithAddedModifier(
            new DockSystemTextJsonResolver(),
            IgnoreRuntimeState));

    private static void IgnoreRuntimeState(JsonTypeInfo typeInfo)
    {
        if (!typeof(IDockable).IsAssignableFrom(typeInfo.Type))
        {
            return;
        }

        var ignored = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(IDockable.Owner),
        };
        if (typeof(IDock).IsAssignableFrom(typeInfo.Type))
        {
            ignored.Add(nameof(IDock.ActiveDockable));
            ignored.Add(nameof(IDock.DefaultDockable));
            ignored.Add(nameof(IDock.FocusedDockable));
            ignored.Add(nameof(IDock.IsActive));
        }

        if (typeof(IRootDock).IsAssignableFrom(typeInfo.Type))
        {
            ignored.Add(nameof(IRootDock.Window));
        }

        for (var index = typeInfo.Properties.Count - 1; index >= 0; index--)
        {
            if (ignored.Contains(typeInfo.Properties[index].Name))
            {
                typeInfo.Properties.RemoveAt(index);
            }
        }
    }
}
